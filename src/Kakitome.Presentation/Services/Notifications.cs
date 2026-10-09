using System.Globalization;
using Kakitome.Application.Asr;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Models;
using Kakitome.Application.Settings;
using Kakitome.Domain.Recordings;

namespace Kakitome.Presentation.Services;

/// <summary>A Windows notification. <see cref="Arguments"/> only ever opens a Kakitome screen (docs/01, docs/07).</summary>
public sealed record AppNotice(string Title, string Body, string Arguments);

/// <summary>Shows notifications (AppNotificationManager in the app).</summary>
public interface INotificationSink
{
    /// <summary>True while the user is looking at Kakitome; in-app views already show the result then.</summary>
    bool IsAppInForeground { get; }

    void Show(AppNotice notice);
}

/// <summary>Notification click arguments: <c>action=open&amp;recording=&lt;id&gt;</c> or <c>action=open&amp;page=&lt;page&gt;</c>.</summary>
public static class NoticeArguments
{
    public static string OpenRecording(RecordingId id) => "action=open&recording=" + id;

    public static string OpenPage(PageKey page) => "action=open&page=" + page;

    /// <summary>Maps arguments to a navigation target; anything unexpected opens Home (never performs an action).</summary>
    public static (PageKey Page, object? Parameter) Parse(string? arguments)
    {
        var pairs = (arguments ?? string.Empty)
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First()[1], StringComparer.Ordinal);
        if (!pairs.TryGetValue("action", out var action) || action != "open")
        {
            return (PageKey.Home, null);
        }

        if (pairs.TryGetValue("recording", out var id) && RecordingId.TryParse(id, CultureInfo.InvariantCulture, out var recording))
        {
            return (PageKey.RecordingDetail, new RecordingNavigation(recording));
        }

        return pairs.TryGetValue("page", out var page)
               && Enum.TryParse<PageKey>(page, ignoreCase: false, out var key)
               && key != PageKey.RecordingDetail
            ? (key, null)
            : (PageKey.Home, null);
    }
}

/// <summary>
/// Decides which background results deserve a Windows notification: processing finished (summary ready) or failed for
/// good. Events are coalesced for a few seconds so a batch (e.g. reprocessing after an update) produces one
/// notification, and nothing is shown while Kakitome is in the foreground or notifications are turned off.
/// </summary>
public sealed class NotificationPolicy : IDisposable
{
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(3);

    private readonly LibraryService _library;
    private readonly ISettingsStore _settings;
    private readonly INotificationSink _sink;
    private readonly ILocalizer _text;
    private readonly JobScheduler _scheduler;
    private readonly GlossaryTips? _tips;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private readonly List<RecordingId> _ready = [];
    private readonly List<JobRecord> _failed = [];

    public NotificationPolicy(
        JobScheduler scheduler, LibraryService library, ISettingsStore settings, INotificationSink sink, ILocalizer text, TimeProvider time, GlossaryTips? tips = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(time);
        _scheduler = scheduler;
        _library = library;
        _settings = settings;
        _sink = sink;
        _text = text;
        _tips = tips;
        _timer = time.CreateTimer(_ => _ = FlushAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _scheduler.JobChanged += OnJobChanged;
    }

    public void Dispose()
    {
        _scheduler.JobChanged -= OnJobChanged;
        _timer.Dispose();
    }

    /// <summary>Shows what has been collected (normally called by the coalescing timer).</summary>
    public async Task FlushAsync()
    {
        List<RecordingId> ready;
        List<JobRecord> failed;
        lock (_gate)
        {
            ready = [.. _ready.Distinct()];
            failed = [.. _failed];
            _ready.Clear();
            _failed.Clear();
        }

        if ((ready.Count == 0 && failed.Count == 0) || !_settings.Current.General.Notifications || _sink.IsAppInForeground)
        {
            return;
        }

        if (failed.Count == 1)
        {
            var job = failed[0];
            var title = job.RecordingId is { } id ? await TitleAsync(id).ConfigureAwait(false) : null;
            var stage = _text.GetString($"Stage_{job.Kind.Replace('.', '_')}");
            _sink.Show(new AppNotice(
                _text.GetString("Notify_Failed"),
                title is null ? stage : _text.Format("Notify_FailedBody", title, stage),
                job.Kind == ModelInstallJobHandler.JobKind ? NoticeArguments.OpenPage(PageKey.Settings)
                : job.RecordingId is { } failedId ? NoticeArguments.OpenRecording(failedId)
                : NoticeArguments.OpenPage(PageKey.Queue)));
        }
        else if (failed.Count > 1)
        {
            _sink.Show(new AppNotice(_text.GetString("Notify_Failed"), _text.Format("Notify_FailedMany", failed.Count), NoticeArguments.OpenPage(PageKey.Queue)));
        }

        if (ready.Count == 1)
        {
            var body = await TitleAsync(ready[0]).ConfigureAwait(false) ?? string.Empty;
            if (_tips is not null && await _tips.GetAsync(ready[0]).ConfigureAwait(false) is not null)
            {
                body += Environment.NewLine + _text.GetString("Notify_GlossaryTip"); // details are on the recording's page
            }

            _sink.Show(new AppNotice(_text.GetString("Notify_Ready"), body, NoticeArguments.OpenRecording(ready[0])));
        }
        else if (ready.Count > 1)
        {
            _sink.Show(new AppNotice(_text.GetString("Notify_Ready"), _text.Format("Notify_ReadyMany", ready.Count), NoticeArguments.OpenPage(PageKey.Library)));
        }
    }

    private void OnJobChanged(object? sender, JobChangedEventArgs e) => Observe(e.Job);

    internal void Observe(JobRecord job)
    {
        var interesting = false;
        lock (_gate)
        {
            if (job.State == JobState.Succeeded && job.Kind == Kakitome.Application.Summaries.SummaryJobHandler.JobKind && job.RecordingId is { } id)
            {
                _ready.Add(id);
                interesting = true;
            }
            else if (job.State == JobState.Failed)
            {
                // Failed is terminal (retries exhausted or permanent); cancellations are the user's own action.
                _failed.Add(job);
                interesting = true;
            }
        }

        if (interesting)
        {
            _timer.Change(CoalesceWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task<string?> TitleAsync(RecordingId id) =>
        (await _library.FindAsync(id).ConfigureAwait(false))?.Title;
}
