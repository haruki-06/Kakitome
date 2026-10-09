using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Kakitome.Application.Asr;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Models;
using Kakitome.Domain.Library;
using Kakitome.Infrastructure.Asr;
using Kakitome.Storage;
using Kakitome.Storage.Models;
using Kakitome.Tests.Jobs;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Asr;

/// <summary>
/// End-to-end with real local models (installed in the user's model folder by the Model Manager / benchmark tool) and
/// the synthetic corpus. Skipped when a model or the corpus is not present on the machine.
/// </summary>
[Trait("Category", "Model")]
public sealed class RealModelTests
{
    [Theory]
    [InlineData(ModelCatalog.ReazonSpeechK2V2Int8)]
    [InlineData(ModelCatalog.WhisperSmallQ5)]
    [InlineData(ModelCatalog.WhisperLargeV3TurboQ5)]
    public async Task Recording_is_transcribed_by_a_real_local_model(string modelId)
    {
        var realStore = new ModelStore(AppDataPaths.Default, new SharedHttpClientFactory(), NullLogger<ModelStore>.Instance);
        Assert.SkipUnless(realStore.GetState(modelId) == ModelState.Installed, $"{modelId} is not installed on this machine.");
        var corpusFile = Path.Combine(RepositoryPaths.Root, "benchmarks", "corpus", "v1", "lecture-01.clean.wav");
        Assert.SkipUnless(File.Exists(corpusFile), "Benchmark corpus not generated (Kakitome.Bench corpus).");

        await using var f = await LibraryFixture.CreateAsync(configure: services =>
        {
            services.AddSingleton<IModelStore>(realStore);
            services.AddSingleton<IFinalAsrProvider, WhisperCppProvider>();
            services.AddSingleton<IFinalAsrProvider, SherpaOnnxProvider>();
        });
        await f.Settings.UpdateAsync(s =>
        {
            s.Processing.AsrModelId = modelId;
            s.Processing.TranscriptionLanguage = "ja";
        });

        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Project = "E2E", RecordedAt = Samples.Jst });
        var folder = await f.Library.GetRecordingPathAsync(entry.Id);
        File.Copy(corpusFile, Path.Combine(folder, "audio.wav"));
        await f.Library.UpdateMetadataAsync(entry.Id, m =>
        {
            m.Capture!.Status = CaptureStatus.Completed;
            m.Audio = [new AudioStreamInfo { FileName = "audio.wav", Role = AudioStreamRole.Microphone }];
        });

        var job = await f.Scheduler.EnqueueAsync(new JobRequest(AsrJobHandler.JobKind) { RecordingId = entry.Id });
        await JobSchedulerTests.RunUntilSettledAsync(f, passes: 3);

        var done = (await f.Jobs.GetAsync(job.Id))!;
        Assert.True(done.State == JobState.Succeeded, done.LastError);
        Assert.Contains(modelId, done.Engine, StringComparison.Ordinal);

        var transcript = (await f.Library.LoadTranscriptAsync(entry.Id))!;
        var text = string.Concat(transcript.Segments.Select(s => s.Text));
        var cer = AsrMetrics.Cer("それでは今日の講義を始めます。前回は線形回帰の考え方を説明しましたが、今回は勾配降下法について詳しく見ていきます。", text);
        Assert.True(cer.Rate < 0.25, $"CER {cer.Rate:P1}: {text}");
        Assert.All(transcript.Segments, s => Assert.True(s.EndSeconds >= s.StartSeconds));
        Assert.True(File.Exists(Path.Combine(folder, LibraryLayout.TranscriptMarkdownFile)));
    }
}
