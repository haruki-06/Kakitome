using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Maintenance;
using Kakitome.Application.Settings;
using Kakitome.Domain.Library;

namespace Kakitome.Application.Backup;

/// <summary>
/// <c>manifest.json</c> of a <c>.kakitome-backup</c> container (docs/06 "Backup"): a ZIP with the canonical Library
/// files under <c>Library/</c>. Versioned so future releases can read older backups. Models are not included.
/// </summary>
public sealed class BackupManifest
{
    public const string FormatName = "kakitome-backup";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;

    public int Version { get; set; } = CurrentVersion;

    public required DateTimeOffset CreatedAt { get; set; }

    public string? AppVersion { get; set; }

    /// <summary><c>manual</c> or <c>automatic</c> (automatic ones are rotated).</summary>
    public string Kind { get; set; } = "manual";

    public List<BackupRecording> Recordings { get; set; } = [];

    public List<BackupFile> Files { get; set; } = [];
}

public sealed class BackupRecording
{
    public required string Id { get; set; }

    /// <summary>Folder relative to the Library root, '/'-separated.</summary>
    public required string Folder { get; set; }

    public string? Title { get; set; }
}

public sealed class BackupFile
{
    /// <summary>Path relative to the Library root, '/'-separated.</summary>
    public required string Path { get; set; }

    public long Size { get; set; }

    public required string Sha256 { get; set; }
}

public sealed record BackupResult(string FilePath, int Recordings, long Bytes);

public sealed record RestoreResult(int Restored, int SkippedExisting, IReadOnlyList<string> RestoredAsCopies);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;

/// <summary>Creates and restores Library backups. Restore never overwrites anything already in the Library.</summary>
public sealed class BackupService(LibraryService library, IAppLocations locations, TimeProvider time)
{
    public const string Extension = ".kakitome-backup";
    private const string ManifestEntry = "manifest.json";
    private const string LibraryPrefix = "Library/";
    private static readonly string[] Compressed = [".m4a", ".mp3", ".aac", ".flac", ".ogg", ".opus", ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".wma", ".wmv", ".avi"];

    /// <summary>Writes a backup of the whole Library into <paramref name="directory"/> (via a .partial file).</summary>
    public async Task<BackupResult> CreateAsync(string directory, string kind, string? appVersion, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var root = library.LibraryRoot;
        Directory.CreateDirectory(directory);
        var name = string.Create(CultureInfo.InvariantCulture, $"Kakitome_{time.GetLocalNow():yyyy-MM-dd_HH-mm-ss}{(kind == "automatic" ? "_auto" : string.Empty)}{Extension}");
        var target = Path.Combine(directory, name);
        var partial = target + ".partial";

        var files = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(f => !Path.GetFileName(f).StartsWith(LibraryLayout.TempFilePrefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];
        var total = Math.Max(1, files.Sum(f => new FileInfo(f).Length));
        var manifest = new BackupManifest { CreatedAt = time.GetLocalNow(), AppVersion = appVersion, Kind = kind };
        foreach (var entry in await library.ListRecordingsAsync(cancellationToken).ConfigureAwait(false))
        {
            manifest.Recordings.Add(new BackupRecording { Id = entry.Id.ToString(), Folder = Relative(root, Path.Combine(root, entry.Folder)), Title = entry.Title });
        }

        try
        {
            var stream = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    long done = 0;
                    foreach (var file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = Relative(root, file);
                        var level = Compressed.Contains(Path.GetExtension(file).ToLowerInvariant()) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                        var entry = zip.CreateEntry(LibraryPrefix + relative, level);
                        entry.LastWriteTime = File.GetLastWriteTime(file);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        long size = 0;
                        var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using (input.ConfigureAwait(false))
                        {
                            var output = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                            await using (output.ConfigureAwait(false))
                            {
                                var buffer = new byte[1 << 20];
                                int read;
                                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                                {
                                    hash.AppendData(buffer, 0, read);
                                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                                    size += read;
                                    done += read;
                                    progress?.Report(Math.Min(0.99, done / (double)total));
                                }
                            }
                        }

                        manifest.Files.Add(new BackupFile { Path = relative, Size = size, Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()) });
                    }

                    var manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                    var manifestStream = await manifestEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await using (manifestStream.ConfigureAwait(false))
                    {
                        await JsonSerializer.SerializeAsync(manifestStream, manifest, BackupJsonContext.Default.BackupManifest, cancellationToken).ConfigureAwait(false);
                    }
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // A backup is only published after it reads back completely.
            await VerifyAsync(partial, cancellationToken).ConfigureAwait(false);
            File.Move(partial, target, overwrite: false);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        progress?.Report(1);
        return new BackupResult(target, manifest.Recordings.Count, new FileInfo(target).Length);
    }

    /// <summary>Reads the manifest and checks every file's size and SHA-256.</summary>
    public static async Task<BackupManifest> VerifyAsync(string backupFile, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(backupFile);
        var manifest = await ReadManifestAsync(zip, cancellationToken).ConfigureAwait(false);
        foreach (var file in manifest.Files)
        {
            var entry = zip.GetEntry(LibraryPrefix + file.Path) ?? throw new InvalidDataException($"The backup is missing {file.Path}.");
            if (entry.Length != file.Size)
            {
                throw new InvalidDataException($"{file.Path} in the backup has the wrong size.");
            }

            var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"{file.Path} in the backup is damaged.");
                }
            }
        }

        return manifest;
    }

    /// <summary>
    /// Restores recordings that are not in the Library. Existing recordings (same id) are skipped; a folder name that
    /// is taken by something else gets a " (restored)" suffix. Nothing in the Library is overwritten.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(string backupFile, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var manifest = await VerifyAsync(backupFile, cancellationToken).ConfigureAwait(false);
        var root = library.LibraryRoot;
        var existing = (await library.ListRecordingsAsync(cancellationToken).ConfigureAwait(false)).Select(e => e.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var zip = ZipFile.OpenRead(backupFile);
        var restored = 0;
        var skipped = 0;
        var copies = new List<string>();
        for (var i = 0; i < manifest.Recordings.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recording = manifest.Recordings[i];
            if (existing.Contains(recording.Id))
            {
                skipped++;
                continue;
            }

            var folder = SafeRelative(recording.Folder);
            var destination = Path.Combine(root, folder);
            for (var n = 1; Directory.Exists(destination) || File.Exists(destination); n++)
            {
                destination = Path.Combine(root, folder + (n == 1 ? " (restored)" : $" (restored {n})"));
            }

            if (!string.Equals(destination, Path.Combine(root, folder), StringComparison.OrdinalIgnoreCase))
            {
                copies.Add(Relative(root, destination));
            }

            // Extract into a temporary sibling, then rename: a half-restored recording never appears in the Library.
            var staging = destination + ".restoring";
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            var prefix = recording.Folder.TrimEnd('/') + "/";
            foreach (var file in manifest.Files.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var path = Path.Combine(staging, SafeRelative(file.Path[prefix.Length..]));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var entry = zip.GetEntry(LibraryPrefix + file.Path)!;
                var input = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (input.ConfigureAwait(false))
                {
                    var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
                    await using (output.ConfigureAwait(false))
                    {
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }
                }

                File.SetLastWriteTime(path, entry.LastWriteTime.DateTime);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
            restored++;
            progress?.Report((i + 1) / (double)Math.Max(1, manifest.Recordings.Count));
        }

        await library.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return new RestoreResult(restored, skipped, copies);
    }

    /// <summary>Automatic backups: keep the newest <paramref name="keep"/>; manual backups are never touched.</summary>
    public int RotateAutomatic(int keep)
    {
        var dir = locations.BackupsDirectory;
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        var old = Directory.EnumerateFiles(dir, "Kakitome_*_auto" + Extension).Order(StringComparer.Ordinal).Reverse().Skip(keep).ToList();
        foreach (var file in old)
        {
            File.Delete(file);
        }

        return old.Count;
    }

    /// <summary>Newest backup (manual or automatic) in the backups folder, by file time.</summary>
    public DateTimeOffset? LastBackupAt()
    {
        var dir = locations.BackupsDirectory;
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*" + Extension).Select(f => (DateTimeOffset?)File.GetLastWriteTime(f)).Max()
            : null;
    }

    private static async Task<BackupManifest> ReadManifestAsync(ZipArchive zip, CancellationToken cancellationToken)
    {
        var entry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("This is not a Kakitome backup (no manifest).");
        var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            BackupManifest? manifest;
            try
            {
                manifest = await JsonSerializer.DeserializeAsync(stream, BackupJsonContext.Default.BackupManifest, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The backup manifest is unreadable.", ex);
            }

            if (manifest is null || manifest.Format != BackupManifest.FormatName)
            {
                throw new InvalidDataException("This is not a Kakitome backup.");
            }

            if (manifest.Version > BackupManifest.CurrentVersion)
            {
                throw new InvalidDataException("This backup was made by a newer Kakitome. Update Kakitome to restore it.");
            }

            foreach (var file in manifest.Files)
            {
                _ = SafeRelative(file.Path);
            }

            return manifest;
        }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>Rejects absolute paths and "..": a crafted backup can never write outside the Library.</summary>
    internal static string SafeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsafe path in backup: {path}");
        }

        var parts = path.Split('/', '\\');
        if (parts.Any(p => p is "" or "." or ".."))
        {
            throw new InvalidDataException($"Unsafe path in backup: {path}");
        }

        return Path.Combine(parts);
    }
}

/// <summary><c>backup.create</c>: durable, cancellable backup into the backups folder.</summary>
public sealed class BackupJobHandler(BackupService backups, IAppLocations locations, ISettingsStore settings) : IJobHandler
{
    public const string JobKind = "backup.create";
    public const int AutomaticToKeep = 3;

    public string Kind => JobKind;

    public JobResourceClass ResourceClass => JobResourceClass.Light;

    public static string PayloadFor(string kind, string? appVersion) => JsonSerializer.Serialize(new Payload(kind, appVersion));

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = context.Job.Payload is { } p ? JsonSerializer.Deserialize<Payload>(p) : null;
        var kind = payload?.Kind == "automatic" ? "automatic" : "manual";
        if (kind == "automatic" && !settings.Current.Backup.Automatic)
        {
            await context.SetEngineAsync("skipped (automatic backups are off)").ConfigureAwait(false);
            return;
        }

        var last = -1.0;
        var progress = new Progress<double>(v =>
        {
            if (v - last >= 0.02 || v >= 1)
            {
                last = v;
                _ = context.ReportProgressAsync(v);
            }
        });

        try
        {
            await backups.CreateAsync(locations.BackupsDirectory, kind, payload?.AppVersion, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new TransientJobException($"The backup could not be written: {ex.Message}", ex);
        }

        if (kind == "automatic")
        {
            backups.RotateAutomatic(AutomaticToKeep);
        }

        await context.SetEngineAsync(".kakitome-backup v" + BackupManifest.CurrentVersion).ConfigureAwait(false);
    }

    private sealed record Payload(string Kind, string? AppVersion);
}

/// <summary><c>backup.restore</c>: restores recordings missing from the Library from a backup file.</summary>
public sealed class RestoreJobHandler(BackupService backups) : IJobHandler
{
    public const string JobKind = "backup.restore";

    public string Kind => JobKind;

    public JobResourceClass ResourceClass => JobResourceClass.Light;

    public static string PayloadFor(string file) => JsonSerializer.Serialize(new Payload(file));

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var file = context.Job.Payload is { } p ? JsonSerializer.Deserialize<Payload>(p)?.File : null;
        if (file is null || !File.Exists(file))
        {
            throw new PermanentJobException("The backup file is no longer available.");
        }

        RestoreResult result;
        try
        {
            result = await backups.RestoreAsync(file, new Progress<double>(v => _ = context.ReportProgressAsync(v)), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new PermanentJobException(ex.Message, ex);
        }

        await context.SetEngineAsync(string.Create(CultureInfo.InvariantCulture, $"restored {result.Restored}, already present {result.SkippedExisting}")).ConfigureAwait(false);
    }

    private sealed record Payload(string File);
}

/// <summary>Queues an automatic backup when one is due (checked at start-up and every few hours).</summary>
public sealed class AutomaticBackupPolicy(BackupService backups, JobScheduler scheduler, ISettingsStore settings, TimeProvider time)
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    public async Task<bool> EnqueueIfDueAsync(string? appVersion, CancellationToken cancellationToken = default)
    {
        var backup = settings.Current.Backup;
        if (!backup.Automatic)
        {
            return false;
        }

        var last = backups.LastBackupAt();
        if (last is { } at && time.GetLocalNow() - at < TimeSpan.FromDays(Math.Max(1, backup.IntervalDays)))
        {
            return false;
        }

        var active = (await scheduler.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .Any(j => j.Kind == BackupJobHandler.JobKind && !j.IsTerminal);
        if (active)
        {
            return false;
        }

        await scheduler.EnqueueAsync(new JobRequest(BackupJobHandler.JobKind)
        {
            Payload = BackupJobHandler.PayloadFor("automatic", appVersion),
            Priority = -10,
        }, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
