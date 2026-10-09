using Kakitome.Application.Maintenance;

namespace Kakitome.Storage;

/// <summary>
/// Executes a Factory Reset requested in Settings &gt; Maintenance. Runs at startup before the database, settings or
/// logs are opened, so nothing holds the files. Removes app state, indexes, jobs, models, cache and logs; the Library
/// (outside AppData) is never touched and is re-indexed on the following start-up scan.
/// </summary>
public static class FactoryReset
{
    /// <returns>Paths that could not be removed (empty on success); null when no reset was requested.</returns>
    public static IReadOnlyList<string>? RunIfRequested(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var marker = Path.Combine(paths.Root, MaintenanceService.FactoryResetMarker);
        if (!File.Exists(marker))
        {
            return null;
        }

        var problems = new List<string>();
        foreach (var dir in new[] { paths.DataDirectory, paths.CacheDirectory, paths.ModelsDirectory, paths.LogsDirectory })
        {
            TryDelete(() => Directory.Delete(dir, recursive: true), () => Directory.Exists(dir), dir, problems);
        }

        foreach (var file in Directory.EnumerateFiles(paths.Root, Path.GetFileName(paths.SettingsFile) + "*"))
        {
            TryDelete(() => File.Delete(file), () => File.Exists(file), file, problems);
        }

        // Always clear the request: a reset that partly failed must not loop on every start.
        TryDelete(() => File.Delete(marker), () => File.Exists(marker), marker, problems);
        return problems;
    }

    private static void TryDelete(Action delete, Func<bool> exists, string path, List<string> problems)
    {
        for (var attempt = 0; exists(); attempt++)
        {
            try
            {
                delete();
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                Thread.Sleep(200);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(path);
                return;
            }
        }
    }
}
