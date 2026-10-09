using Kakitome.Domain.Library;
using Kakitome.Domain.Recordings;
using Kakitome.Domain.Summaries;
using Kakitome.Domain.Transcripts;

namespace Kakitome.Tests.TestSupport;

internal static class Samples
{
    public static readonly DateTimeOffset Jst = new(2026, 9, 30, 10, 15, 30, TimeSpan.FromHours(9));

    public static RecordingMetadata Metadata(RecordingId? id = null) => new()
    {
        Id = id ?? RecordingId.New(),
        Title = "機械学習 第3回",
        Project = "大学講義",
        Tags = ["講義", "ML"],
        CreatedAt = Jst,
        RecordedAt = Jst,
        DurationSeconds = 3723,
        SourceType = RecordingSourceType.Recording,
        Capture = new CaptureInfo { Status = CaptureStatus.Completed, StartedAt = Jst, EndedAt = Jst.AddSeconds(3723) },
        Audio = [new AudioStreamInfo { FileName = "audio.flac", Role = AudioStreamRole.Microphone, Format = "flac", SampleRate = 48000, Channels = 1 }],
        RetainedAudioFormat = "flac",
        Language = "ja",
    };

    public static TranscriptDocument Transcript(RecordingId id, string language = "ja") => new()
    {
        RecordingId = id,
        Language = language,
        CreatedAt = Jst,
        UpdatedAt = Jst,
        Engine = new EngineInfo { Provider = "test-asr", Model = "tiny", Version = "1.0", Locality = "local" },
        Lineage = [new TranscriptLineageEntry { Kind = TranscriptKind.Raw, Revision = 1, At = Jst, Source = "asr" }],
        Speakers = [new SpeakerInfo { Id = "S1" }, new SpeakerInfo { Id = "S2", Label = "田中" }],
        Segments =
        [
            new TranscriptSegment { Id = "s1", StartSeconds = 5, EndSeconds = 8, Text = "それでは始めます。", Speaker = "S1", Confidence = 0.93 },
            new TranscriptSegment { Id = "s2", StartSeconds = 8.2, EndSeconds = 12, Text = "今日は勾配降下法です。", Speaker = "S1" },
            new TranscriptSegment { Id = "s3", StartSeconds = 20, EndSeconds = 25, Text = "質問があります。", Speaker = "S2" },
            new TranscriptSegment { Id = "s4", StartSeconds = 3700, EndSeconds = 3720, Text = "以上です。", Speaker = "S1" },
        ],
    };

    public static SummaryDocument Summary(RecordingId id) => new()
    {
        RecordingId = id,
        Language = "ja",
        Title = "勾配降下法の基礎",
        Overview = "勾配降下法の考え方と学習率の選び方を扱った。",
        KeyPoints = [new SummaryItem { Text = "学習率が大きすぎると発散する", AtSeconds = 125 }],
        Decisions = [new SummaryItem { Text = "次回は確率的勾配降下法を扱う" }],
        ActionItems = [new ActionItem { Text = "演習問題 3 を解く", Owner = "受講者", Due = "来週月曜" }],
        Questions = [],
        Topics = ["最適化", "学習率"],
        Generation = new SummaryGeneration
        {
            Engine = new EngineInfo { Provider = "test-llm", Model = "small" },
            CreatedAt = Jst.AddHours(2),
            TranscriptRevision = 1,
        },
    };
}
