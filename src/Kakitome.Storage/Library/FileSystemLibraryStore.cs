using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Kakitome.Application.Library;
using Kakitome.Domain.Library;

namespace Kakitome.Storage.Library;

/// <summary>
/// <see cref="ILibraryStore"/> over the local filesystem. All relative paths are resolved against the root and
/// rejected if they escape it. Writes are atomic (temp file + flush + replace in the same folder).
/// </summary>
public sealed partial class FileSystemLibraryStore : ILibraryStore
{
    /// <summary>Files larger than this are fingerprinted by size/time only (media is not hashed on every scan).</summary>
    public const long MaxHashedFileSize = 64L * 1024 * 1024;

    private readonly ILogger<FileSystemLibraryStore> _logger;
    private readonly Lock _createLock = new();

    public FileSystemLibraryStore(IOptions<LibraryLocationOptions> options, ILogger<FileSystemLibraryStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        var root = options.Value.RootPath;
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException($"Library root must be an absolute path: '{root}'.", nameof(options));
        }

        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Directory.CreateDirectory(Path.Combine(RootPath, LibraryLayout.ProjectsFolder));
    }

    public string RootPath { get; }

    public string EnsureProjectFolder(string projectName)
    {
        var name = LibraryNaming.SanitizeSegment(projectName, LibraryLayout.DefaultProjectName);
        var relative = LibraryLayout.ProjectsFolder + "/" + name;
        Directory.CreateDirectory(Resolve(relative));
        return relative;
    }

    public string CreateRecordingFolder(string projectName, DateTimeOffset localTimestamp)
    {
        var projectFolder = EnsureProjectFolder(projectName);
        var projectPath = Resolve(projectFolder);
        lock (_createLock)
        {
            var name = LibraryNaming.MakeUnique(
                LibraryNaming.RecordingDirectoryName(projectName, localTimestamp),
                candidate => Directory.Exists(Path.Combine(projectPath, candidate)) || File.Exists(Path.Combine(projectPath, candidate)));
            var relative = projectFolder + "/" + name;
            Directory.CreateDirectory(Resolve(relative));
            return relative;
        }
    }

    public string MoveRecordingFolder(string recordingFolder, string projectName, DateTimeOffset localTimestamp)
    {
        var source = Resolve(recordingFolder);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Recording folder not found: '{recordingFolder}'.");
        }

        var projectFolder = EnsureProjectFolder(projectName);
        var projectPath = Resolve(projectFolder);
        lock (_createLock)
        {
            var name = LibraryNaming.MakeUnique(
                LibraryNaming.RecordingDirectoryName(projectName, localTimestamp),
                candidate => Directory.Exists(Path.Combine(projectPath, candidate)) || File.Exists(Path.Combine(projectPath, candidate)));
            var relative = projectFolder + "/" + name;
            Directory.Move(source, Resolve(relative));
            LogMoved(recordingFolder, relative);
            return relative;
        }
    }

    public IReadOnlyList<string> EnumerateProjectFolders()
    {
        var projects = Path.Combine(RootPath, LibraryLayout.ProjectsFolder);
        if (!Directory.Exists(projects))
        {
            return [];
        }

        return new DirectoryInfo(projects).EnumerateDirectories()
            .Where(IsVisible)
            .Select(d => d.Name)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> EnumerateRecordingFolders()
    {
        var result = new List<string>();
        foreach (var project in EnumerateProjectFolders())
        {
            var projectDir = new DirectoryInfo(Path.Combine(RootPath, LibraryLayout.ProjectsFolder, project));
            try
            {
                foreach (var recording in projectDir.EnumerateDirectories().Where(IsVisible))
                {
                    result.Add($"{LibraryLayout.ProjectsFolder}/{project}/{recording.Name}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogEnumerateFailed(ex, projectDir.FullName);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    public bool FolderExists(string recordingFolder) => Directory.Exists(Resolve(recordingFolder));

    public IReadOnlyList<string> ListFiles(string recordingFolder)
    {
        var dir = new DirectoryInfo(Resolve(recordingFolder));
        if (!dir.Exists)
        {
            return [];
        }

        return dir.EnumerateFiles()
            .Where(f => !f.Name.StartsWith(LibraryLayout.TempFilePrefix, StringComparison.Ordinal))
            .Select(f => f.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task WriteFileAsync(
        string recordingFolder, string fileName, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var target = ResolveFile(recordingFolder, fileName);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"{LibraryLayout.TempFilePrefix}{Guid.NewGuid():N}-{fileName}");

        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await MoveWithRetryAsync(temp, target, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteTemp(temp);
            throw;
        }
    }

    public async Task<byte[]?> ReadFileAsync(string recordingFolder, string fileName, CancellationToken cancellationToken = default)
    {
        var path = ResolveFile(recordingFolder, fileName);

        // A concurrent atomic replace (ours or an editor's) can briefly deny access; retry like writes do.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous);
                await using (stream.ConfigureAwait(false))
                {
                    var buffer = new byte[stream.Length];
                    await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
                    return buffer;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 14)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50 * attempt, 500)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<FileStamp?> GetStampAsync(
        string recordingFolder, string fileName, bool includeHash, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(ResolveFile(recordingFolder, fileName));
        if (!info.Exists)
        {
            return null;
        }

        string? hash = null;
        if (includeHash && info.Length <= MaxHashedFileSize)
        {
            try
            {
                var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous);
                await using (stream.ConfigureAwait(false))
                {
                    hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                }
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        return new FileStamp(info.Length, info.LastWriteTimeUtc, hash);
    }

    public string PreserveFile(string recordingFolder, string fileName, string suffix)
    {
        var source = ResolveFile(recordingFolder, fileName);
        var directory = Path.GetDirectoryName(source)!;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var safeSuffix = LibraryNaming.SanitizeSegment(suffix, "preserved");

        var baseName = $"{stem}.{safeSuffix}";
        var uniqueBase = LibraryNaming.MakeUnique(baseName, candidate => File.Exists(Path.Combine(directory, candidate + extension)));
        var newName = uniqueBase + extension;
        File.Move(source, Path.Combine(directory, newName), overwrite: false);
        LogPreserved(fileName, newName, recordingFolder);
        return newName;
    }

    public int CleanupTemporaryFiles(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var deleted = 0;
        foreach (var folder in EnumerateRecordingFolders())
        {
            var dir = new DirectoryInfo(Resolve(folder));
            foreach (var file in dir.EnumerateFiles(LibraryLayout.TempFilePrefix + "*"))
            {
                if (file.LastWriteTimeUtc < cutoff && TryDeleteTemp(file.FullName))
                {
                    deleted++;
                }
            }
        }

        return deleted;
    }

    /// <summary>Resolves a Library-relative folder path, rejecting anything that escapes the root.</summary>
    internal string Resolve(string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        if (Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal))
        {
            throw new UnsafeLibraryPathException($"Absolute paths are not allowed: '{relative}'.");
        }

        var full = Path.GetFullPath(Path.Combine(RootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafeLibraryPathException($"Path escapes the Library root: '{relative}'.");
        }

        return full;
    }

    private string ResolveFile(string recordingFolder, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName != Path.GetFileName(fileName) || fileName is "." or ".." || fileName.Contains(':', StringComparison.Ordinal))
        {
            throw new UnsafeLibraryPathException($"Invalid file name: '{fileName}'.");
        }

        return Path.Combine(Resolve(recordingFolder), fileName);
    }

    private static bool IsVisible(DirectoryInfo dir) =>
        !dir.Name.StartsWith('.') && (dir.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;

    private static async Task MoveWithRetryAsync(string source, string target, CancellationToken cancellationToken)
    {
        // Editors, antivirus and the search indexer can briefly hold the target open; retry sharing violations
        // with capped backoff (~5 s in total) before giving up. The temp file is removed by the caller on failure.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 14)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50 * attempt, 500)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool TryDeleteTemp(string path)
    {
        try
        {
            if (Path.GetFileName(path).StartsWith(LibraryLayout.TempFilePrefix, StringComparison.Ordinal))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogTempDeleteFailed(ex, path);
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved recording folder {From} to {To}")]
    private partial void LogMoved(string from, string to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not enumerate {Path}")]
    private partial void LogEnumerateFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Preserved externally edited {File} as {NewName} in {Folder}")]
    private partial void LogPreserved(string file, string newName, string folder);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not delete temporary file {Path}")]
    private partial void LogTempDeleteFailed(Exception ex, string path);
}

public sealed class LibraryLocationOptions
{
    /// <summary>Absolute Library root. Defaults to <c>Documents\Kakitome\Library</c> (outside MSIX package storage).</summary>
    public string RootPath { get; set; } = DefaultRootPath;

    public static string DefaultRootPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kakitome", "Library");
}

public sealed class UnsafeLibraryPathException : Exception
{
    public UnsafeLibraryPathException()
    {
    }

    public UnsafeLibraryPathException(string message)
        : base(message)
    {
    }

    public UnsafeLibraryPathException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
