using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Kakitome.Application.Audio;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Models;
using Kakitome.Storage.Models;
using Kakitome.Application.Recording;
using Kakitome.Storage.Audio;
using Kakitome.Storage.Library;
using Kakitome.Storage.Persistence;
using Kakitome.Storage.Settings;
using Kakitome.Application.Settings;

namespace Kakitome.Storage;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Library file store, the SQLite database and the derived index.</summary>
    public static IServiceCollection AddKakitomeStorage(this IServiceCollection services, AppDataPaths? paths = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        paths ??= AppDataPaths.Default;

        services.AddSingleton(paths);
        services.AddSingleton<Kakitome.Application.Maintenance.IAppLocations>(new AppLocations(paths));
        services.TryAddSingleton(TimeProvider.System);

        var database = new DatabaseLocation(paths.DatabaseFile);
        services.AddSingleton(database);
        services.AddDbContextFactory<KakitomeDbContext>(o => o.UseSqlite(database.ConnectionString));
        services.AddSingleton<KakitomeDatabase>();

        services.AddOptions<LibraryLocationOptions>();
        services.AddSingleton<ILibraryStore, FileSystemLibraryStore>();
        services.AddSingleton<ILibraryIndex, EfLibraryIndex>();
        services.AddSingleton<IJobStore, EfJobStore>();
        services.AddSingleton<Kakitome.Application.Search.ISearchIndex, SqliteSearchIndex>();
        services.AddSingleton(new Kakitome.Application.Import.ImportWorkspace(Path.Combine(paths.CacheDirectory, "import")));

        services.AddSingleton<IAudioFileWriterFactory, WavFileWriterFactory>();
        services.AddSingleton<IAudioFileRepair, WavRepair>();
        services.AddSingleton<IAudioSampleReaderFactory, WavSampleReaderFactory>();
        services.AddSingleton<IDiskSpaceProbe, DriveDiskSpaceProbe>();

        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.TryAddSingleton<IHttpClientFactoryLite, SharedHttpClientFactory>();
        services.AddSingleton<IModelStore, ModelStore>();

        services.AddSingleton<LibraryBootstrapper>();
        services.AddHostedService(sp => sp.GetRequiredService<LibraryBootstrapper>());
        return services;
    }
}
