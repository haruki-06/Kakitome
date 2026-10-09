using Microsoft.Extensions.DependencyInjection;
using Kakitome.Application.Decision;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Search;
using Kakitome.Application.Summaries;
using Kakitome.Domain.Library;
using Kakitome.Domain.Transcripts;
using Kakitome.Tests.Jobs;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.PostProcessing;

public sealed class DecisionEngineTests
{
    private readonly RuleBasedDecisionEngine _engine = new();

    [Theory]
    [InlineData("えーと、その件については確認します。", "その件については確認します。")]
    [InlineData("えー、今日はですね", "今日はですね")]
    [InlineData("それで、それで次の話です", "それで次の話です")]
    [InlineData("確認します。えっと、次に進みます。", "確認します。次に進みます。")]
    [InlineData("Um, I think so", "I think so")]
    [InlineData("we we should go", "we should go")]
    public void Low_risk_edits_are_applied(string input, string expected)
    {
        var lang = input.Any(c => c > 0x3000) ? "ja" : "en";
        var edits = _engine.FindEditCandidates(input, lang).Where(e => e.Risk == EditRisk.Low);
        Assert.Equal(expected, CleanupJobHandler.Apply(input, edits));
    }

    [Theory]
    [InlineData("あの人が来ました。")]          // demonstrative, not a filler
    [InlineData("その件は明日です。")]
    [InlineData("えいご")]                       // not a hesitation
    [InlineData("講義を始めます")]
    public void Meaningful_words_are_never_auto_removed(string input)
    {
        var edits = _engine.FindEditCandidates(input, "ja").Where(e => e.Risk == EditRisk.Low);
        Assert.Equal(input, CleanupJobHandler.Apply(input, edits));
    }

    [Fact]
    public void Ambiguous_fillers_become_suggestions_only()
    {
        var edits = _engine.FindEditCandidates("まあ、方向性は悪くないです", "ja");
        var suggestion = Assert.Single(edits);
        Assert.Equal(EditRisk.High, suggestion.Risk);
        Assert.Equal(EditKind.Filler, suggestion.Kind);
    }

    [Fact]
    public void Suspicious_segments_are_flagged()
    {
        Assert.Contains("possibleHallucination", _engine.AssessSegment("ご視聴ありがとうございました", 0.9, 2, "ja"));
        Assert.Contains("repetitionLoop", _engine.AssessSegment("はいはいはいはいはいはいはい", 0.9, 5, "ja"));
        Assert.Contains("lowConfidence", _engine.AssessSegment("こんにちは", 0.3, 1, "ja"));
        Assert.Contains("implausibleRate", _engine.AssessSegment(new string('あ', 100), 0.9, 2, "ja"));
        Assert.Empty(_engine.AssessSegment("今日は晴れです", 0.9, 2, "ja"));
    }

    [Fact]
    public void Recording_type_is_classified_from_cues()
    {
        Assert.Equal(RecordingType.Lecture, _engine.ClassifyRecording(["今日の講義では", "次の章を説明します", "演習問題です"], 1, 3000, "ja").Type);
        Assert.Equal(RecordingType.Meeting, _engine.ClassifyRecording(["議題に移ります", "担当は佐藤さんで", "次回決定します"], 3, 1800, "ja").Type);
        Assert.Equal(RecordingType.VoiceNote, _engine.ClassifyRecording(["牛乳を買う"], 1, 20, "ja").Type);
    }
}

public sealed class PostProcessingPipelineTests
{
    [Fact]
    public async Task Cleanup_keeps_raw_text_respects_user_edits_and_records_lineage()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        var transcript = Samples.Transcript(entry.Id);
        transcript.Segments[0].Text = "えーと、それでは始めます。";
        transcript.Segments[1].Text = "えーと、これはユーザーが直した文です。";
        transcript.Segments[1].Edited = true;
        transcript.Segments[2].Text = "まあ、質問があります。";
        await f.Library.SaveTranscriptAsync(transcript);

        var job = await f.Scheduler.EnqueueAsync(new JobRequest(CleanupJobHandler.JobKind) { RecordingId = entry.Id });
        await JobSchedulerTests.RunUntilSettledAsync(f);

        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(job.Id))!.State);
        var clean = (await f.Library.LoadTranscriptAsync(entry.Id))!;
        Assert.Equal(TranscriptKind.Clean, clean.Kind);
        Assert.Equal(2, clean.Revision);
        Assert.Equal("それでは始めます。", clean.Segments[0].Text);
        Assert.Equal("えーと、それでは始めます。", clean.Segments[0].RawText);
        Assert.Equal("えーと、これはユーザーが直した文です。", clean.Segments[1].Text); // user edit untouched
        Assert.Equal("まあ、質問があります。", clean.Segments[2].Text);               // ambiguous → suggestion only
        var suggestion = Assert.Single(clean.Suggestions!);
        Assert.Equal("まあ、", suggestion.Original);
        Assert.Equal(clean.Segments[2].Id, suggestion.SegmentId);
        Assert.Equal(["asr", "cleanup:rules-v1"], clean.Lineage.Select(l => l.Source));
        Assert.Equal(4, clean.Segments.Count); // segments are never removed

        // Re-running is a no-op.
        await f.Scheduler.EnqueueAsync(new JobRequest(CleanupJobHandler.JobKind) { RecordingId = entry.Id });
        await JobSchedulerTests.RunUntilSettledAsync(f);
        Assert.Equal(2, (await f.Library.LoadTranscriptAsync(entry.Id))!.Revision);
    }

    [Fact]
    public async Task Extractive_summary_finds_decisions_actions_and_questions_in_the_transcript_language()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Project = "会議", Title = "定例会議" });
        var t = Samples.Transcript(entry.Id);
        t.Segments =
        [
            Seg("a", 0, "では定例会議を始めます。今週はテストの自動化を中心に進めました。"),
            Seg("b", 30, "結論として、来月のリリースは予定どおり進めることにします。"),
            Seg("c", 60, "担当は佐藤さんで、来週の水曜日までに対策案をまとめてください。"),
            Seg("d", 90, "バッテリー消費の原因はわかっていますか？"),
            Seg("e", 120, "テストの自動化は八割ほど終わっていて、自動化の残りは来週終わる予定です。"),
        ];
        await f.Library.SaveTranscriptAsync(t);

        await f.Scheduler.EnqueueAsync(new JobRequest(SummaryJobHandler.JobKind) { RecordingId = entry.Id });
        await JobSchedulerTests.RunUntilSettledAsync(f);

        var summary = (await f.Library.LoadSummaryAsync(entry.Id))!;
        Assert.Equal("ja", summary.Language);
        Assert.Contains(summary.Decisions, d => d.Text.Contains("予定どおり進めることにします", StringComparison.Ordinal));
        var action = Assert.Single(summary.ActionItems, a => a.Text.Contains("対策案", StringComparison.Ordinal));
        Assert.Equal("佐藤", action.Owner);
        Assert.Equal("来週の水曜日", action.Due);
        Assert.Equal(60, action.AtSeconds);
        Assert.Contains(summary.Questions, q => q.Text.Contains("バッテリー消費", StringComparison.Ordinal));
        Assert.Contains("自動化", summary.Topics);
        Assert.NotEmpty(summary.KeyPoints);
        Assert.Equal(1, summary.Generation.TranscriptRevision);
        Assert.Contains("extractive", summary.Generation.Engine.Provider, StringComparison.Ordinal);
        Assert.All(summary.KeyPoints, k => Assert.Contains(k.Text, string.Concat(t.Segments.Select(s => s.Text)), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Summary_failure_never_touches_the_transcript()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(entry.Id));
        var folder = await f.Library.GetRecordingPathAsync(entry.Id);
        await File.WriteAllTextAsync(Path.Combine(folder, LibraryLayout.SummaryMarkdownFile), "my own summary");
        var before = await File.ReadAllBytesAsync(Path.Combine(folder, LibraryLayout.TranscriptJsonFile));

        var job = await f.Scheduler.EnqueueAsync(new JobRequest(SummaryJobHandler.JobKind) { RecordingId = entry.Id });
        await JobSchedulerTests.RunUntilSettledAsync(f);

        Assert.Equal(JobState.Failed, (await f.Jobs.GetAsync(job.Id))!.State);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(folder, LibraryLayout.TranscriptJsonFile)));
        Assert.Equal("my own summary", await File.ReadAllTextAsync(Path.Combine(folder, LibraryLayout.SummaryMarkdownFile)));
    }

    [Fact]
    public async Task A_failing_local_llm_falls_back_to_the_extractive_summary()
    {
        var llm = new FakeLlm { Fail = true };
        await using var f = await LibraryFixture.CreateAsync(configure: s => s.AddSingleton<ISummaryProvider>(llm));
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(entry.Id));

        var job = await f.Scheduler.EnqueueAsync(new JobRequest(SummaryJobHandler.JobKind) { RecordingId = entry.Id });
        await JobSchedulerTests.RunUntilSettledAsync(f);

        Assert.Equal(1, llm.Calls);
        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(job.Id))!.State);
        Assert.StartsWith("Kakitome extractive", (await f.Library.LoadSummaryAsync(entry.Id))!.Generation.Engine.Provider, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_llm_is_preferred_unless_settings_ask_for_extraction_only()
    {
        var llm = new FakeLlm();
        await using var f = await LibraryFixture.CreateAsync(configure: s => s.AddSingleton<ISummaryProvider>(llm));
        var a = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(a.Id));
        await f.Scheduler.EnqueueAsync(new JobRequest(SummaryJobHandler.JobKind) { RecordingId = a.Id });
        await JobSchedulerTests.RunUntilSettledAsync(f);
        Assert.Equal("fake-llm", (await f.Library.LoadSummaryAsync(a.Id))!.Generation.Engine.Provider);

        await f.Settings.UpdateAsync(s => s.Processing.SummaryEngine = Kakitome.Application.Settings.SummaryEngines.Extractive);
        var b = await f.Library.CreateRecordingAsync(new NewRecording { Project = "P" });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(b.Id));
        await f.Scheduler.EnqueueAsync(new JobRequest(SummaryJobHandler.JobKind) { RecordingId = b.Id });
        await JobSchedulerTests.RunUntilSettledAsync(f);
        Assert.StartsWith("Kakitome extractive", (await f.Library.LoadSummaryAsync(b.Id))!.Generation.Engine.Provider, StringComparison.Ordinal);
        Assert.Equal(1, llm.Calls);
    }

    private sealed class FakeLlm : ISummaryProvider
    {
        public bool Fail { get; init; }

        public int Calls { get; private set; }

        public string Id => "fake-llm";

        public int Preference => 10;

        public JobResourceClass ResourceClass => JobResourceClass.Heavy;

        public bool IsAvailable(string? language) => true;

        public Task<SummaryDraft> SummarizeAsync(SummaryInput input, ResourceBudget budget, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Fail
                ? throw new InsufficientMemoryException("model does not fit")
                : Task.FromResult(new SummaryDraft { Title = input.Title, Overview = "llm", Engine = "fake-llm" });
        }
    }

    [Fact]
    public async Task Search_finds_japanese_words_of_any_length_with_positions_and_survives_rebuild()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var a = await f.Library.CreateRecordingAsync(new NewRecording { Project = "大学講義", Title = "機械学習 第3回", Tags = ["ML"] });
        await f.Library.SaveTranscriptAsync(Samples.Transcript(a.Id));
        var b = await f.Library.CreateRecordingAsync(new NewRecording { Project = "会議", Title = "週次定例" });
        await f.Library.SaveTranscriptAsync(Transcript(b.Id, "来月のリリースについて報告します。"));

        await f.Search.ReindexAsync(a.Id);
        await f.Search.ReindexAsync(b.Id);

        var three = await f.Search.SearchAsync("勾配降下");
        var hit = Assert.Single(three);
        Assert.Equal(a.Id, hit.RecordingId);
        Assert.Equal(SearchHitKind.Transcript, hit.Kind);
        Assert.Equal(8.2, hit.StartSeconds);
        Assert.Contains("[勾配降下]", hit.Snippet, StringComparison.Ordinal);

        var two = await f.Search.SearchAsync("報告"); // 2 characters: substring fallback
        Assert.Equal(b.Id, Assert.Single(two).RecordingId);
        Assert.Contains("[報告]", two[0].Snippet, StringComparison.Ordinal);

        Assert.Equal(a.Id, Assert.Single(await f.Search.SearchAsync("ML")).RecordingId);   // tag (title document)
        Assert.Empty(await f.Search.SearchAsync("存在しない語句"));
        Assert.Empty(await f.Search.SearchAsync("\"; DROP TABLE x; --"));                    // treated as text

        await f.SearchIndex.ClearAsync();
        Assert.Equal(2, await f.Search.RebuildAsync());
        Assert.Single(await f.Search.SearchAsync("勾配降下"));
    }

    [Fact]
    public async Task Full_pipeline_after_recording_produces_transcript_summary_and_search()
    {
        var asr = new Kakitome.Tests.Asr.FakeAsrProvider();
        await using var f = await Kakitome.Tests.Asr.AsrPipelineTests.CreateAsync(asr);
        var id = await Kakitome.Tests.Asr.AsrPipelineTests.CreateRecordingAsync(f, ("audio.wav", AudioStreamRole.Microphone, 40));

        await f.Pipeline.EnqueueAsync(id);
        await JobSchedulerTests.RunUntilSettledAsync(f, passes: 12);

        var metadata = await f.Library.GetMetadataAsync(id);
        Assert.Equal(["audio.analyze", "asr", "cleanup", "summary", "index"], metadata.Processing.Select(p => p.Stage));
        for (var i = 0; i < 100 && metadata.Processing.Any(p => p.Status != ProcessingStepStatus.Succeeded); i++)
        {
            await Task.Delay(20);
            metadata = await f.Library.GetMetadataAsync(id);
        }

        Assert.All(metadata.Processing, p => Assert.Equal(ProcessingStepStatus.Succeeded, p.Status));
        Assert.NotNull(await f.Library.LoadSummaryAsync(id));
        Assert.NotEmpty(await f.Search.SearchAsync("発話1"));
    }

    private static TranscriptSegment Seg(string id, double start, string text) =>
        new() { Id = id, StartSeconds = start, EndSeconds = start + 10, Text = text };

    private static TranscriptDocument Transcript(Kakitome.Domain.Recordings.RecordingId id, string text) => new()
    {
        RecordingId = id,
        Language = "ja",
        Segments = [Seg("s1", 3, text)],
    };
}
