using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kakitome.Storage.Persistence;

/// <summary>
/// Opens the app database, applying migrations. A corrupt or unreadable database is moved aside (never
/// deleted) and a fresh one is created; the caller then rebuilds the Library index from the files.
/// </summary>
public sealed partial class KakitomeDatabase(
    IDbContextFactory<KakitomeDbContext> contextFactory,
    DatabaseLocation location,
    TimeProvider timeProvider,
    ILogger<KakitomeDatabase> logger)
{
    /// <summary>SQLite result codes meaning "this file is not a usable database".</summary>
    private static readonly HashSet<int> CorruptionCodes = [11 /* SQLITE_CORRUPT */, 26 /* SQLITE_NOTADB */];

    private readonly Lock _initLock = new();
    private Task<DatabaseInitResult>? _initialization;

    public string DatabasePath => location.FilePath;

    /// <summary>
    /// Opens/migrates/recovers the database once per process; later calls (from any caller, e.g. the index before
    /// its first query) await the same result. A failed attempt is retried on the next call.
    /// </summary>
    public Task<DatabaseInitResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_initLock)
        {
            if (_initialization is null || _initialization.IsFaulted || _initialization.IsCanceled)
            {
                _initialization = InitializeCoreAsync();
            }

            return _initialization.WaitAsync(cancellationToken);
        }
    }

    private async Task<DatabaseInitResult> InitializeCoreAsync()
    {
        var cancellationToken = CancellationToken.None;
        Directory.CreateDirectory(Path.GetDirectoryName(location.FilePath)!);
        var existed = File.Exists(location.FilePath);
        string? quarantined = null;

        if (existed && !await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
        {
            quarantined = Quarantine();
        }

        try
        {
            await MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (quarantined is null && CorruptionCodes.Contains(ex.SqliteErrorCode))
        {
            LogCorrupt(ex, location.FilePath);
            quarantined = Quarantine();
            await MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        var needsRebuild = !existed || quarantined is not null;
        return new DatabaseInitResult(needsRebuild, quarantined);
    }

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(location.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            LogQuickCheckFailed(location.FilePath, result ?? "(null)");
            return false;
        }
        catch (SqliteException ex)
        {
            LogCorrupt(ex, location.FilePath);
            return false;
        }
    }

    /// <summary>Moves the database (and WAL/SHM side files) to <c>*.corrupt-&lt;time&gt;</c> for diagnosis.</summary>
    private string Quarantine()
    {
        // Only this database's pool: ClearAllPools would also close connections of other databases in the process.
        using (var connection = new SqliteConnection(location.ConnectionString))
        {
            SqliteConnection.ClearPool(connection);
        }
        var stamp = timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = $"{location.FilePath}.corrupt-{stamp}";
        File.Move(location.FilePath, target, overwrite: true);
        foreach (var side in new[] { "-wal", "-shm" })
        {
            var sidePath = location.FilePath + side;
            if (File.Exists(sidePath))
            {
                File.Move(sidePath, target + side, overwrite: true);
            }
        }

        LogQuarantined(location.FilePath, target);
        return target;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Database {Path} is corrupt")]
    private partial void LogCorrupt(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Database {Path} failed quick_check: {Result}")]
    private partial void LogQuickCheckFailed(string path, string result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Moved unusable database {Path} to {Target}; the index will be rebuilt from the Library")]
    private partial void LogQuarantined(string path, string target);
}

/// <param name="NeedsIndexRebuild">True for a new database or after corruption recovery.</param>
/// <param name="QuarantinedPath">Where a corrupt database was moved, if any.</param>
public sealed record DatabaseInitResult(bool NeedsIndexRebuild, string? QuarantinedPath);

public sealed class DatabaseLocation(string filePath)
{
    public string FilePath { get; } = Path.GetFullPath(filePath);

    public string ConnectionString => new SqliteConnectionStringBuilder { DataSource = FilePath, Cache = SqliteCacheMode.Private }.ToString();
}
