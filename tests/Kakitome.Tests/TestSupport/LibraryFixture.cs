using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kakitome.Application;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Recording;
using Kakitome.Storage;
using Kakitome.Storage.Library;
using Kakitome.Storage.Persistence;

namespace Kakitome.Tests.TestSupport;

/// <summary>
/// A real Library folder + SQLite database in a temporary directory, wired exactly like the app.
/// </summary>
public sealed class LibraryFixture : IAsyncDisposable
{
    private ServiceProvider _provider;

    private readonly IAudioCaptureFactory? _realCapture;
    private readonly Action<IServiceCollection>? _configure;

    private LibraryFixture(string root, LibraryOutputOptions output, IAudioCaptureFactory? realCapture, Action<IServiceCollection>? configure)
    {
        _realCapture = realCapture;
        _configure = configure;
        Root = root;
        LibraryRoot = Path.Combine(root, "Library");
        AppData = new AppDataPaths(Path.Combine(root, "AppData"));
        Output = output;
        _provider = Build();
    }

    public string Root { get; }

    public string LibraryRoot { get; }

    public AppDataPaths AppData { get; }

    public LibraryOutputOptions Output { get; }

    /// <summary>2026-09-30 10:15:30 +09:00 (Japan) unless advanced by a test.</summary>
    public FakeTimeProvider Time { get; } = CreateTime();

    public IServiceProvider Services => _provider;

    public LibraryService Library => _provider.GetRequiredService<LibraryService>();

    public ILibraryStore Store => _provider.GetRequiredService<ILibraryStore>();

    public ILibraryIndex Index => _provider.GetRequiredService<ILibraryIndex>();

    public KakitomeDatabase Database => _provider.GetRequiredService<KakitomeDatabase>();

    public RecordingService Recording => _provider.GetRequiredService<RecordingService>();

    public RecordingRecovery RecordingRecovery => _provider.GetRequiredService<RecordingRecovery>();

    public FakeCaptureFactory Capture { get; } = new();

    public FakeKeepAwake KeepAwake { get; } = new();

    public FakePowerEvents Power { get; } = new();

    public FakeDiskSpace Disk { get; } = new();

    public FakeRecycleBin RecycleBin { get; } = new();

    public FakeSystemResources SystemResources { get; } = new();

    public FakeNetworkCost Network { get; } = new();


    public JobScheduler Scheduler => _provider.GetRequiredService<JobScheduler>();

    public IJobStore Jobs => _provider.GetRequiredService<IJobStore>();

    public ProcessingPipeline Pipeline => _provider.GetRequiredService<ProcessingPipeline>();

    public Kakitome.Application.Search.SearchService Search => _provider.GetRequiredService<Kakitome.Application.Search.SearchService>();

    public Kakitome.Application.Search.ISearchIndex SearchIndex => _provider.GetRequiredService<Kakitome.Application.Search.ISearchIndex>();

    public Kakitome.Application.Settings.ISettingsStore Settings => _provider.GetRequiredService<Kakitome.Application.Settings.ISettingsStore>();

    /// <summary>Advances fake time in scheduler-sized steps, letting background loops observe each step.</summary>
    public async Task AdvanceAsync(TimeSpan total, TimeSpan? step = null)
    {
        var s = step ?? RecordingLimits.TickInterval;
        for (var t = TimeSpan.Zero; t < total; t += s)
        {
            Time.Advance(s);
            await Task.Delay(2);
        }
    }

    /// <param name="realCapture">Use this capture factory (e.g. real WASAPI) instead of the scriptable fake.</param>
    /// <param name="configure">Extra/overriding service registrations for a test.</param>
    /// <param name="initializeDatabase">False leaves the database to be initialized lazily (as at app startup).</param>
    public static async Task<LibraryFixture> CreateAsync(
        LibraryOutputOptions? output = null,
        IAudioCaptureFactory? realCapture = null,
        Action<IServiceCollection>? configure = null,
        bool initializeDatabase = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "KakitomeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fixture = new LibraryFixture(root, output ?? new LibraryOutputOptions(), realCapture, configure);
        if (initializeDatabase)
        {
            await fixture.Database.InitializeAsync();
        }

        return fixture;
    }

    public string PathOf(string relativeFolder, string? fileName = null)
    {
        var folder = Path.Combine(LibraryRoot, relativeFolder.Replace('/', Path.DirectorySeparatorChar));
        return fileName is null ? folder : Path.Combine(folder, fileName);
    }

    /// <summary>Simulates an app restart: new DI container over the same Library and database files.</summary>
    /// <param name="whileStopped">Runs between shutdown and start (e.g. start-up work before the database opens).</param>
    public async Task RestartAsync(Action? whileStopped = null)
    {
        await _provider.DisposeAsync();
        ClearOwnPool();
        whileStopped?.Invoke();
        _provider = Build();
    }

    /// <summary>Releases pooled connections to this fixture's database only (tests run in parallel).</summary>
    public void ClearOwnPool()
    {
        using var connection = new SqliteConnection(new Kakitome.Storage.Persistence.DatabaseLocation(AppData.DatabaseFile).ConnectionString);
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>SHA-256 of every file under the Library (to prove nothing canonical changed).</summary>
    public Dictionary<string, string> SnapshotLibrary() =>
        Directory.EnumerateFiles(LibraryRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(LibraryRoot, p),
                p => ContentHash.Sha256Hex(File.ReadAllBytes(p)),
                StringComparer.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        ClearOwnPool();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp directory.
        }
    }

    private static FakeTimeProvider CreateTime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 1, 15, 30, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST"));
        return time;
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKakitomeApplication();
        services.AddKakitomeStorage(AppData);
        services.AddSingleton<Kakitome.Application.Maintenance.IAppLocations>(new AppLocations(AppData, Path.Combine(Root, "Backups")));
        services.AddSingleton<TimeProvider>(_realCapture is null ? Time : TimeProvider.System);
        services.AddSingleton<IAudioCaptureFactory>(_realCapture ?? Capture);
        services.AddSingleton<IKeepAwake>(KeepAwake);
        services.AddSingleton<IPowerEvents>(Power);
        services.AddSingleton<IDiskSpaceProbe>(Disk);
        services.AddSingleton<IRecycleBin>(RecycleBin);
        services.AddSingleton<ISystemResourceProbe>(SystemResources);
        services.AddSingleton<Kakitome.Application.Asr.IAccelerationProbe>(new NoGpu());
        services.AddSingleton<Kakitome.Application.Models.INetworkCostProbe>(Network);
        services.AddSingleton<Kakitome.Application.Updates.IReleaseFeed>(new NoReleases()); // tests never go online
        services.Configure<LibraryLocationOptions>(o => o.RootPath = LibraryRoot);
        services.Configure<LibraryOutputOptions>(o =>
        {
            o.WriteMarkdown = Output.WriteMarkdown;
            o.WriteText = Output.WriteText;
        });
        _configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}

/// <summary>A release feed with nothing published (no network in tests).</summary>
public sealed class NoReleases : Kakitome.Application.Updates.IReleaseFeed
{
    public Task<Kakitome.Application.Updates.ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Kakitome.Application.Updates.ReleaseInfo?>(null);
}
