using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Kakitome.Application.Asr;
using Kakitome.Application.Models;
using Kakitome.Infrastructure.Asr;
using Kakitome.Storage;
using Kakitome.Storage.Models;
using Kakitome.Storage.Settings;

namespace Kakitome.Tests.Asr;

public sealed class ModelStoreTests : IDisposable
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("model-bytes-", 5000)));
    private static readonly byte[] Tokens = Encoding.UTF8.GetBytes("あ 1\nい 2\n");

    private readonly AppDataPaths _paths = new(Path.Combine(Path.GetTempPath(), "KakitomeTests", "models-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task Install_downloads_verifies_and_marks_installed()
    {
        var server = new FakeServer();
        var store = CreateStore(server, Model(Sha(Payload)));

        Assert.Equal(ModelState.NotInstalled, store.GetState("m"));
        var reports = new List<double>();
        await store.InstallAsync("m", new SyncProgress(reports.Add), TestContext.Current.CancellationToken);

        Assert.Equal(ModelState.Installed, store.GetState("m"));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(Path.Combine(store.GetDirectory("m"), "weights.bin"), TestContext.Current.CancellationToken));
        Assert.Equal(1, reports[^1]);
        Assert.True(await store.VerifyAsync("m", TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(store.GetDirectory("m"), "*.partial"));
    }

    [Fact]
    public async Task The_store_can_install_every_catalog_item()
    {
        var store = new ModelStore(_paths, new FakeServer(), NullLogger<ModelStore>.Instance);
        foreach (var model in ModelCatalog.Everything)
        {
            // An unknown id throws ArgumentException before any download; a known one reaches the (failing) fake server.
            var error = await Record.ExceptionAsync(() => store.InstallAsync(model.Id, null, TestContext.Current.CancellationToken));
            Assert.IsNotType<ArgumentException>(error);
        }
    }

    [Fact]
    public async Task Tampered_download_is_discarded_and_never_marked_installed()
    {
        var server = new FakeServer { Corrupt = true };
        var store = CreateStore(server, Model(Sha(Payload)));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync("m", null, TestContext.Current.CancellationToken));

        Assert.Equal(ModelState.NotInstalled, store.GetState("m"));
        Assert.False(File.Exists(Path.Combine(store.GetDirectory("m"), "weights.bin")));
    }

    [Fact]
    public async Task Interrupted_download_resumes_with_a_range_request()
    {
        var server = new FakeServer();
        var store = CreateStore(server, Model(Sha(Payload)));
        Directory.CreateDirectory(store.GetDirectory("m"));
        await File.WriteAllBytesAsync(Path.Combine(store.GetDirectory("m"), "weights.bin.partial"), Payload[..1000], TestContext.Current.CancellationToken);

        await store.InstallAsync("m", null, TestContext.Current.CancellationToken);

        Assert.Equal(1000, server.RangeFrom);
        Assert.Equal(ModelState.Installed, store.GetState("m"));
    }

    [Fact]
    public async Task Changed_files_make_the_model_invalid_and_verification_catches_same_size_tampering()
    {
        var store = CreateStore(new FakeServer(), Model(Sha(Payload)));
        await store.InstallAsync("m", null, TestContext.Current.CancellationToken);
        var path = Path.Combine(store.GetDirectory("m"), "weights.bin");

        var bytes = (byte[])Payload.Clone();
        bytes[10] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        Assert.False(await store.VerifyAsync("m", TestContext.Current.CancellationToken));
        Assert.Equal(ModelState.NotInstalled, store.GetState("m"));

        await File.WriteAllBytesAsync(path, Payload[..100], TestContext.Current.CancellationToken);
        await store.InstallAsync("m", null, TestContext.Current.CancellationToken);
        File.WriteAllBytes(path, Payload[..100]);
        Assert.Equal(ModelState.Invalid, store.GetState("m"));
    }

    [Fact]
    public async Task Small_files_are_verified_by_git_blob_id()
    {
#pragma warning disable CA5350 // Recomputes a git object id, which is defined as SHA-1.
        var blob = Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {Tokens.Length}\0"), .. Tokens]));
#pragma warning restore CA5350
        var file = new ModelFile("tokens.txt", new Uri("https://example.test/tokens.txt"), Tokens.Length, Sha256: null, GitBlobSha1: blob);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(path, Tokens, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(await ModelStore.HashMatchesAsync(path, file, TestContext.Current.CancellationToken));
            Assert.False(await ModelStore.HashMatchesAsync(path, file with { GitBlobSha1 = new string('0', 40) }, TestContext.Current.CancellationToken));
            Assert.False(await ModelStore.HashMatchesAsync(path, file with { GitBlobSha1 = null }, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Tools_are_installable_but_never_offered_as_speech_models()
    {
        Assert.NotNull(ModelCatalog.Find(ModelCatalog.YtDlp));
        Assert.DoesNotContain(ModelCatalog.All, m => m.Id == ModelCatalog.YtDlp);
        Assert.Equal("yt-dlp.exe", Assert.Single(ModelCatalog.Find(ModelCatalog.YtDlp)!.Files).Name);
    }

    [Fact]
    public async Task Remove_deletes_only_the_model_folder()
    {
        var store = CreateStore(new FakeServer(), Model(Sha(Payload)));
        await store.InstallAsync("m", null, TestContext.Current.CancellationToken);

        await store.RemoveAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(ModelState.NotInstalled, store.GetState("m"));
        Assert.True(Directory.Exists(store.RootPath));
    }

    [Fact]
    public void Catalog_pins_every_file_by_size_and_hash()
    {
        Assert.All(ModelCatalog.All.Concat(ModelCatalog.Tools).Concat(ModelCatalog.SummaryModels)
            .Append(ModelCatalog.YtDlpFor(System.Runtime.InteropServices.Architecture.Arm64))
            .Append(ModelCatalog.YtDlpFor(System.Runtime.InteropServices.Architecture.X64))
            .SelectMany(m => m.Files), f =>
        {
            Assert.True(f.Size > 0);
            Assert.True(f.Sha256 is { Length: 64 } || f.GitBlobSha1 is { Length: 40 }, f.Name);
            Assert.Equal("https", f.Url.Scheme);
        });
    }

    [Fact]
    public async Task Selector_prefers_the_users_choice_then_the_benchmark_order_and_respects_languages()
    {
        var models = new FakeModelStore();
        var whisper = new NamedProvider("whisper.cpp");
        var sherpa = new NamedProvider("sherpa-onnx");
        using var settings = new JsonSettingsStore(_paths, TimeProvider.System, NullLogger<JsonSettingsStore>.Instance);
        var selector = new AsrEngineSelector([whisper, sherpa], models, settings, new FixedProbe(HasGpu: false));

        Assert.Null(await selector.SelectAsync("ja", TestContext.Current.CancellationToken));

        models.Installed.Add(ModelCatalog.ReazonSpeechK2V2Int8);
        Assert.Equal(ModelCatalog.ReazonSpeechK2V2Int8, (await selector.SelectAsync("ja", TestContext.Current.CancellationToken))!.ModelId);
        Assert.Null(await selector.SelectAsync("en", TestContext.Current.CancellationToken)); // Japanese-only model
        Assert.Null(await selector.SelectAsync(null, TestContext.Current.CancellationToken));

        models.Installed.Add(ModelCatalog.WhisperSmallQ5);
        Assert.Equal(ModelCatalog.WhisperSmallQ5, (await selector.SelectAsync("en", TestContext.Current.CancellationToken))!.ModelId);

        await settings.UpdateAsync(s => s.Processing.AsrModelId = ModelCatalog.WhisperSmallQ5, TestContext.Current.CancellationToken);
        Assert.Equal(ModelCatalog.WhisperSmallQ5, (await selector.SelectAsync("ja", TestContext.Current.CancellationToken))!.ModelId);
    }

    private ModelStore CreateStore(FakeServer server, ModelDescriptor model) =>
        new(_paths, server, NullLogger<ModelStore>.Instance, [model]);

    private static ModelDescriptor Model(string sha) => new(
        "m", "test", "Test model",
        [new ModelFile("weights.bin", new Uri("https://example.test/weights.bin"), Payload.Length, sha)],
        "test", new Uri("https://example.test"), ["*"], 1);

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Fact]
    public void Defaults_follow_the_benchmark_per_hardware()
    {
        Assert.Equal(ModelCatalog.WhisperLargeV3TurboQ5, AsrDefaults.PreferenceFor("ja", hasCapableGpu: true)[0]);
        Assert.Equal(ModelCatalog.WhisperSmallQ5, AsrDefaults.PreferenceFor("ja", hasCapableGpu: false)[0]);
        Assert.Contains(ModelCatalog.ReazonSpeechK2V2Int8, AsrDefaults.PreferenceFor("ja", hasCapableGpu: false));
        Assert.DoesNotContain(ModelCatalog.ReazonSpeechK2V2Int8, AsrDefaults.PreferenceFor("en", hasCapableGpu: false));
    }

    [Fact]
    public void Only_a_discrete_GPU_with_enough_memory_is_used_and_the_largest_wins()
    {
        const long gb = 1024L * 1024 * 1024;
        GpuDevice Device(int index, GpuDeviceType type, long memory) => new(index, $"gpu{index}", type, memory);

        // A laptop: the integrated GPU is enumerated first and may even report a large shared heap.
        Assert.Equal(1, GpuAccelerationProbe.Choose([Device(0, GpuDeviceType.Integrated, 8 * gb), Device(1, GpuDeviceType.Discrete, 6 * gb)])?.Index);
        Assert.Null(GpuAccelerationProbe.Choose([Device(0, GpuDeviceType.Integrated, 16 * gb)]));
        Assert.Null(GpuAccelerationProbe.Choose([Device(0, GpuDeviceType.Discrete, 1 * gb)])); // too small (e.g. 1 GB MX)
        Assert.Null(GpuAccelerationProbe.Choose([Device(0, GpuDeviceType.Cpu, 64 * gb)])); // software Vulkan
        Assert.Equal(2, GpuAccelerationProbe.Choose([Device(0, GpuDeviceType.Discrete, 4 * gb), Device(2, GpuDeviceType.Discrete, 12 * gb)])?.Index);
        Assert.Null(GpuAccelerationProbe.Choose([]));
    }

    [Fact]
    public void The_probe_reads_this_PCs_GPUs_through_Vulkan()
    {
        var probe = new GpuAccelerationProbe();
        if (probe.Devices.Count == 0)
        {
            return; // no Vulkan driver on this machine: nothing to check (the app then stays on the CPU)
        }

        Assert.All(probe.Devices, d => Assert.False(string.IsNullOrEmpty(d.Name)));
        if (probe.Gpu is { } gpu)
        {
            Assert.Equal(GpuDeviceType.Discrete, gpu.Type);
            Assert.True(probe.GpuMemoryBytes >= GpuAccelerationProbe.MinimumGpuMemoryBytes);
        }
    }

    private sealed record FixedProbe(bool HasGpu) : IAccelerationProbe
    {
        public bool HasCapableGpu => HasGpu;

        public long GpuMemoryBytes => HasGpu ? 8L * 1024 * 1024 * 1024 : 0;

        public long TotalMemoryBytes => 16L * 1024 * 1024 * 1024;
    }

    private sealed class NamedProvider(string id) : IFinalAsrProvider
    {
        public string Id { get; } = id;

        public IReadOnlyList<AsrModelInfo> Models { get; } = [];

        public Task<IAsrSession> OpenAsync(AsrSessionOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>In-memory HTTP server honoring Range requests.</summary>
    private sealed class FakeServer : HttpMessageHandler, IHttpClientFactoryLite
    {
        public bool Corrupt { get; set; }

        public long? RangeFrom { get; private set; }

        public HttpClient Create() => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = (byte[])Payload.Clone();
            if (Corrupt)
            {
                body[^1] ^= 0xFF;
            }

            var from = request.Headers.Range?.Ranges.First().From ?? 0;
            RangeFrom = request.Headers.Range is null ? null : from;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body[(int)from..]),
            };
            return Task.FromResult(response);
        }
    }
}
