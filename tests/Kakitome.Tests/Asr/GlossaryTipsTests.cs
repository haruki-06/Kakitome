using Kakitome.Application.Asr;
using Kakitome.Application.Library;
using Kakitome.Domain.Transcripts;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Asr;

public sealed class GlossaryTipsTests
{
    [Fact]
    public async Task A_long_transcript_without_a_glossary_gets_the_tip_with_its_project_and_topics()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var tips = Create(f);
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Title = "憲法 第12回", Project = "憲法", RecordedAt = Samples.Jst });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(entry.Id));
        await f.Library.SaveSummaryAsync(Samples.Summary(entry.Id));

        var tip = await tips.GetAsync(entry.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(tip);
        Assert.Equal("憲法", tip.Project);
        Assert.Equal("憲法 第12回", tip.Title);
        Assert.Equal(Samples.Summary(entry.Id).Topics, tip.Topics);
    }

    [Fact]
    public async Task No_tip_once_a_glossary_applies_or_when_turned_off()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var tips = Create(f);
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Title = "講義", Project = "憲法", RecordedAt = Samples.Jst });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(entry.Id));
        Assert.NotNull(await tips.GetAsync(entry.Id, TestContext.Current.CancellationToken));

        await File.WriteAllTextAsync(Path.Combine(f.LibraryRoot, "Projects", "glossary.txt"), "信教の自由\n", TestContext.Current.CancellationToken);
        Assert.Null(await tips.GetAsync(entry.Id, TestContext.Current.CancellationToken));

        File.Delete(Path.Combine(f.LibraryRoot, "Projects", "glossary.txt"));
        await tips.HideAsync(TestContext.Current.CancellationToken);
        Assert.False(f.Settings.Current.Processing.GlossaryTips);
        Assert.Null(await tips.GetAsync(entry.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Short_recordings_and_recordings_without_a_transcript_get_no_tip()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var tips = Create(f);
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Title = "メモ", RecordedAt = Samples.Jst });
        Assert.Null(await tips.GetAsync(entry.Id, TestContext.Current.CancellationToken));

        var metadata = await f.Library.GetMetadataAsync(entry.Id, TestContext.Current.CancellationToken);
        var shortTranscript = Samples.Transcript(entry.Id);
        shortTranscript.Segments = [.. shortTranscript.Segments.Take(3)];
        Assert.False(GlossaryTips.Applies(metadata, shortTranscript, Glossary.Empty));
        Assert.True(GlossaryTips.Applies(metadata, Samples.Transcript(entry.Id), Glossary.Empty));
    }

    private static GlossaryTips Create(LibraryFixture f) => new(f.Library, new GlossaryService(f.Store), f.Settings);
}
