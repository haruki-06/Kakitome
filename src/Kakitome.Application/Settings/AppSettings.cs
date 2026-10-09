using Kakitome.Application.Jobs;
using Kakitome.Application.Recording;

namespace Kakitome.Application.Settings;

/// <summary>
/// User preferences (AppData, resettable by "Reset Config"). Never contains secrets or Library content.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public RecordingSettings Recording { get; set; } = new();

    public ProcessingSettings Processing { get; set; } = new();

    public GeneralSettings General { get; set; } = new();

    public BackupSettings Backup { get; set; } = new();

    public AppSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Recording = Recording with { },
        Processing = Processing with { },
        General = General with { },
        Backup = Backup with { },
    };
}

public sealed record RecordingSettings
{
    public string? DefaultProject { get; set; }

    public bool IncludeMicrophone { get; set; } = true;

    /// <summary>Null = follow the Windows default microphone.</summary>
    public string? MicrophoneId { get; set; }

    /// <summary>System audio is off by default (docs/03).</summary>
    public bool IncludeSystemAudio { get; set; }

    public string? SystemAudioDeviceId { get; set; }

    public bool PreventSleep { get; set; } = true;

    /// <summary>Global start/stop hotkey, e.g. <c>Ctrl+Alt+Shift+R</c>. Empty disables it.</summary>
    public string ToggleHotkey { get; set; } = HotkeyGesture.DefaultToggle;

    /// <summary>Closing the window while recording keeps recording in the notification area.</summary>
    public bool KeepRecordingWhenWindowClosed { get; set; } = true;

    /// <summary>Show a live (preview) transcript while recording; the final transcript is produced afterwards.</summary>
    public bool LiveTranscript { get; set; } = true;

    /// <summary>How finished recordings keep their audio (<see cref="Audio.RetentionPolicy"/>); default lossless.</summary>
    public string Retention { get; set; } = Audio.RetentionPolicy.Raw;

    public RecordingOptions ToOptions() => new()
    {
        Project = DefaultProject,
        IncludeMicrophone = IncludeMicrophone,
        MicrophoneId = MicrophoneId,
        IncludeSystemAudio = IncludeSystemAudio,
        SystemAudioDeviceId = SystemAudioDeviceId,
        PreventSleep = PreventSleep,
    };
}

/// <summary>Automatic backups are optional and off by default (docs/06).</summary>
public sealed record BackupSettings
{
    public bool Automatic { get; set; }

    /// <summary>Days between automatic backups.</summary>
    public int IntervalDays { get; set; } = 7;
}

public sealed record GeneralSettings
{
    /// <summary><c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>UI language override (<c>ja-JP</c>, <c>en-US</c>); null follows the Windows display language.</summary>
    public string? UiLanguage { get; set; }

    /// <summary>Windows notifications when background processing finishes or fails.</summary>
    public bool Notifications { get; set; } = true;

    /// <summary>
    /// Pane shown on the recording page: <c>transcript</c> or <c>summary</c> (ADR-032). The name is kept from the former
    /// layout setting so saved preferences still load; older values map to the pane they showed first.
    /// </summary>
    public string DetailLayout { get; set; } = "transcript";

    /// <summary>Ask GitHub once a day whether a newer release is published (ADR-040).</summary>
    public bool CheckForUpdates { get; set; } = true;

    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Release whose Home notice the user closed (shown again for a newer one).</summary>
    public string? DismissedUpdate { get; set; }
}

public sealed record ProcessingSettings
{
    /// <summary>Battery/thermal behavior for background AI work (docs/06).</summary>
    public ProcessingMode Mode { get; set; } = ProcessingMode.Auto;

    /// <summary>Run the processing pipeline automatically after each recording/import.</summary>
    public bool AutoProcess { get; set; } = true;

    /// <summary>Spoken language hint for transcription (BCP-47, e.g. <c>ja</c>); null = detect automatically.</summary>
    public string? TranscriptionLanguage { get; set; }

    /// <summary>Explicit ASR model; null = automatic (benchmark-derived preference, ADR-022).</summary>
    public string? AsrModelId { get; set; }

    /// <summary>
    /// A yt-dlp executable chosen by the user for URL import; used when the managed (hash-pinned) copy is not installed.
    /// </summary>
    public string? YtDlpPath { get; set; }

    /// <summary><see cref="SummaryEngines.Auto"/> (local AI when installed) or <see cref="SummaryEngines.Extractive"/>.</summary>
    public string SummaryEngine { get; set; } = SummaryEngines.Auto;

    /// <summary>Suggest a glossary on longer recordings that no glossary applies to (ADR-031).</summary>
    public bool GlossaryTips { get; set; } = true;

    /// <summary>Download the recommended ASR model and yt-dlp at startup when they are missing (ADR-034).</summary>
    public bool AutoDownloadModels { get; set; } = true;
}

public static class SummaryEngines
{
    public const string Auto = "auto";
    public const string Extractive = "extractive";
}

/// <summary>Persisted settings with change notification.</summary>
public interface ISettingsStore
{
    /// <summary>A snapshot; mutate through <see cref="UpdateAsync"/>.</summary>
    AppSettings Current { get; }

    event EventHandler? Changed;

    Task UpdateAsync(Action<AppSettings> mutate, CancellationToken cancellationToken = default);

    /// <summary>Restores defaults ("Reset Config"). The Library is not touched.</summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
