namespace Kakitome.Application.Library;

/// <summary>
/// Raw access to the canonical filesystem Library. Paths passed in are relative to the Library root and
/// are validated by the implementation; nothing outside the root can be read or written.
/// </summary>
public interface ILibraryStore
{
    /// <summary>Absolute path of the Library root.</summary>
    string RootPath { get; }

    /// <summary>Creates (if needed) the folder for a project and returns its relative path.</summary>
    string EnsureProjectFolder(string projectName);

    /// <summary>Creates a new, uniquely named recording folder and returns its relative path.</summary>
    string CreateRecordingFolder(string projectName, DateTimeOffset localTimestamp);

    /// <summary>
    /// Moves a whole recording folder into another project (renamed after that project, kept unique) and returns its
    /// new relative path. Nothing is copied or deleted: the folder is renamed in place on the same volume.
    /// </summary>
    string MoveRecordingFolder(string recordingFolder, string projectName, DateTimeOffset localTimestamp);

    /// <summary>Relative paths of every recording folder (<c>Projects/&lt;project&gt;/&lt;recording&gt;</c>).</summary>
    IReadOnlyList<string> EnumerateRecordingFolders();

    /// <summary>Folder names under <c>Projects/</c>.</summary>
    IReadOnlyList<string> EnumerateProjectFolders();

    bool FolderExists(string recordingFolder);

    /// <summary>File names directly inside a recording folder (temporary write files excluded).</summary>
    IReadOnlyList<string> ListFiles(string recordingFolder);

    /// <summary>Atomically replaces (or creates) a file: readers see either the old or the new content, never a mix.</summary>
    Task WriteFileAsync(string recordingFolder, string fileName, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Returns the file content, or null when the file does not exist.</summary>
    Task<byte[]?> ReadFileAsync(string recordingFolder, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Returns a fingerprint of the file, or null when it does not exist.</summary>
    Task<FileStamp?> GetStampAsync(string recordingFolder, string fileName, bool includeHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames an existing file inside the recording folder so a newer version can take its place without
    /// losing the old one. Returns the new file name.
    /// </summary>
    string PreserveFile(string recordingFolder, string fileName, string suffix);

    /// <summary>Deletes leftover temporary files from interrupted atomic writes. Never touches canonical files.</summary>
    int CleanupTemporaryFiles(TimeSpan olderThan);
}

/// <summary>Identity of a file's content at a point in time.</summary>
/// <param name="Sha256">Lower-case hex SHA-256, or null when hashing was skipped (large media).</param>
public sealed record FileStamp(long Length, DateTime LastWriteUtc, string? Sha256)
{
    /// <summary>True when both stamps describe the same content (hash wins when both have one).</summary>
    public bool SameContentAs(FileStamp? other)
    {
        if (other is null)
        {
            return false;
        }

        if (Sha256 is not null && other.Sha256 is not null)
        {
            return string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        return Length == other.Length && LastWriteUtc == other.LastWriteUtc;
    }
}
