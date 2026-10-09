using Kakitome.Application.Asr;
using Kakitome.Application.Models;

namespace Kakitome.Tests.Asr;

/// <summary>Deterministic ASR: one segment per chunk, text = "chunk{n}@{start}". Can fail on a given call.</summary>
public sealed class FakeAsrProvider : IFinalAsrProvider
{
    public const string ProviderId = "fake-asr";

    private int _calls;

    public string Id => ProviderId;

    public IReadOnlyList<AsrModelInfo> Models { get; } = [new("fake-model", "Fake", ["*"], 1, "test")];

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Throw a transient failure on this 1-based call number (once).</summary>
    public int? FailOnCall { get; set; }

    public List<string?> Prompts { get; } = [];

    public string? Language { get; set; } = "ja";

    public Task<IAsrSession> OpenAsync(AsrSessionOptions options, CancellationToken cancellationToken = default) =>
        Task.FromResult<IAsrSession>(new Session(this));

    private sealed class Session(FakeAsrProvider owner) : IAsrSession
    {
        public string EngineDescription => "fake-asr 1.0";

        public Task<AsrChunkResult> TranscribeAsync(ReadOnlyMemory<float> samples, string? prompt, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref owner._calls);
            lock (owner.Prompts)
            {
                owner.Prompts.Add(prompt);
            }

            if (owner.FailOnCall == call)
            {
                owner.FailOnCall = null;
                throw new IOException("simulated engine crash");
            }

            var seconds = samples.Length / (double)SpeechChunker.SampleRate;
            return Task.FromResult(new AsrChunkResult(
                [new AsrSegment(0.5, Math.Max(0.6, seconds - 0.5), $"発話{call}", 0.9)],
                owner.Language));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Model store where every model in the fake catalog is "installed".</summary>
public sealed class FakeModelStore : IModelStore
{
    public HashSet<string> Installed { get; } = [];

    public string RootPath => Path.GetTempPath();

    public ModelState GetState(string modelId) => Installed.Contains(modelId) ? ModelState.Installed : ModelState.NotInstalled;

    public string GetDirectory(string modelId) => RootPath;

    public Task InstallAsync(string modelId, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Installed.Add(modelId);
        return Task.CompletedTask;
    }

    public Task<bool> VerifyAsync(string modelId, CancellationToken cancellationToken = default) => Task.FromResult(Installed.Contains(modelId));

    public Task RemoveAsync(string modelId, CancellationToken cancellationToken = default)
    {
        Installed.Remove(modelId);
        return Task.CompletedTask;
    }

    public long GetUsedBytes() => 0;
}

/// <summary>Always selects the fake provider (or nothing when <see cref="Available"/> is false).</summary>
public sealed class FakeEngineSelector(FakeAsrProvider provider) : IAsrEngineSelector
{
    public bool Available { get; set; } = true;

    public Task<AsrEngineChoice?> SelectAsync(string? language, CancellationToken cancellationToken = default) =>
        Task.FromResult(Available ? new AsrEngineChoice(provider, "fake-model") : null);
}
