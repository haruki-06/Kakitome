using System.Globalization;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Domain.Recordings;
using Kakitome.Presentation.Services;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Presentation;

public sealed class NotificationTests
{
    [Fact]
    public async Task Finished_processing_notifies_with_the_title_and_opens_the_recording()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var sink = new FakeSink();
        using var policy = Create(f, sink);
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Title = "週次定例", RecordedAt = Samples.Jst });

        policy.Observe(Job("summary", JobState.Succeeded, entry.Id));
        await policy.FlushAsync();

        var notice = Assert.Single(sink.Shown);
        Assert.Equal("Notify_Ready", notice.Title);
        Assert.Equal("週次定例", notice.Body);
        Assert.Equal((PageKey.RecordingDetail, (object?)new RecordingNavigation(entry.Id)), NoticeArguments.Parse(notice.Arguments));
    }

    [Fact]
    public async Task A_long_recording_without_a_glossary_mentions_the_glossary_tip()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var sink = new FakeSink();
        using var policy = new NotificationPolicy(f.Scheduler, f.Library, f.Settings, sink, new KeyLocalizer(), f.Time,
            new Kakitome.Application.Asr.GlossaryTips(f.Library, new Kakitome.Application.Asr.GlossaryService(f.Store), f.Settings));
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Title = "講義", RecordedAt = Samples.Jst });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(entry.Id));

        policy.Observe(Job("summary", JobState.Succeeded, entry.Id));
        await policy.FlushAsync();

        Assert.Equal("講義" + Environment.NewLine + "Notify_GlossaryTip", Assert.Single(sink.Shown).Body);
    }

    [Fact]
    public async Task Batches_are_coalesced_and_intermediate_stages_are_ignored()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var sink = new FakeSink();
        using var policy = Create(f, sink);

        policy.Observe(Job("asr", JobState.Succeeded, RecordingId.New()));
        policy.Observe(Job("summary", JobState.Running, RecordingId.New()));
        for (var i = 0; i < 3; i++)
        {
            policy.Observe(Job("summary", JobState.Succeeded, RecordingId.New()));
        }

        await policy.FlushAsync();

        var notice = Assert.Single(sink.Shown);
        Assert.Equal("Notify_ReadyMany(3)", notice.Body);
        Assert.Equal((PageKey.Library, (object?)null), NoticeArguments.Parse(notice.Arguments));
    }

    [Fact]
    public async Task Failures_notify_but_cancellations_do_not()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var sink = new FakeSink();
        using var policy = Create(f, sink);

        policy.Observe(Job("asr", JobState.Cancelled, RecordingId.New()));
        await policy.FlushAsync();
        Assert.Empty(sink.Shown);

        policy.Observe(Job("model.install", JobState.Failed, null));
        await policy.FlushAsync();
        var notice = Assert.Single(sink.Shown);
        Assert.Equal("Notify_Failed", notice.Title);
        Assert.Equal((PageKey.Settings, (object?)null), NoticeArguments.Parse(notice.Arguments));
    }

    [Fact]
    public async Task Nothing_is_shown_in_the_foreground_or_when_turned_off()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var sink = new FakeSink { Foreground = true };
        using var policy = Create(f, sink);

        policy.Observe(Job("summary", JobState.Succeeded, RecordingId.New()));
        await policy.FlushAsync();
        Assert.Empty(sink.Shown);

        sink.Foreground = false;
        await f.Settings.UpdateAsync(s => s.General.Notifications = false);
        policy.Observe(Job("summary", JobState.Succeeded, RecordingId.New()));
        await policy.FlushAsync();
        Assert.Empty(sink.Shown);
    }

    [Fact]
    public async Task The_coalescing_timer_flushes_after_the_window()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var sink = new FakeSink();
        using var policy = Create(f, sink);

        policy.Observe(Job("summary", JobState.Succeeded, RecordingId.New()));
        Assert.Empty(sink.Shown);
        f.Time.Advance(NotificationPolicy.CoalesceWindow);
        for (var i = 0; i < 200 && sink.Shown.IsEmpty; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Single(sink.Shown);
    }

    [Theory]
    [InlineData("action=open&page=Queue", PageKey.Queue)]
    [InlineData("action=delete&recording=4f30db6d-5275-43ad-bc74-09a2e740e1ec", PageKey.Home)]
    [InlineData("action=open&page=RecordingDetail", PageKey.Home)]
    [InlineData("action=open&page=Nope", PageKey.Home)]
    [InlineData("action=open&recording=not-a-guid", PageKey.Home)]
    [InlineData("", PageKey.Home)]
    [InlineData(null, PageKey.Home)]
    public void Notification_arguments_only_ever_open_a_screen(string? arguments, PageKey expected) =>
        Assert.Equal(expected, NoticeArguments.Parse(arguments).Page);

    private static NotificationPolicy Create(LibraryFixture f, FakeSink sink) =>
        new(f.Scheduler, f.Library, f.Settings, sink, new KeyLocalizer(), f.Time);

    private static JobRecord Job(string kind, JobState state, RecordingId? recording) =>
        new() { Id = Guid.NewGuid(), Kind = kind, State = state, RecordingId = recording };

    private sealed class FakeSink : INotificationSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<AppNotice> Shown { get; } = new();

        public bool Foreground { get; set; }

        public bool IsAppInForeground => Foreground;

        public void Show(AppNotice notice) => Shown.Enqueue(notice);
    }

    private sealed class KeyLocalizer : ILocalizer
    {
        public string GetString(string key) => key;

        public string Format(string key, params object?[] args) =>
            $"{key}({string.Join(",", args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)))})";
    }
}
