using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Kakitome.Application.Asr;
using Kakitome.Application.Audio;
using Kakitome.Application.Decision;
using Kakitome.Application.Search;
using Kakitome.Application.Summaries;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Recording;

namespace Kakitome.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers application-layer services (use cases, policies).</summary>
    public static IServiceCollection AddKakitomeApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<LibraryOutputOptions>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<GlossaryService>();
        services.AddSingleton<GlossaryTips>();
        services.AddSingleton<Kakitome.Application.Models.AutoModelInstaller>();
        services.AddSingleton<Kakitome.Application.Asr.AsrModelReadyRetry>();
        services.AddSingleton<Kakitome.Application.Updates.UpdateChecker>();
        services.AddSingleton<Kakitome.Application.Diagnostics.DiagnosticsService>();
        services.AddSingleton<RecordingService>();
        services.AddSingleton<RecordingRecovery>();

        services.AddSingleton<IResourceMonitor, ResourceMonitor>();
        services.AddSingleton<JobScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<JobScheduler>());
        services.AddSingleton<ProcessingPipeline>();
        services.AddSingleton<IJobHandler, AnalyzeAudioJobHandler>();
        services.AddSingleton<IJobHandler, AsrJobHandler>();
        services.AddSingleton<IAsrEngineSelector, AsrEngineSelector>();
        services.AddSingleton<IJobHandler, Kakitome.Application.Models.ModelInstallJobHandler>();

        services.AddSingleton<IDecisionEngine, RuleBasedDecisionEngine>();
        services.AddSingleton<IJobHandler, CleanupJobHandler>();
        services.AddSingleton<ISummaryProvider, ExtractiveSummaryProvider>();
        services.AddSingleton<IJobHandler, SummaryJobHandler>();
        services.AddSingleton<SearchService>();
        services.AddSingleton<IJobHandler, IndexJobHandler>();
        services.AddSingleton<RecordingActions>();
        services.AddSingleton<Kakitome.Application.Maintenance.MaintenanceService>();
        services.AddSingleton<Kakitome.Application.Live.LiveTranscriptionService>();
        services.AddSingleton<Kakitome.Application.Backup.BackupService>();
        services.AddSingleton<Kakitome.Application.Backup.AutomaticBackupPolicy>();
        services.AddSingleton<IJobHandler, Kakitome.Application.Backup.BackupJobHandler>();
        services.AddSingleton<IJobHandler, Kakitome.Application.Backup.RestoreJobHandler>();
        services.AddSingleton<Kakitome.Application.Import.ImportService>();
        services.AddSingleton<IJobHandler, Kakitome.Application.Import.ImportFileJobHandler>();
        services.AddSingleton<IJobHandler, Kakitome.Application.Import.ImportUrlJobHandler>();
        services.AddSingleton<IJobHandler, RetentionJobHandler>();
        services.TryAddSingleton<IAudioEncoder, UnavailableAudioEncoder>();
        services.TryAddSingleton<Kakitome.Application.Import.IMediaUrlDownloader, Kakitome.Application.Import.UnavailableMediaUrlDownloader>();
        return services;
    }
}
