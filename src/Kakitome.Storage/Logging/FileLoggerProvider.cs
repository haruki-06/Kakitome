using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Kakitome.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Kakitome.Storage.Logging;

/// <summary>
/// Daily log files (<c>kakitome-yyyyMMdd.log</c>) in the app data Logs folder, for issue reports and field tests
/// (ADR-041). Lines are queued and written by one background task, so logging never blocks the UI or audio threads.
/// Kakitome's own messages from Information up, other libraries' from Warning; user names and personal folders are
/// replaced (<see cref="LogRedactor"/>). Files older than <see cref="RetentionDays"/> days are deleted, and a day's file
/// stops growing at <see cref="MaxBytesPerDay"/>.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int RetentionDays = 14;
    public const long MaxBytesPerDay = 20L * 1024 * 1024;
    public const string FilePrefix = "kakitome-";

    private readonly string _directory;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _writer;

    public FileLoggerProvider(string directory, string header)
    {
        _directory = directory;
        _writer = Task.Run(WriteLoopAsync);
        _queue.Writer.TryWrite(Format(DateTimeOffset.Now, LogLevel.Information, "Kakitome", header, null));
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(3));
    }

    internal static bool IsEnabled(string category, LogLevel level) =>
        level != LogLevel.None && level >= (category.StartsWith("Kakitome", StringComparison.Ordinal) ? LogLevel.Information : LogLevel.Warning);

    internal void Enqueue(string line) => _queue.Writer.TryWrite(line);

    internal static string Format(DateTimeOffset at, LogLevel level, string category, string message, Exception? exception)
    {
        var builder = new StringBuilder()
            .Append(at.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(level switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                _ => "CRT",
            }).Append("] ")
            .Append(category.StartsWith("Kakitome.", StringComparison.Ordinal) ? category[9..] : category)
            .Append(": ").Append(message);
        if (exception is not null)
        {
            builder.AppendLine().Append("    ").Append(exception.ToString().Replace("\n", "\n    ", StringComparison.Ordinal));
        }

        return LogRedactor.Redact(builder.ToString());
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            DeleteOld();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // logging is best effort
        }

        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            try
            {
                var path = Path.Combine(_directory, FilePrefix + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                var full = File.Exists(path) && new FileInfo(path).Length >= MaxBytesPerDay;
                await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                while (reader.TryRead(out var line))
                {
                    if (!full)
                    {
                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(500).ConfigureAwait(false); // locked by an editor or a scanner: try again with the next lines
            }
        }
    }

    private void DeleteOld()
    {
        var cutoff = DateTime.Now.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, FilePrefix + "*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => FileLoggerProvider.IsEnabled(category, logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                owner.Enqueue(Format(DateTimeOffset.Now, logLevel, category, formatter(state, exception), exception));
            }
        }
    }
}
