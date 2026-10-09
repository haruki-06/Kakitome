using Microsoft.Extensions.DependencyInjection;
using Kakitome.Application.Asr;
using Kakitome.Application.Jobs;
using Kakitome.Application.Models;
using Kakitome.Application.Recording;
using Kakitome.Infrastructure.Asr;
using Kakitome.Infrastructure.Audio;
using Kakitome.Infrastructure.FileSystem;
using Kakitome.Infrastructure.Power;

namespace Kakitome.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers Windows-specific adapters (audio, power, OS integration, model runtimes).</summary>
    public static IServiceCollection AddKakitomeInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<Kakitome.Application.Updates.IReleaseFeed>(_ => new Kakitome.Infrastructure.Updates.GitHubReleaseFeed());
        services.AddSingleton<Kakitome.Application.Diagnostics.IDiagnosticsSource, Diagnostics.HardwareDiagnostics>();
        services.AddSingleton<Kakitome.Application.Diagnostics.IDiagnosticsSource, Diagnostics.AudioDiagnostics>();
        services.AddSingleton<Kakitome.Application.Diagnostics.IDiagnosticsSource, Diagnostics.WindowsErrorReports>();
        services.AddSingleton<IAudioDeviceCatalog, WasapiDeviceCatalog>();
        services.AddSingleton<IAudioCaptureFactory, WasapiCaptureFactory>();
        services.AddSingleton<IKeepAwake, WindowsKeepAwake>();
        services.AddSingleton<IPowerEvents, WindowsPowerEvents>();
        services.AddSingleton<IRecycleBin, WindowsRecycleBin>();
        services.AddSingleton<ISystemResourceProbe, WindowsSystemResourceProbe>();
        services.AddSingleton<IAccelerationProbe, GpuAccelerationProbe>();
        services.AddSingleton<Kakitome.Application.Models.INetworkCostProbe, Network.WindowsNetworkCostProbe>();
        services.AddSingleton<Kakitome.Application.Audio.IAudioSampleReaderFactory, MediaAudioSampleReaderFactory>();
        services.AddSingleton<Kakitome.Application.Import.IMediaUrlDownloader, Import.YtDlpDownloader>();
        services.AddSingleton<Kakitome.Application.Audio.IAudioEncoder, MediaFoundationAudioEncoder>();
        services.AddSingleton<IFinalAsrProvider>(sp => new WhisperCppProvider(sp.GetRequiredService<IModelStore>(), sp.GetRequiredService<IAccelerationProbe>()));
        services.AddSingleton<IFinalAsrProvider, SherpaOnnxProvider>();
        services.AddSingleton<Kakitome.Application.Summaries.ISummaryProvider, Summaries.LlamaSummaryProvider>();
        return services;
    }
}
