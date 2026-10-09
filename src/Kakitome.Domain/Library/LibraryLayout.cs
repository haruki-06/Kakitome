namespace Kakitome.Domain.Library;

/// <summary>Canonical Library file and folder names (docs/05_LIBRARY_DATA.md).</summary>
public static class LibraryLayout
{
    public const string ProjectsFolder = "Projects";
    public const string MetadataFile = "metadata.json";
    public const string TranscriptMarkdownFile = "transcript.md";
    public const string TranscriptJsonFile = "transcript.json";
    public const string TranscriptTextFile = "transcript.txt";
    public const string SummaryMarkdownFile = "summary.md";
    public const string SummaryJsonFile = "summary.json";
    public const string SummaryTextFile = "summary.txt";

    /// <summary>Base name of the primary audio stream: <c>audio.&lt;ext&gt;</c>.</summary>
    public const string AudioBaseName = "audio";

    /// <summary>Default project for recordings created without choosing one.</summary>
    public const string DefaultProjectName = "Inbox";

    /// <summary>Prefix for in-progress atomic writes; such files are never canonical.</summary>
    public const string TempFilePrefix = ".kakitome-tmp-";

    /// <summary>
    /// File name for an audio stream. The primary stream is <c>audio.ext</c>; additional independent
    /// streams (e.g. system audio) are <c>audio.&lt;suffix&gt;.ext</c> so no source is forced into one mix.
    /// </summary>
    public static string AudioFileName(string extension, string? streamSuffix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var ext = extension.TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0 || ext.Any(c => !char.IsAsciiLetterOrDigit(c)))
        {
            throw new ArgumentException($"Invalid audio extension '{extension}'.", nameof(extension));
        }

        if (streamSuffix is null)
        {
            return $"{AudioBaseName}.{ext}";
        }

        if (streamSuffix.Length == 0 || streamSuffix.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new ArgumentException($"Invalid audio stream suffix '{streamSuffix}'.", nameof(streamSuffix));
        }

        return $"{AudioBaseName}.{streamSuffix.ToLowerInvariant()}.{ext}";
    }
}
