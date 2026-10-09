using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Kakitome.Domain.Library;
using Kakitome.Domain.Recordings;
using Kakitome.Domain.Rendering;
using Kakitome.Domain.Serialization;
using Kakitome.Domain.Summaries;
using Kakitome.Domain.Transcripts;

namespace Kakitome.Application.Library;

/// <summary>
/// Reads and writes canonical Library content and keeps the derived index in step with it.
/// Invariants: Library files are the source of truth; nothing here deletes Library content; externally
/// edited files are never silently overwritten.
/// </summary>
public sealed partial class LibraryService(
    ILibraryStore store,
    ILibraryIndex index,
    IOptions<LibraryOutputOptions> outputOptions,
    TimeProvider timeProvider,
    ILogger<LibraryService> logger) : IDisposable
{
    /// <summary>Text artifacts whose content Kakitome tracks for external-change detection.</summary>
    private static readonly string[] TrackedFiles =
    [
        LibraryLayout.MetadataFile,
        LibraryLayout.TranscriptJsonFile,
        LibraryLayout.TranscriptMarkdownFile,
        LibraryLayout.TranscriptTextFile,
        LibraryLayout.SummaryJsonFile,
        LibraryLayout.SummaryMarkdownFile,
        LibraryLayout.SummaryTextFile,
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string LibraryRoot => store.RootPath;

    /// <summary>Raised after Library content or the index changed (recording id, or null for many). Any thread.</summary>
    public event EventHandler<LibraryChangedEventArgs>? Changed;

    private void RaiseChanged(RecordingId? id) => Changed?.Invoke(this, new LibraryChangedEventArgs(id));

    /// <summary>Absolute path of a recording folder from its index entry (no index lookup).</summary>
    public string GetAbsolutePath(RecordingIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Path.Combine(store.RootPath, entry.Folder.Replace('/', Path.DirectorySeparatorChar));
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Creates a recording folder with its initial <c>metadata.json</c>.</summary>
    public async Task<RecordingIndexEntry> CreateRecordingAsync(NewRecording request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = timeProvider.GetLocalNow();
        var project = string.IsNullOrWhiteSpace(request.Project) ? LibraryLayout.DefaultProjectName : request.Project.Trim();
        var timestamp = request.RecordedAt ?? now;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = store.CreateRecordingFolder(project, timestamp);
            var metadata = new RecordingMetadata
            {
                Id = RecordingId.New(),
                Title = string.IsNullOrWhiteSpace(request.Title) ? DefaultTitle(project, timestamp) : request.Title.Trim(),
                Project = project,
                Tags = NormalizeTags(request.Tags),
                CreatedAt = now,
                RecordedAt = request.RecordedAt,
                SourceType = request.SourceType,
                Capture = request.SourceType == RecordingSourceType.Recording
                    ? new CaptureInfo { Status = CaptureStatus.InProgress, StartedAt = timestamp }
                    : null,
                Import = request.Import,
                Language = request.Language,
                ProcessingProfile = request.ProcessingProfile,
            };

            await WriteTrackedAsync(metadata.Id, folder, LibraryLayout.MetadataFile, LibraryJson.Serialize(metadata), cancellationToken)
                .ConfigureAwait(false);
            var entry = BuildEntry(folder, metadata, hasExternalChanges: false);
            await index.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
            RaiseChanged(metadata.Id);
            LogCreated(metadata.Id, folder);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<RecordingIndexEntry>> ListRecordingsAsync(CancellationToken cancellationToken = default) =>
        index.ListAsync(cancellationToken);

    public Task<RecordingIndexEntry?> FindAsync(RecordingId id, CancellationToken cancellationToken = default) =>
        index.FindAsync(id, cancellationToken);

    /// <summary>Absolute path of a recording folder (for audio capture, the player and "open in Explorer").</summary>
    public async Task<string> GetRecordingPathAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        var entry = await RequireAsync(id, cancellationToken).ConfigureAwait(false);
        return Path.Combine(store.RootPath, entry.Folder);
    }

    public async Task<RecordingMetadata> GetMetadataAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        var entry = await RequireAsync(id, cancellationToken).ConfigureAwait(false);
        var bytes = await store.ReadFileAsync(entry.Folder, LibraryLayout.MetadataFile, cancellationToken).ConfigureAwait(false)
            ?? throw new LibraryFormatException($"'{entry.Folder}' has no {LibraryLayout.MetadataFile}.");
        return LibraryJson.DeserializeMetadata(bytes);
    }

    /// <summary>
    /// Read-modify-write of <c>metadata.json</c>. The current file on disk is always re-read first, so edits
    /// made outside Kakitome are merged rather than overwritten. Readable views are re-rendered afterwards.
    /// </summary>
    public async Task<RecordingMetadata> UpdateMetadataAsync(
        RecordingId id, Action<RecordingMetadata> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await RequireAsync(id, cancellationToken).ConfigureAwait(false);
            var bytes = await store.ReadFileAsync(entry.Folder, LibraryLayout.MetadataFile, cancellationToken).ConfigureAwait(false)
                ?? throw new LibraryFormatException($"'{entry.Folder}' has no {LibraryLayout.MetadataFile}.");
            var metadata = LibraryJson.DeserializeMetadata(bytes);
            mutate(metadata);
            if (metadata.Id != id)
            {
                throw new InvalidOperationException("The recording id cannot be changed.");
            }

            metadata.Tags = NormalizeTags(metadata.Tags);
            await WriteTrackedAsync(id, entry.Folder, LibraryLayout.MetadataFile, LibraryJson.Serialize(metadata), cancellationToken)
                .ConfigureAwait(false);
            await RerenderViewsAsync(entry.Folder, metadata, cancellationToken).ConfigureAwait(false);
            var updated = BuildEntry(entry.Folder, metadata, entry.HasExternalChanges);
            await index.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            RaiseChanged(id);
            return metadata;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TranscriptDocument?> LoadTranscriptAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        var entry = await RequireAsync(id, cancellationToken).ConfigureAwait(false);
        var bytes = await store.ReadFileAsync(entry.Folder, LibraryLayout.TranscriptJsonFile, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : LibraryJson.DeserializeTranscript(bytes);
    }

    public async Task<SummaryDocument?> LoadSummaryAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        var entry = await RequireAsync(id, cancellationToken).ConfigureAwait(false);
        var bytes = await store.ReadFileAsync(entry.Folder, LibraryLayout.SummaryJsonFile, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : LibraryJson.DeserializeSummary(bytes);
    }

    /// <summary>Writes <c>transcript.json</c> plus the configured readable formats.</summary>
    public async Task<SaveResult> SaveTranscriptAsync(
        TranscriptDocument transcript, ConflictPolicy policy = ConflictPolicy.Fail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return await SaveDocumentAsync(
            transcript.RecordingId,
            policy,
            metadata =>
            {
                var options = outputOptions.Value;
                var files = new List<(string, byte[])> { (LibraryLayout.TranscriptJsonFile, LibraryJson.Serialize(transcript)) };
                if (options.WriteMarkdown)
                {
                    files.Add((LibraryLayout.TranscriptMarkdownFile, Utf8(TranscriptRenderer.ToMarkdown(metadata, transcript))));
                }

                if (options.WriteText)
                {
                    files.Add((LibraryLayout.TranscriptTextFile, Utf8(TranscriptRenderer.ToPlainText(metadata, transcript))));
                }

                return files;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes <c>summary.json</c> plus the configured readable formats. The transcript is never touched.</summary>
    public async Task<SaveResult> SaveSummaryAsync(
        SummaryDocument summary, ConflictPolicy policy = ConflictPolicy.Fail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return await SaveDocumentAsync(
            summary.RecordingId,
            policy,
            metadata =>
            {
                var options = outputOptions.Value;
                var files = new List<(string, byte[])> { (LibraryLayout.SummaryJsonFile, LibraryJson.Serialize(summary)) };
                if (options.WriteMarkdown)
                {
                    files.Add((LibraryLayout.SummaryMarkdownFile, Utf8(SummaryRenderer.ToMarkdown(metadata, summary))));
                }

                if (options.WriteText)
                {
                    files.Add((LibraryLayout.SummaryTextFile, Utf8(SummaryRenderer.ToPlainText(metadata, summary))));
                }

                return files;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Discards the derived index and rebuilds it from the Library files alone. Used after SQLite loss or
    /// corruption and from Settings. Current file contents become the accepted baseline.
    /// </summary>
    public async Task<LibraryScanReport> RebuildIndexAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // No Clear first: rows are upserted/removed in place so readers never observe a transiently empty
            // index while the rebuild runs (e.g. a recording started during app startup).
            return await ScanAsync(acceptAllAsBaseline: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Incremental refresh at safe synchronization points (startup, Library refresh): picks up new, moved and
    /// removed folders and flags files edited outside Kakitome.
    /// </summary>
    public async Task<LibraryScanReport> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ScanAsync(acceptAllAsBaseline: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Accepts the current on-disk content of a recording's files as Kakitome's baseline (after the user chose
    /// "keep external edits"). Clears the external-change flag.
    /// </summary>
    public async Task AcceptExternalChangesAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await RequireAsync(id, cancellationToken).ConfigureAwait(false);
            foreach (var file in TrackedFiles)
            {
                var stamp = await store.GetStampAsync(entry.Folder, file, includeHash: true, cancellationToken).ConfigureAwait(false);
                await index.SetArtifactStampAsync(id, file, stamp, cancellationToken).ConfigureAwait(false);
            }

            await index.UpsertAsync(entry with { HasExternalChanges = false }, cancellationToken).ConfigureAwait(false);
            RaiseChanged(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes a recording folder that Kakitome just created but never used (e.g. the devices failed to start),
    /// only when it contains nothing except its own <c>metadata.json</c>. Returns false and keeps the folder otherwise.
    /// </summary>
    public async Task<bool> DiscardUnusedRecordingAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await index.FindAsync(id, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return false;
            }

            var files = store.ListFiles(entry.Folder);
            if (files.Count != 1 || files[0] != LibraryLayout.MetadataFile)
            {
                return false;
            }

            var folder = Path.Combine(store.RootPath, entry.Folder);
            File.Delete(Path.Combine(folder, LibraryLayout.MetadataFile));
            Directory.Delete(folder, recursive: false);
            await index.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
            RaiseChanged(id);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Moves a recording (its whole folder) into another project and records the new project in <c>metadata.json</c>.
    /// A title Kakitome generated from the old project name follows the new one; a user title is kept. Returns false
    /// when the recording is already in that project. The audio must not be open for writing.
    /// </summary>
    public async Task<bool> MoveToProjectAsync(RecordingId id, string project, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        var target = project.Trim();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await RequireAsync(id, cancellationToken).ConfigureAwait(false);
            var bytes = await store.ReadFileAsync(entry.Folder, LibraryLayout.MetadataFile, cancellationToken).ConfigureAwait(false)
                ?? throw new LibraryFormatException($"'{entry.Folder}' has no {LibraryLayout.MetadataFile}.");
            var metadata = LibraryJson.DeserializeMetadata(bytes);
            var current = string.IsNullOrWhiteSpace(metadata.Project) ? LibraryLayout.DefaultProjectName : metadata.Project;
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var timestamp = metadata.RecordedAt ?? metadata.CreatedAt;
            var folder = store.MoveRecordingFolder(entry.Folder, target, timestamp);
            if (string.Equals(metadata.Title, DefaultTitle(current, timestamp), StringComparison.Ordinal))
            {
                metadata.Title = DefaultTitle(target, timestamp);
            }

            metadata.Project = target;
            await WriteTrackedAsync(id, folder, LibraryLayout.MetadataFile, LibraryJson.Serialize(metadata), cancellationToken)
                .ConfigureAwait(false);
            await RerenderViewsAsync(folder, metadata, cancellationToken).ConfigureAwait(false);
            await index.UpsertAsync(BuildEntry(folder, metadata, entry.HasExternalChanges), cancellationToken).ConfigureAwait(false);
            RaiseChanged(id);
            LogMovedToProject(id, folder);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops a recording from the derived index after its folder was removed by an explicit user action
    /// (e.g. a cancelled recording moved to the Recycle Bin). Library files are not touched here.
    /// </summary>
    public async Task ForgetAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await index.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
            RaiseChanged(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<LibraryIssue>> ListIssuesAsync(CancellationToken cancellationToken = default) =>
        index.ListIssuesAsync(cancellationToken);

    private async Task<SaveResult> SaveDocumentAsync(
        RecordingId id,
        ConflictPolicy policy,
        Func<RecordingMetadata, List<(string FileName, byte[] Content)>> render,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await RequireAsync(id, cancellationToken).ConfigureAwait(false);
            var metadataBytes = await store.ReadFileAsync(entry.Folder, LibraryLayout.MetadataFile, cancellationToken).ConfigureAwait(false)
                ?? throw new LibraryFormatException($"'{entry.Folder}' has no {LibraryLayout.MetadataFile}.");
            var metadata = LibraryJson.DeserializeMetadata(metadataBytes);
            var files = render(metadata);

            var conflicts = new List<string>();
            foreach (var (fileName, content) in files)
            {
                if (await IsExternallyChangedAsync(id, entry.Folder, fileName, content, cancellationToken).ConfigureAwait(false))
                {
                    conflicts.Add(fileName);
                }
            }

            var preserved = new List<string>();
            if (conflicts.Count > 0)
            {
                if (policy == ConflictPolicy.Fail)
                {
                    LogConflict(id, string.Join(", ", conflicts));
                    return SaveResult.Blocked(conflicts);
                }

                var suffix = "conflict-" + timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                foreach (var file in conflicts)
                {
                    preserved.Add(store.PreserveFile(entry.Folder, file, suffix));
                }
            }

            // JSON first: it is the canonical structured copy; readable views follow.
            foreach (var (fileName, content) in files)
            {
                await WriteTrackedAsync(id, entry.Folder, fileName, content, cancellationToken).ConfigureAwait(false);
            }

            var updated = BuildEntry(entry.Folder, metadata, hasExternalChanges: false);
            await index.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            RaiseChanged(id);
            return new SaveResult(true, conflicts, preserved);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A file is externally changed when it exists on disk, differs from what Kakitome last wrote/accepted, and
    /// differs from what is about to be written.
    /// </summary>
    private async Task<bool> IsExternallyChangedAsync(
        RecordingId id, string folder, string fileName, byte[] newContent, CancellationToken cancellationToken)
    {
        var disk = await store.GetStampAsync(folder, fileName, includeHash: true, cancellationToken).ConfigureAwait(false);
        if (disk is null)
        {
            return false;
        }

        var known = await index.GetArtifactStampAsync(id, fileName, cancellationToken).ConfigureAwait(false);
        if (disk.SameContentAs(known))
        {
            return false;
        }

        return !string.Equals(disk.Sha256, ContentHash.Sha256Hex(newContent), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Brings every recording's reading versions (<c>transcript.txt</c>, <c>summary.txt</c>) up to date with the current
    /// renderer: creates missing ones and refreshes ones Kakitome wrote earlier. Files changed by the user are left alone.
    /// Runs at start-up; returns the number of files written.
    /// </summary>
    public async Task<int> RefreshReadableTextAsync(CancellationToken cancellationToken = default)
    {
        if (!outputOptions.Value.WriteText)
        {
            return 0;
        }

        var written = 0;
        foreach (var entry in await index.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var metadataBytes = await store.ReadFileAsync(entry.Folder, LibraryLayout.MetadataFile, cancellationToken).ConfigureAwait(false);
                if (metadataBytes is null)
                {
                    continue;
                }

                var metadata = LibraryJson.DeserializeMetadata(metadataBytes);
                if (await store.ReadFileAsync(entry.Folder, LibraryLayout.TranscriptJsonFile, cancellationToken).ConfigureAwait(false) is { } t
                    && await RefreshIfSafeAsync(metadata.Id, entry.Folder, LibraryLayout.TranscriptTextFile,
                        () => TranscriptRenderer.ToPlainText(metadata, LibraryJson.DeserializeTranscript(t)), cancellationToken).ConfigureAwait(false))
                {
                    written++;
                }

                if (await store.ReadFileAsync(entry.Folder, LibraryLayout.SummaryJsonFile, cancellationToken).ConfigureAwait(false) is { } s
                    && await RefreshIfSafeAsync(metadata.Id, entry.Folder, LibraryLayout.SummaryTextFile,
                        () => SummaryRenderer.ToPlainText(metadata, LibraryJson.DeserializeSummary(s)), cancellationToken).ConfigureAwait(false))
                {
                    written++;
                }
            }
            catch (LibraryFormatException)
            {
                // A damaged recording is reported by the scan; it does not stop the others.
            }
            finally
            {
                _gate.Release();
            }
        }

        return written;
    }

    private async Task<bool> RefreshIfSafeAsync(RecordingId id, string folder, string fileName, Func<string> render, CancellationToken cancellationToken)
    {
        var content = Utf8(render());
        var existing = await store.ReadFileAsync(folder, fileName, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.AsSpan().SequenceEqual(content))
        {
            return false;
        }

        // An existing file is replaced only when it is Kakitome's own earlier rendering, unchanged since: text files
        // used to be optional, so one Kakitome did not write is the user's and stays as it is.
        if (existing is not null
            && (!LooksLikeEarlierRendering(existing) || await IsExternallyChangedAsync(id, folder, fileName, content, cancellationToken).ConfigureAwait(false)))
        {
            return false;
        }

        await WriteTrackedAsync(id, folder, fileName, content, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Kakitome's own plain-text renderings: the earlier format (<c>[HH:MM:SS]</c> line stamps or the generated-by
    /// footer) and the reading format (second line "2026年10月3日 18:37 ・ …" / "October 3, 2026 18:37 · …").
    /// </summary>
    internal static bool LooksLikeEarlierRendering(byte[] content)
    {
        var text = System.Text.Encoding.UTF8.GetString(content);
        return EarlierStampRegex().IsMatch(text)
               || ReadingHeaderRegex().IsMatch(text)
               || text.Contains("Kakitome がローカルで生成", StringComparison.Ordinal)
               || text.Contains("Generated locally by Kakitome", StringComparison.Ordinal);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\A[^\n]*\n(\d{4}年\d{1,2}月\d{1,2}日 \d{1,2}:\d{2} ・ |[A-Z][a-z]+ \d{1,2}, \d{4} \d{1,2}:\d{2} · )")]
    private static partial System.Text.RegularExpressions.Regex ReadingHeaderRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?m)^\[\d{2}:\d{2}:\d{2}\] ")]
    private static partial System.Text.RegularExpressions.Regex EarlierStampRegex();

    private async Task RerenderViewsAsync(string folder, RecordingMetadata metadata, CancellationToken cancellationToken)
    {
        var options = outputOptions.Value;
        var transcriptBytes = await store.ReadFileAsync(folder, LibraryLayout.TranscriptJsonFile, cancellationToken).ConfigureAwait(false);
        if (transcriptBytes is not null)
        {
            var transcript = LibraryJson.DeserializeTranscript(transcriptBytes);
            await RerenderIfSafeAsync(metadata.Id, folder, options.WriteMarkdown, LibraryLayout.TranscriptMarkdownFile,
                () => TranscriptRenderer.ToMarkdown(metadata, transcript), cancellationToken).ConfigureAwait(false);
            await RerenderIfSafeAsync(metadata.Id, folder, options.WriteText, LibraryLayout.TranscriptTextFile,
                () => TranscriptRenderer.ToPlainText(metadata, transcript), cancellationToken).ConfigureAwait(false);
        }

        var summaryBytes = await store.ReadFileAsync(folder, LibraryLayout.SummaryJsonFile, cancellationToken).ConfigureAwait(false);
        if (summaryBytes is not null)
        {
            var summary = LibraryJson.DeserializeSummary(summaryBytes);
            await RerenderIfSafeAsync(metadata.Id, folder, options.WriteMarkdown, LibraryLayout.SummaryMarkdownFile,
                () => SummaryRenderer.ToMarkdown(metadata, summary), cancellationToken).ConfigureAwait(false);
            await RerenderIfSafeAsync(metadata.Id, folder, options.WriteText, LibraryLayout.SummaryTextFile,
                () => SummaryRenderer.ToPlainText(metadata, summary), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RerenderIfSafeAsync(
        RecordingId id, string folder, bool enabled, string fileName, Func<string> render, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            return;
        }

        var content = Utf8(render());
        if (await IsExternallyChangedAsync(id, folder, fileName, content, cancellationToken).ConfigureAwait(false))
        {
            // Leave the user's edited file alone; the external-change issue lets the UI offer a reconcile.
            LogConflict(id, fileName);
            return;
        }

        await WriteTrackedAsync(id, folder, fileName, content, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteTrackedAsync(RecordingId id, string folder, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        await store.WriteFileAsync(folder, fileName, content, cancellationToken).ConfigureAwait(false);
        var stamp = await store.GetStampAsync(folder, fileName, includeHash: true, cancellationToken).ConfigureAwait(false);
        await index.SetArtifactStampAsync(id, fileName, stamp, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LibraryScanReport> ScanAsync(bool acceptAllAsBaseline, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetLocalNow();
        var issues = new List<LibraryIssue>();
        var seen = new Dictionary<RecordingId, string>();
        var added = 0;
        var updated = 0;

        store.CleanupTemporaryFiles(TimeSpan.FromHours(1));

        foreach (var folder in store.EnumerateRecordingFolders())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadataBytes = await store.ReadFileAsync(folder, LibraryLayout.MetadataFile, cancellationToken).ConfigureAwait(false);
            if (metadataBytes is null)
            {
                issues.Add(new LibraryIssue(LibraryIssueKind.MissingMetadata, folder, LibraryLayout.MetadataFile,
                    "The folder has no metadata.json.", now));
                continue;
            }

            RecordingMetadata metadata;
            try
            {
                metadata = LibraryJson.DeserializeMetadata(metadataBytes);
            }
            catch (LibraryFormatException ex)
            {
                issues.Add(new LibraryIssue(LibraryIssueKind.UnreadableMetadata, folder, LibraryLayout.MetadataFile, ex.Message, now));
                continue;
            }

            if (seen.TryGetValue(metadata.Id, out var firstFolder))
            {
                issues.Add(new LibraryIssue(LibraryIssueKind.DuplicateId, folder, LibraryLayout.MetadataFile,
                    $"Same recording id as '{firstFolder}'.", now));
                continue;
            }

            seen[metadata.Id] = folder;
            var existing = await index.FindAsync(metadata.Id, cancellationToken).ConfigureAwait(false);
            var knownStamps = await index.GetArtifactStampsAsync(metadata.Id, cancellationToken).ConfigureAwait(false);
            var isNew = existing is null || acceptAllAsBaseline || knownStamps.Count == 0;
            var hasExternalChanges = false;

            foreach (var file in TrackedFiles)
            {
                var disk = await store.GetStampAsync(folder, file, includeHash: true, cancellationToken).ConfigureAwait(false);
                knownStamps.TryGetValue(file, out var known);

                if (isNew || IsJson(file))
                {
                    // New folders are accepted as found. JSON files are canonical: external edits are adopted.
                    if (!isNew && disk is not null && !disk.SameContentAs(known) && file != LibraryLayout.MetadataFile)
                    {
                        issues.Add(new LibraryIssue(LibraryIssueKind.ExternalChange, folder, file,
                            "Edited outside Kakitome; the edit was adopted.", now));
                    }

                    await index.SetArtifactStampAsync(metadata.Id, file, disk, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (disk is null)
                {
                    // The user removed a readable view; that is their choice. Forget it.
                    await index.SetArtifactStampAsync(metadata.Id, file, null, cancellationToken).ConfigureAwait(false);
                }
                else if (!disk.SameContentAs(known))
                {
                    // Keep the old stamp so the next write reports a conflict instead of overwriting.
                    hasExternalChanges = true;
                    issues.Add(new LibraryIssue(LibraryIssueKind.ExternalChange, folder, file,
                        "Edited outside Kakitome; it will not be overwritten without confirmation.", now));
                }
            }

            var entry = BuildEntry(folder, metadata, hasExternalChanges);
            if (existing is null)
            {
                added++;
            }
            else if (existing with { Tags = entry.Tags } != entry || !existing.Tags.SequenceEqual(entry.Tags))
            {
                updated++;
            }

            await index.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        var removed = 0;
        foreach (var stale in (await index.ListAsync(cancellationToken).ConfigureAwait(false)).Where(e => !seen.ContainsKey(e.Id)))
        {
            // Only the derived index row is removed; the folder is already gone from the Library.
            await index.RemoveAsync(stale.Id, cancellationToken).ConfigureAwait(false);
            removed++;
        }

        await index.ReplaceIssuesAsync(issues, cancellationToken).ConfigureAwait(false);
        LogScanned(seen.Count, added, updated, removed, issues.Count);
        RaiseChanged(null);
        return new LibraryScanReport(seen.Count, added, updated, removed, issues);
    }

    private RecordingIndexEntry BuildEntry(string folder, RecordingMetadata metadata, bool hasExternalChanges)
    {
        var files = store.ListFiles(folder);
        return new RecordingIndexEntry
        {
            Id = metadata.Id,
            Folder = folder,
            Title = metadata.Title,
            Project = metadata.Project,
            Tags = metadata.Tags,
            CreatedAt = metadata.CreatedAt,
            RecordedAt = metadata.RecordedAt,
            DurationSeconds = metadata.DurationSeconds,
            SourceType = metadata.SourceType,
            CaptureStatus = metadata.Capture?.Status,
            Language = metadata.Language,
            HasAudio = files.Any(f => f.StartsWith(LibraryLayout.AudioBaseName + ".", StringComparison.OrdinalIgnoreCase)),
            HasTranscript = files.Contains(LibraryLayout.TranscriptJsonFile, StringComparer.OrdinalIgnoreCase),
            HasSummary = files.Contains(LibraryLayout.SummaryJsonFile, StringComparer.OrdinalIgnoreCase),
            HasExternalChanges = hasExternalChanges,
        };
    }

    private async Task<RecordingIndexEntry> RequireAsync(RecordingId id, CancellationToken cancellationToken) =>
        await index.FindAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Recording {id} is not in the Library index.");

    private static bool IsJson(string fileName) => fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string DefaultTitle(string project, DateTimeOffset timestamp) =>
        $"{project} {timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";

    private static List<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? [])
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    [LoggerMessage(Level = LogLevel.Information, Message = "Created recording {Id} in {Folder}")]
    private partial void LogCreated(RecordingId id, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved recording {Id} to {Folder}")]
    private partial void LogMovedToProject(RecordingId id, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording {Id}: externally edited file(s) not overwritten: {Files}")]
    private partial void LogConflict(RecordingId id, string files);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Library scan: {Total} recordings ({Added} added, {Updated} updated, {Removed} removed), {Issues} issue(s)")]
    private partial void LogScanned(int total, int added, int updated, int removed, int issues);
}

/// <summary>Input for <see cref="LibraryService.CreateRecordingAsync"/>.</summary>
public sealed record NewRecording
{
    public string? Title { get; init; }

    public string? Project { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public RecordingSourceType SourceType { get; init; } = RecordingSourceType.Recording;

    /// <summary>Local wall-clock start time; also used for the folder name. Defaults to now.</summary>
    public DateTimeOffset? RecordedAt { get; init; }

    public ImportInfo? Import { get; init; }

    public string? Language { get; init; }

    public string? ProcessingProfile { get; init; }
}

public sealed record LibraryScanReport(int Total, int Added, int Updated, int Removed, IReadOnlyList<LibraryIssue> Issues);

public sealed class LibraryChangedEventArgs(RecordingId? recordingId) : EventArgs
{
    /// <summary>The recording that changed, or null when many may have changed (sync/rebuild).</summary>
    public RecordingId? RecordingId { get; } = recordingId;
}
