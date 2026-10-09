using Kakitome.Application.Asr;
using Kakitome.Application.Models;
using Kakitome.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Kakitome.Tests.Asr;

public sealed class AutoModelInstallerTests
{
    [Fact]
    public async Task Missing_recommended_model_and_yt_dlp_are_queued_once()
    {
        var store = new FakeModelStore();
        await using var f = await CreateAsync(store);
        var installer = f.Services.GetRequiredService<AutoModelInstaller>();

        var started = await installer.EnsureAsync();

        // Without a capable GPU the CPU-friendly model is recommended (ADR-022); with 8 GB no summary model.
        Assert.Equal([AsrDefaults.RecommendedModel(hasCapableGpu: false), ModelCatalog.YtDlp], started);
        var jobs = await f.Scheduler.ListAsync();
        Assert.Equal(2, jobs.Count(j => j.Kind == ModelInstallJobHandler.JobKind));

        // Started again (e.g. the next launch) while still queued: nothing is added.
        Assert.Empty(await installer.EnsureAsync());
        Assert.Equal(2, (await f.Scheduler.ListAsync()).Count(j => j.Kind == ModelInstallJobHandler.JobKind));
    }

    [Fact]
    public async Task Nothing_is_downloaded_when_present_turned_off_or_metered()
    {
        var store = new FakeModelStore();
        store.Installed.Add(ModelCatalog.ReazonSpeechK2V2Int8); // any installed ASR model is enough
        await using var f = await CreateAsync(store);
        await f.Settings.UpdateAsync(s => s.Processing.YtDlpPath = @"C:\Tools\yt-dlp.exe"); // the user's own yt-dlp
        var installer = f.Services.GetRequiredService<AutoModelInstaller>();
        Assert.Empty(installer.MissingDownloads());

        store.Installed.Clear();
        f.Network.IsMetered = true;
        Assert.NotEmpty(installer.MissingDownloads());
        Assert.Empty(await installer.EnsureAsync());

        f.Network.IsMetered = false;
        await f.Settings.UpdateAsync(s => s.Processing.AutoDownloadModels = false);
        Assert.Empty(await installer.EnsureAsync());
        Assert.DoesNotContain(await f.Scheduler.ListAsync(), j => j.Kind == ModelInstallJobHandler.JobKind);
    }

    [Fact]
    public async Task Summary_model_is_added_with_enough_memory_unless_summaries_are_extractive()
    {
        var store = new FakeModelStore();
        store.Installed.Add(ModelCatalog.WhisperSmallQ5);
        store.Installed.Add(ModelCatalog.YtDlp);
        var hardware = new NoGpu { TotalMemoryBytes = 16L * 1024 * 1024 * 1024 };
        await using var f = await LibraryFixture.CreateAsync(configure: services =>
        {
            services.AddSingleton<IModelStore>(store);
            services.AddSingleton<IAccelerationProbe>(hardware);
        });
        var installer = f.Services.GetRequiredService<AutoModelInstaller>();
        Assert.Equal([ModelCatalog.Qwen3Instruct4B], installer.MissingDownloads());

        await f.Settings.UpdateAsync(s => s.Processing.SummaryEngine = Kakitome.Application.Settings.SummaryEngines.Extractive);
        Assert.Empty(installer.MissingDownloads());

        await f.Settings.UpdateAsync(s => s.Processing.SummaryEngine = Kakitome.Application.Settings.SummaryEngines.Auto);
        store.Installed.Add(ModelCatalog.Phi4MiniInstruct); // any summary model is enough
        Assert.Empty(installer.MissingDownloads());
    }

    private static Task<LibraryFixture> CreateAsync(FakeModelStore store) =>
        LibraryFixture.CreateAsync(configure: services => services.AddSingleton<IModelStore>(store));
}
