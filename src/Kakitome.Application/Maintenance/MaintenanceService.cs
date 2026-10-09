using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Recording;
using Kakitome.Application.Settings;

namespace Kakitome.Application.Maintenance;

/// <summary>Where Kakitome keeps non-Library data (docs/06 "Data roots").</summary>
public interface IAppLocations
{
    /// <summary>Settings, database, logs (and by default Models/Cache below it).</summary>
    string AppDataRoot { get; }

    string CacheDirectory { get; }

    string ModelsDirectory { get; }

    /// <summary>Backup containers; user data, so it lives outside package-private storage (next to the Library).</summary>
    string BackupsDirectory { get; }

    /// <summary>True for the MSIX install: AppData/Cache/Models are package-private and removed by Windows on uninstall.</summary>
    bool IsPackaged { get; }
}

public sealed record StorageBreakdown(long Library, long Models, long Cache, long AppData, long Backups);

/// <summary>Choices in the uninstall flow. Library and Backups are kept unless the user explicitly removes them.</summary>
public sealed record UninstallChoices(bool RemoveLibrary, bool RemoveModels, bool RemoveBackups);

public sealed record UninstallResult(bool LibraryRemoved, bool ModelsRemoved, bool BackupsRemoved, IReadOnlyList<string> Problems);

/// <summary>
/// Settings &gt; Maintenance (docs/02, docs/06): storage usage, Clear Cache, Reset Config, Factory Reset (keeps the
/// Library) and the preparation step of the uninstall flow. Library data is only ever moved to the Recycle Bin, and
/// only when the user chose that explicitly.
/// </summary>
public sealed class MaintenanceService(
    IAppLocations locations,
    LibraryService library,
    ISettingsStore settings,
    IRecycleBin recycleBin,
    JobScheduler scheduler,
    RecordingService recording)
{
    /// <summary>Marker read at the next start (before the database opens) by the Factory Reset runner.</summary>
    public const string FactoryResetMarker = "factory-reset.pending";

    public Task<StorageBreakdown> MeasureAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var models = Size(locations.ModelsDirectory, cancellationToken);
        var cache = Size(locations.CacheDirectory, cancellationToken);
        var appData = Size(locations.AppDataRoot, cancellationToken)
                      - (IsInside(locations.ModelsDirectory, locations.AppDataRoot) ? models : 0)
                      - (IsInside(locations.CacheDirectory, locations.AppDataRoot) ? cache : 0);
        return new StorageBreakdown(
            Size(library.LibraryRoot, cancellationToken), models, cache, Math.Max(0, appData), Size(locations.BackupsDirectory, cancellationToken));
    }, cancellationToken);

    /// <summary>Deletes rebuildable data only. Files in use (e.g. a running download) are skipped. Returns bytes freed.</summary>
    public Task<long> ClearCacheAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var root = locations.CacheDirectory;
        if (!Directory.Exists(root))
        {
            return 0L;
        }

        long freed = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var length = new FileInfo(file).Length;
                File.Delete(file);
                freed += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use; it is cleaned up by its owner or on a later clear.
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use.
            }
        }

        return freed;
    }, cancellationToken);

    /// <summary>Resets application settings; the Library is not touched.</summary>
    public Task ResetConfigAsync(CancellationToken cancellationToken = default) => settings.ResetAsync(cancellationToken);

    /// <summary>
    /// Schedules a Factory Reset for the next start (the app restarts right after): app state, indexes, jobs, models,
    /// cache and logs are removed before the database is opened; the Library is kept and re-indexed.
    /// </summary>
    public async Task RequestFactoryResetAsync(CancellationToken cancellationToken = default)
    {
        EnsureIdle();
        Directory.CreateDirectory(locations.AppDataRoot);
        await File.WriteAllTextAsync(
            Path.Combine(locations.AppDataRoot, FactoryResetMarker),
            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the user's uninstall choices before Windows removes the app. Library and Backups go to the Recycle Bin
    /// (recoverable); models are deleted (re-downloadable). Nothing is removed unless chosen.
    /// </summary>
    public async Task<UninstallResult> PrepareUninstallAsync(UninstallChoices choices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choices);
        EnsureIdle();
        foreach (var job in (await scheduler.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                     .Where(j => !j.IsTerminal && j.State != JobState.Paused))
        {
            await scheduler.PauseAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }

        var problems = new List<string>();
        var libraryRemoved = choices.RemoveLibrary && Recycle(library.LibraryRoot, problems);
        var backupsRemoved = choices.RemoveBackups && Recycle(locations.BackupsDirectory, problems);
        var modelsRemoved = false;
        // Only on an explicit choice. In the MSIX package the option is informational: Windows removes the package's own
        // data, and a models folder that already existed outside the package (e.g. used by another Kakitome build) is
        // not the package's to delete.
        if (choices.RemoveModels)
        {
            try
            {
                if (Directory.Exists(locations.ModelsDirectory))
                {
                    Directory.Delete(locations.ModelsDirectory, recursive: true);
                }

                modelsRemoved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(ex.Message);
            }
        }

        return new UninstallResult(libraryRemoved, modelsRemoved, backupsRemoved, problems);
    }

    private bool Recycle(string folder, List<string> problems)
    {
        if (!Directory.Exists(folder))
        {
            return true;
        }

        if (recycleBin.TryMoveToRecycleBin(folder))
        {
            return true;
        }

        problems.Add(folder);
        return false;
    }

    private void EnsureIdle()
    {
        if (recording.Current is not null)
        {
            throw new InvalidOperationException("Stop the recording first.");
        }
    }

    internal static bool IsInside(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static long Size(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                total += file.Length;
            }
            catch (IOException)
            {
                // Vanished while measuring.
            }
        }

        return total;
    }
}
