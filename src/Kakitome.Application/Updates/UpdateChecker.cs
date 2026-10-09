using System.Globalization;
using System.Reflection;
using Kakitome.Application.Settings;
using Microsoft.Extensions.Logging;

namespace Kakitome.Application.Updates;

/// <summary>The newest published release: version (without a leading "v") and its release page.</summary>
public sealed record ReleaseInfo(string Version, Uri Page);

/// <summary>Where published releases are listed (GitHub in the app; a fake in tests).</summary>
public interface IReleaseFeed
{
    /// <summary>The latest (non pre-release) release, or null when none is published.</summary>
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default);
}

public enum UpdateCheckResult
{
    /// <summary>Turned off, or checked less than a day ago.</summary>
    Skipped,
    UpToDate,
    Available,
    Failed,
}

/// <summary>
/// Update notice (ADR-040, user request): once a day asks the release feed for the latest release and tells the user
/// when it is newer than this build. Only the version is requested — no user data is sent — and nothing is downloaded
/// or installed; the user opens the release page. Off with <see cref="GeneralSettings.CheckForUpdates"/>.
/// </summary>
public sealed partial class UpdateChecker(IReleaseFeed feed, ISettingsStore settings, TimeProvider time, ILogger<UpdateChecker> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>This build's version (SemVer, e.g. 2.0.0-beta.10).</summary>
    public string CurrentVersion { get; init; } = EntryVersion();

    /// <summary>A newer release, once found (null otherwise).</summary>
    public ReleaseInfo? Available { get; private set; }

    /// <summary>Raised when <see cref="Available"/> changes.</summary>
    public event EventHandler? AvailableChanged;

    /// <summary>Checks now when <paramref name="force"/>, otherwise only when on and the last check is a day old.</summary>
    public async Task<UpdateCheckResult> CheckAsync(bool force, CancellationToken cancellationToken = default)
    {
        var general = settings.Current.General;
        var now = time.GetUtcNow();
        if (!force && (!general.CheckForUpdates || (general.LastUpdateCheck is { } last && now - last < Interval)))
        {
            return UpdateCheckResult.Skipped;
        }

        ReleaseInfo? latest;
        try
        {
            latest = await feed.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException
                                   && !cancellationToken.IsCancellationRequested)
        {
            LogFailed(ex);
            return UpdateCheckResult.Failed;
        }

        await settings.UpdateAsync(s => s.General.LastUpdateCheck = now, cancellationToken).ConfigureAwait(false);
        var newer = latest is not null && IsNewer(latest.Version, CurrentVersion) ? latest : null;
        if (newer != Available)
        {
            Available = newer;
            AvailableChanged?.Invoke(this, EventArgs.Empty);
        }

        if (newer is not null)
        {
            LogAvailable(newer.Version);
        }

        return newer is null ? UpdateCheckResult.UpToDate : UpdateCheckResult.Available;
    }

    /// <summary>Checks at start and then every few hours (the daily limit applies) until <paramref name="stop"/>.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6), time);
        try
        {
            do
            {
                await CheckAsync(force: false, stop).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // app exit
        }
    }

    /// <summary>SemVer precedence (build metadata ignored): true when <paramref name="candidate"/> is newer.</summary>
    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    public static int Compare(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);
        for (var i = 0; i < 3; i++)
        {
            var c = coreA[i].CompareTo(coreB[i]);
            if (c != 0)
            {
                return c;
            }
        }

        // A release is newer than its pre-releases (2.0.0 > 2.0.0-beta.10).
        if (preA.Length == 0 || preB.Length == 0)
        {
            return preA.Length == preB.Length ? 0 : preA.Length == 0 ? 1 : -1;
        }

        for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            var numA = int.TryParse(preA[i], NumberStyles.None, CultureInfo.InvariantCulture, out var na);
            var numB = int.TryParse(preB[i], NumberStyles.None, CultureInfo.InvariantCulture, out var nb);
            var c = numA && numB ? na.CompareTo(nb)
                : numA ? -1
                : numB ? 1
                : string.CompareOrdinal(preA[i], preB[i]);
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return preA.Length.CompareTo(preB.Length);
    }

    private static (int[] Core, string[] Pre) Split(string version)
    {
        var v = version.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = v.IndexOf('-', StringComparison.Ordinal);
        var core = (dash < 0 ? v : v[..dash]).Split('.');
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            numbers[i] = i < core.Length && int.TryParse(core[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n
                : i < core.Length ? throw new FormatException($"Not a version: {version}") : 0;
        }

        return (numbers, dash < 0 ? [] : v[(dash + 1)..].Split('.'));
    }

    private static string EntryVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    [LoggerMessage(Level = LogLevel.Information, Message = "Kakitome {Version} is available")]
    private partial void LogAvailable(string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update check failed")]
    private partial void LogFailed(Exception ex);
}
