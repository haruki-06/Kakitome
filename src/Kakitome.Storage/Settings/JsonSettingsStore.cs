using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Kakitome.Application.Settings;

namespace Kakitome.Storage.Settings;

/// <summary>
/// <c>settings.json</c> in AppData, written atomically. An unreadable file is moved aside and defaults are used,
/// so a bad settings file can never prevent the app from starting.
/// </summary>
public sealed partial class JsonSettingsStore : ISettingsStore, IDisposable
{
    private static readonly SettingsJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    });

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current;

    public JsonSettingsStore(AppDataPaths paths, TimeProvider time, ILogger<JsonSettingsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.SettingsFile;
        _time = time;
        _logger = logger;
        _current = Load();
    }

    public AppSettings Current => Volatile.Read(ref _current).Clone();

    public event EventHandler? Changed;

    public async Task UpdateAsync(Action<AppSettings> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = _current.Clone();
            mutate(next);
            await SaveAsync(next, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, next);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var defaults = new AppSettings();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, defaults);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _gate.Dispose();

    private AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            // Editors such as Notepad may save a UTF-8 BOM, which Utf8JsonReader rejects.
            ReadOnlySpan<byte> bytes = File.ReadAllBytes(_path);
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                bytes = bytes[3..];
            }

            return JsonSerializer.Deserialize(bytes, Json.AppSettings) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            var aside = $"{_path}.corrupt-{_time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
            File.Move(_path, aside, overwrite: true);
            LogCorrupt(ex, aside);
            return new AppSettings();
        }
    }

    private async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Json.AppSettings);
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temp, _path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings file was unreadable and moved to {Path}; defaults are used")]
    private partial void LogCorrupt(Exception ex, string path);
}

[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
