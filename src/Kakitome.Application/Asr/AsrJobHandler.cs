using System.Globalization;
using System.Text;
using System.Text.Json;
using Kakitome.Application.Audio;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Settings;
using Kakitome.Domain.Library;
using Kakitome.Domain.Transcripts;

namespace Kakitome.Application.Asr;

/// <summary>
/// <c>asr</c>: final transcription of every non-silent audio stream into <c>transcript.json/.md</c>. Streams are
/// transcribed separately (source-aware, docs/03) and merged by time with one speaker per source. Progress is
/// checkpointed after every VAD chunk, so a deferral or crash resumes without redoing finished audio.
/// </summary>
public sealed class AsrJobHandler(
    LibraryService library,
    IAudioSampleReaderFactory readers,
    IAsrEngineSelector engines,
    ISettingsStore settings,
    GlossaryService glossaries,
    TimeProvider time) : IJobHandler
{
    public const string JobKind = "asr";

    /// <summary>Start of the error when no model is installed (<see cref="AsrModelReadyRetry"/> looks for it).</summary>
    public const string NoModelMessage = "No speech recognition model is installed.";

    /// <summary>Payload flag: replace an existing transcript (user-requested re-transcription).</summary>
    public const string ReplacePayload = "{\"replace\":true}";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => JobKind;

    public JobResourceClass ResourceClass => JobResourceClass.Heavy;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = context.Job.RecordingId ?? throw new PermanentJobException("The job has no recording.");
        var replace = context.Job.Payload == ReplacePayload;

        var existing = await library.LoadTranscriptAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !replace)
        {
            // Idempotent: a previous run (e.g. before a crash) already saved the transcript.
            return;
        }

        var metadata = await library.GetMetadataAsync(id, cancellationToken).ConfigureAwait(false);
        var folder = await library.GetRecordingPathAsync(id, cancellationToken).ConfigureAwait(false);
        var language = metadata.Language ?? settings.Current.Processing.TranscriptionLanguage;

        var choice = await engines.SelectAsync(language, cancellationToken).ConfigureAwait(false)
            ?? throw new PermanentJobException(NoModelMessage + " Install one in Settings › Models; this job is retried when it is ready.");

        var state = context.Job.Checkpoint is { } cp
            ? JsonSerializer.Deserialize<AsrCheckpoint>(cp, Json) ?? new AsrCheckpoint()
            : new AsrCheckpoint();
        if (state.ModelId is not null && state.ModelId != choice.ModelId)
        {
            state = new AsrCheckpoint(); // a different model must not mix with partial results
        }

        state.ModelId = choice.ModelId;
        var streams = metadata.Audio
            .Where(a => a.Analysis?.Silent != true && File.Exists(Path.Combine(folder, a.FileName)))
            .ToList();
        var totalSeconds = Math.Max(1, metadata.DurationSeconds ?? 1) * Math.Max(1, streams.Count);

        string engineDescription;
        var glossary = await glossaries.LoadForProjectAsync(metadata.Project, cancellationToken).ConfigureAwait(false);
        var session = await choice.Provider.OpenAsync(
            new AsrSessionOptions(choice.ModelId, language, context.Budget.MaxThreads, context.Budget.PreferEfficiency),
            cancellationToken).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            engineDescription = session.EngineDescription;
            await context.SetEngineAsync(engineDescription).ConfigureAwait(false);
            for (var s = 0; s < streams.Count; s++)
            {
                var stream = streams[s];
                var progressBase = s * (metadata.DurationSeconds ?? 0);
                var done = state.Streams.GetValueOrDefault(stream.FileName) ?? new StreamProgress();
                state.Streams[stream.FileName] = done;
                var prompts = new AsrPromptBuilder(glossary, language ?? state.DetectedLanguage);

                using var reader = readers.Open(Path.Combine(folder, stream.FileName));
                foreach (var chunk in SpeechChunker.Split(reader, cancellationToken))
                {
                    if (chunk.Index < done.NextChunk)
                    {
                        continue;
                    }

                    var prompt = prompts.Next(RecentText(done.Segments));
                    var result = await session.TranscribeAsync(chunk.Samples, prompt, cancellationToken).ConfigureAwait(false);
                    state.DetectedLanguage ??= result.DetectedLanguage;
                    prompts.Observe(glossary.Apply(string.Concat(result.Segments.Select(x => x.Text))));
                    foreach (var segment in result.Segments.Where(x => !string.IsNullOrWhiteSpace(x.Text)))
                    {
                        done.Segments.Add(new SavedSegment(
                            chunk.StartSeconds + segment.StartSeconds,
                            chunk.StartSeconds + Math.Max(segment.EndSeconds, segment.StartSeconds),
                            segment.Text.Trim(),
                            segment.Confidence,
                            segment.Words?.Select(w => new SavedWord(w.Text, chunk.StartSeconds + w.StartSeconds, chunk.StartSeconds + w.EndSeconds, w.Confidence)).ToList()));
                    }

                    done.NextChunk = chunk.Index + 1;
                    await context.SaveCheckpointAsync(JsonSerializer.Serialize(state, Json)).ConfigureAwait(false);
                    await context.ReportProgressAsync(
                        Math.Min(0.99, (progressBase + chunk.StartSeconds + chunk.DurationSeconds) / totalSeconds),
                        stream.FileName).ConfigureAwait(false);
                }
            }
        }

        var transcript = BuildTranscript(id, metadata, streams, state, language, choice.ModelId, engineDescription);
        var saved = await library.SaveTranscriptAsync(transcript, replace ? ConflictPolicy.PreserveAndOverwrite : ConflictPolicy.Fail, cancellationToken)
            .ConfigureAwait(false);
        if (!saved.Saved)
        {
            throw new PermanentJobException(
                $"{string.Join(", ", saved.ConflictingFiles)} was edited outside Kakitome and was not overwritten. " +
                "Review the file, then use Re-transcribe to replace it.");
        }

        if (metadata.Language is null && transcript.Language is not null)
        {
            await library.UpdateMetadataAsync(id, m => m.Language ??= transcript.Language, cancellationToken).ConfigureAwait(false);
        }
    }

    private TranscriptDocument BuildTranscript(
        Domain.Recordings.RecordingId id,
        RecordingMetadata metadata,
        List<AudioStreamInfo> streams,
        AsrCheckpoint state,
        string? language,
        string modelId,
        string engineDescription)
    {
        var now = time.GetLocalNow();
        var multiSource = streams.Count > 1;
        var speakers = multiSource
            ? streams.Select(s => new SpeakerInfo { Id = SpeakerId(s), Label = SpeakerLabel(s, language ?? state.DetectedLanguage) }).ToList()
            : [];

        var merged = streams
            .SelectMany(s => (state.Streams.GetValueOrDefault(s.FileName)?.Segments ?? []).Select(seg => (Stream: s, Segment: seg)))
            .OrderBy(x => x.Segment.Start)
            .ThenBy(x => x.Stream.Role)
            .Select((x, i) => new TranscriptSegment
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"s{i + 1:D5}"),
                StartSeconds = Math.Round(x.Segment.Start, 3),
                EndSeconds = Math.Round(x.Segment.End, 3),
                Text = x.Segment.Text,
                Speaker = multiSource ? SpeakerId(x.Stream) : null,
                Confidence = x.Segment.Confidence is { } c ? Math.Round(c, 3) : null,
                Words = x.Segment.Words?.Select(w => new TranscriptWord
                {
                    Text = w.Text,
                    StartSeconds = Math.Round(w.Start, 3),
                    EndSeconds = Math.Round(w.End, 3),
                    Confidence = w.Confidence,
                }).ToList(),
            })
            .ToList();

        var engine = new EngineInfo { Provider = engineDescription, Model = modelId, Locality = "local" };
        var confidences = merged.Where(m => m.Confidence is not null).Select(m => m.Confidence!.Value).ToList();
        var warnings = new List<string>();
        if (streams.Count == 0 || merged.Count == 0)
        {
            warnings.Add("noSpeechDetected");
        }

        return new TranscriptDocument
        {
            RecordingId = id,
            Language = language ?? state.DetectedLanguage ?? metadata.Language,
            Revision = 1,
            Kind = TranscriptKind.Raw,
            CreatedAt = now,
            UpdatedAt = now,
            Engine = engine,
            Lineage = [new TranscriptLineageEntry { Kind = TranscriptKind.Raw, Revision = 1, At = now, Source = "asr", Engine = engine }],
            Speakers = speakers,
            Segments = merged,
            Quality = new TranscriptQuality
            {
                MeanConfidence = confidences.Count == 0 ? null : Math.Round(confidences.Average(), 3),
                Warnings = warnings.Count == 0 ? null : warnings,
            },
        };
    }

    internal static string SpeakerId(AudioStreamInfo stream) => stream.Role switch
    {
        AudioStreamRole.Microphone => "mic",
        AudioStreamRole.SystemAudio => "system",
        AudioStreamRole.Application => "app",
        _ => "audio",
    };

    private static string SpeakerLabel(AudioStreamInfo stream, string? language)
    {
        var ja = language?.StartsWith("ja", StringComparison.OrdinalIgnoreCase) == true;
        return stream.Role switch
        {
            AudioStreamRole.Microphone => ja ? "マイク" : "Microphone",
            AudioStreamRole.SystemAudio => ja ? "システム音声" : "System audio",
            AudioStreamRole.Application => ja ? "アプリの音声" : "App audio",
            _ => ja ? "音声" : "Audio",
        };
    }

    /// <summary>The end of the text recognized so far (the prompt builder trims it to its budget).</summary>
    private static string? RecentText(List<SavedSegment> segments)
    {
        if (segments.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (var i = segments.Count - 1; i >= 0 && sb.Length < 400; i--)
        {
            sb.Insert(0, segments[i].Text);
        }

        return sb.ToString();
    }

    private sealed class AsrCheckpoint
    {
        public string? ModelId { get; set; }

        public string? DetectedLanguage { get; set; }

        public Dictionary<string, StreamProgress> Streams { get; set; } = [];
    }

    private sealed class StreamProgress
    {
        public int NextChunk { get; set; }

        public List<SavedSegment> Segments { get; set; } = [];
    }

    private sealed record SavedSegment(double Start, double End, string Text, double? Confidence, List<SavedWord>? Words);

    private sealed record SavedWord(string Text, double Start, double End, double? Confidence);
}
