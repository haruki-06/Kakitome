using System.Globalization;
using System.Text;
using Kakitome.Application.Library;
using Kakitome.Domain.Library;
using Kakitome.Domain.Transcripts;

namespace Kakitome.Application.Asr;

/// <summary>Where a glossary applies: every recording, or one project.</summary>
/// <param name="Project">Project name, or null for the glossary shared by all projects.</param>
public sealed record GlossaryScope(string? Project)
{
    public static GlossaryScope Shared { get; } = new((string?)null);
}

/// <summary>State of one glossary file for the Settings page.</summary>
public sealed record GlossaryInfo(GlossaryScope Scope, string FullPath, bool Exists, int Terms, int Replacements);

/// <summary>Result of importing a glossary file.</summary>
public sealed record GlossaryImportResult(Glossary Glossary, string? PreservedPreviousFile);

/// <summary>
/// User glossaries in the Library (ADR-030): <c>Projects/glossary.txt</c> applies to every recording and
/// <c>Projects/&lt;Project&gt;/glossary.txt</c> to one project. They are ordinary user files: portable with the Library,
/// included in backups, editable with any text editor, and never overwritten without keeping the previous copy.
/// </summary>
public sealed class GlossaryService(ILibraryStore store)
{
    public const string FileName = "glossary.txt";

    /// <summary>A glossary is a short list; anything larger is almost certainly the wrong file.</summary>
    public const int MaxFileBytes = 512 * 1024;

    static GlossaryService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The project's glossary merged with the shared one (project entries win).</summary>
    public async Task<Glossary> LoadForProjectAsync(string? project, CancellationToken cancellationToken = default)
    {
        var shared = await LoadAsync(GlossaryScope.Shared, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(project))
        {
            return shared;
        }

        var own = await LoadAsync(new GlossaryScope(project), cancellationToken).ConfigureAwait(false);
        return Glossary.Merge(own, shared);
    }

    public async Task<Glossary> LoadAsync(GlossaryScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var bytes = await store.ReadFileAsync(FolderFor(scope), FileName, cancellationToken).ConfigureAwait(false);
        return bytes is null || bytes.Length > MaxFileBytes ? Glossary.Empty : Glossary.Parse(Decode(bytes));
    }

    public async Task<GlossaryInfo> GetInfoAsync(GlossaryScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var path = FullPathFor(scope);
        var exists = File.Exists(path);
        var glossary = exists ? await LoadAsync(scope, cancellationToken).ConfigureAwait(false) : Glossary.Empty;
        return new GlossaryInfo(scope, path, exists, glossary.Terms.Count, glossary.Replacements.Count);
    }

    /// <summary>
    /// Copies a user-chosen text file in as the glossary of <paramref name="scope"/>. The file is checked first; an
    /// existing glossary is kept as <c>glossary.&lt;date&gt;.txt</c> next to the new one.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is too large, not text, or has no entries.</exception>
    public async Task<GlossaryImportResult> ImportAsync(GlossaryScope scope, string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The glossary file was not found.", sourcePath);
        }

        if (info.Length > MaxFileBytes)
        {
            throw new InvalidDataException("tooLarge");
        }

        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (Array.IndexOf(bytes, (byte)0) >= 0 && !HasUtf16Bom(bytes))
        {
            throw new InvalidDataException("notText");
        }

        var text = Decode(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        var glossary = Glossary.Parse(text);
        if (glossary.IsEmpty)
        {
            throw new InvalidDataException("empty");
        }

        var folder = EnsureFolder(scope);
        string? preserved = null;
        if (store.ListFiles(folder).Contains(FileName, StringComparer.OrdinalIgnoreCase))
        {
            preserved = store.PreserveFile(folder, FileName, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));
        }

        await store.WriteFileAsync(folder, FileName, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray(), cancellationToken)
            .ConfigureAwait(false);
        return new GlossaryImportResult(glossary, preserved);
    }

    /// <summary>Creates the file with the format described in comments (for editing by hand) and returns its path.</summary>
    public async Task<string> EnsureFileAsync(GlossaryScope scope, bool japanese, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var folder = EnsureFolder(scope);
        if (!store.ListFiles(folder).Contains(FileName, StringComparer.OrdinalIgnoreCase))
        {
            var template = Glossary.Template(japanese).Replace("\n", "\r\n", StringComparison.Ordinal);
            await store.WriteFileAsync(folder, FileName, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(template)).ToArray(), cancellationToken)
                .ConfigureAwait(false);
        }

        return FullPathFor(scope);
    }

    /// <summary>Stops using the glossary. The file is kept, renamed to <c>glossary.removed-&lt;date&gt;.txt</c>.</summary>
    public string? Remove(GlossaryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var folder = FolderFor(scope);
        return store.FolderExists(folder) && store.ListFiles(folder).Contains(FileName, StringComparer.OrdinalIgnoreCase)
            ? store.PreserveFile(folder, FileName, "removed-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture))
            : null;
    }

    public string FullPathFor(GlossaryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Path.Combine(store.RootPath, FolderFor(scope).Replace('/', Path.DirectorySeparatorChar), FileName);
    }

    private static string FolderFor(GlossaryScope scope) => scope.Project is { Length: > 0 } project
        ? LibraryLayout.ProjectsFolder + "/" + LibraryNaming.SanitizeSegment(project, LibraryLayout.DefaultProjectName)
        : LibraryLayout.ProjectsFolder;

    private string EnsureFolder(GlossaryScope scope)
    {
        if (scope.Project is { Length: > 0 } project)
        {
            return store.EnsureProjectFolder(project);
        }

        Directory.CreateDirectory(Path.Combine(store.RootPath, LibraryLayout.ProjectsFolder));
        return LibraryLayout.ProjectsFolder;
    }

    private static bool HasUtf16Bom(byte[] b) => b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF));

    /// <summary>UTF-8 (with or without BOM) or UTF-16 with BOM; otherwise Shift_JIS, which older Japanese tools still write.</summary>
    internal static string Decode(byte[] bytes)
    {
        if (HasUtf16Bom(bytes))
        {
            return (bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes, 2, bytes.Length - 2);
        }

        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(932).GetString(bytes);
        }
    }
}
