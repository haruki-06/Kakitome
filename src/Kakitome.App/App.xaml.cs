using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Kakitome.App.Services;
using Kakitome.App.ViewModels;
using Kakitome.Application;
using Kakitome.Application.Asr;
using Kakitome.Application.Jobs;
using Kakitome.Application.Library;
using Kakitome.Application.Models;
using Kakitome.Application.Recording;
using Kakitome.Application.Settings;
using Kakitome.Infrastructure;
using Kakitome.Presentation.Services;
using Kakitome.Presentation.ViewModels;
using Kakitome.Storage;
using Kakitome.Storage.Library;

namespace Kakitome.App;

#pragma warning disable CA1001 // The app owns _tray for the process lifetime; it is disposed in Shutdown.
public partial class App : Microsoft.UI.Xaml.Application
#pragma warning restore CA1001
{
    /// <summary>Test/automation override for the Library root (never needed by users).</summary>
    public const string LibraryRootVariable = "KAKITOME_LIBRARY_ROOT";

    /// <summary>Test/automation override for AppData (settings, database, logs, models).</summary>
    public const string AppDataRootVariable = "KAKITOME_APPDATA_ROOT";

    /// <summary>Test/automation override for the model folder (e.g. reuse installed models with a temporary AppData).</summary>
    public const string ModelsRootVariable = "KAKITOME_MODELS_ROOT";

    private readonly DispatcherQueue _dispatcher;
    private MainWindow? _window;
    private TrayController? _tray;
    private bool _exiting;

    public App()
    {
        UnhandledException += (_, e) => CrashLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();

        // A Factory Reset requested in Settings runs now, before anything opens the database, settings or logs.
        if (FactoryReset.RunIfRequested(AppDataOverride() ?? AppDataPaths.Default) is { Count: > 0 } leftovers)
        {
            CrashLog.Write(new IOException("Factory Reset could not remove: " + string.Join(", ", leftovers)));
        }

        var logs = Path.Combine((AppDataOverride() ?? AppDataPaths.Default).Root, "Logs");
        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                // Log files for issue reports and field tests (ADR-041) instead of the Windows event log.
                logging.ClearProviders();
                logging.AddDebug();
                logging.AddProvider(new Kakitome.Storage.Logging.FileLoggerProvider(logs, string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Kakitome {Version} started ({System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})")));
            })
            .ConfigureServices(services =>
            {
                services.AddKakitomeApplication();
                services.AddKakitomeStorage(AppDataOverride());
                if (Environment.GetEnvironmentVariable(LibraryRootVariable) is { Length: > 0 } libraryRoot)
                {
                    services.Configure<LibraryLocationOptions>(o => o.RootPath = Path.GetFullPath(libraryRoot));

                    // Automation runs keep backups next to their temporary Library, never in the user's Documents.
                    var backups = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(libraryRoot))!, "Backups");
                    services.AddSingleton<Kakitome.Application.Maintenance.IAppLocations>(
                        sp => new AppLocations(sp.GetRequiredService<AppDataPaths>(), backups));
                }

                services.AddKakitomeInfrastructure();
                AddUi(services);
            })
            .Build();

        AppInstance.GetCurrent().Activated += OnActivated;
        InitializeNotifications();
    }

    /// <summary>Composition root for the running app.</summary>
    public IHost Host { get; }

    public IServiceProvider Services => Host.Services;

    public static new App Current => (App)Microsoft.UI.Xaml.Application.Current;

    public static string Version =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public void ApplyTheme(string theme) => _window?.ApplyTheme(theme);

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        ApplyLanguageOverride(Services.GetRequiredService<ISettingsStore>().Current.General.UiLanguage);

        _window = Services.GetRequiredService<MainWindow>();
        _window.AppWindow.Closing += OnWindowClosing;
        _window.Closed += (_, _) => Shutdown();
        _window.Activate();
        Services.GetRequiredService<AppNotificationSink>().WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);

        _tray = new TrayController(
            Services.GetRequiredService<RecordingService>(),
            Services.GetRequiredService<RecordingViewModel>(),
            Services.GetRequiredService<ISettingsStore>(),
            ShowWindow,
            ExitAsync);

        // Background services (database open/recovery, Library sync, job scheduler) start after the window is visible.
        await Host.StartAsync().ConfigureAwait(true);
        Kakitome.Application.Diagnostics.LogRedactor.AddPath(Services.GetRequiredService<LibraryService>().LibraryRoot, "%LIBRARY%");
        _ = Services.GetRequiredService<NotificationPolicy>();
        _ = Services.GetRequiredService<UpdateNotifier>();
        if (AppDataOverride() is null) // automation runs never go online
        {
            var stopping = Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().ApplicationStopping;
            _ = Services.GetRequiredService<Kakitome.Application.Updates.UpdateChecker>().RunAsync(stopping);
        }

        _ = Services.GetRequiredService<Kakitome.Application.Asr.AsrModelReadyRetry>().RetryAsync(); // also listens for model installs
        try
        {
            // Missing recommended models / yt-dlp are downloaded as visible queue jobs (ADR-034). Not in automation runs
            // (temporary app data): UI tests must not fetch gigabytes on every run.
            if (AppDataOverride() is null)
            {
                await Services.GetRequiredService<Kakitome.Application.Models.AutoModelInstaller>().EnsureAsync().ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Optional convenience: the app works without it (Settings > Models still installs).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            CrashLog.Write(ex);
        }

        _ = Services.GetRequiredService<LiveTranscriptViewModel>(); // live preview follows recordings started by hotkey/tray too

        if (Program.LaunchActivation is { Kind: ExtendedActivationKind.AppNotification, Data: AppNotificationActivatedEventArgs notice })
        {
            OpenFromNotice(notice.Argument);
        }
    }

    private void InitializeNotifications()
    {
        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += (_, e) => _dispatcher.TryEnqueue(() => OpenFromNotice(e.Argument));
        try
        {
            manager.Register();
            Services.GetRequiredService<AppNotificationSink>().IsRegistered = true;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // E.g. an unpackaged dev build without the Windows App Runtime main/singleton packages. The app still works;
            // results stay visible in the Library and the processing queue.
            LogNotificationsUnavailable(Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<App>>(), ex.Message);
        }
    }

    /// <summary>A second launch or a notification click was redirected here (single instance).</summary>
    private void OnActivated(object? sender, AppActivationArguments e) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (e is { Kind: ExtendedActivationKind.AppNotification, Data: AppNotificationActivatedEventArgs notice })
            {
                OpenFromNotice(notice.Argument);
            }
            else
            {
                ShowWindow();
            }
        });

    /// <summary>Notifications only ever open a Kakitome screen (docs/01).</summary>
    private void OpenFromNotice(string? arguments)
    {
        ShowWindow();
        var (page, parameter) = NoticeArguments.Parse(arguments);
        Services.GetRequiredService<INavigationService>().Navigate(page, parameter);
    }

    private void AddUi(IServiceCollection services)
    {
        services.AddSingleton<IUiDispatcher>(new WinUiDispatcher(_dispatcher));
        services.AddSingleton<ILocalizer, ResourceLocalizer>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<IShellService>(sp => new ShellService(
            () => _window,
            () => [sp.GetRequiredService<LibraryService>().LibraryRoot, sp.GetRequiredService<IModelStore>().RootPath,
                   sp.GetRequiredService<Kakitome.Application.Maintenance.IAppLocations>().BackupsDirectory,
                   sp.GetRequiredService<Kakitome.Application.Diagnostics.DiagnosticsService>().LogsDirectory],
            Restart,
            () => _ = ExitAsync()));
        services.AddSingleton(sp => new MaintenanceViewModel(
            sp.GetRequiredService<Kakitome.Application.Maintenance.MaintenanceService>(),
            sp.GetRequiredService<Kakitome.Application.Maintenance.IAppLocations>(),
            sp.GetRequiredService<Kakitome.Application.Backup.BackupService>(),
            sp.GetRequiredService<JobScheduler>(),
            sp.GetRequiredService<IShellService>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<Kakitome.Application.Diagnostics.DiagnosticsService>(),
            Version));
        services.AddSingleton<ImportCoordinator>();
        services.AddSingleton<AppNotificationSink>();
        services.AddSingleton<INotificationSink>(sp => sp.GetRequiredService<AppNotificationSink>());
        services.AddSingleton<NotificationPolicy>();

        services.AddSingleton<RecordingViewModel>();
        services.AddSingleton<LiveTranscriptViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<QueueViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<ProjectsViewModel>();
        services.AddSingleton<GlossaryViewModel>();
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IModelStore>(),
            sp.GetRequiredService<JobScheduler>(),
            sp.GetRequiredService<LibraryService>(),
            sp.GetRequiredService<IAccelerationProbe>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<IShellService>(),
            sp.GetRequiredService<Kakitome.Application.Updates.UpdateChecker>(),
            Version));
        services.AddSingleton<UpdateNotifier>();
        services.AddTransient<RecordingDetailViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private static void ApplyLanguageOverride(string? language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return; // follow the Windows display language
        }

        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        }
#pragma warning disable CA1031 // A failed override falls back to the Windows display language.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            CrashLog.Write(ex);
        }
    }

    private static AppDataPaths? AppDataOverride()
    {
        var models = Environment.GetEnvironmentVariable(ModelsRootVariable) is { Length: > 0 } m ? m : null;
        return Environment.GetEnvironmentVariable(AppDataRootVariable) is { Length: > 0 } root
            ? new AppDataPaths(Path.GetFullPath(root), models)
            : models is null ? null : new AppDataPaths(AppDataPaths.Default.Root, models);
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.AppWindow.Show();
        if (_window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        _window.Activate();
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        var recording = Services.GetRequiredService<RecordingService>().Current is not null;
        var keep = Services.GetRequiredService<ISettingsStore>().Current.Recording.KeepRecordingWhenWindowClosed;
        if (!_exiting && recording && keep)
        {
            // Keep recording in the notification area instead of exiting.
            args.Cancel = true;
            sender.Hide();
        }
        else if (!_exiting && recording)
        {
            args.Cancel = true;
            _ = ExitAsync();
        }
    }

    /// <summary>Stops background work cleanly and starts a fresh Kakitome process (Factory Reset completes at start).</summary>
    private void Restart()
    {
        _tray?.Dispose();
        _tray = null;
        Host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

        // Release the single-instance key first so the new process becomes the main instance.
        AppInstance.GetCurrent().UnregisterKey();
        var failure = AppInstance.Restart(string.Empty);
        LogRestartFailed(Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<App>>(), failure.ToString());
        Exit();
    }

    /// <summary>Exit from the tray or window: a running recording is stopped and saved first.</summary>
    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        var recording = Services.GetRequiredService<RecordingService>();
        if (recording.Current is not null)
        {
            await recording.StopAsync().ConfigureAwait(true);
        }

        _window?.Close();
    }

    private void Shutdown()
    {
        _tray?.Dispose();
        _tray = null;
        Host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Exit();
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Windows notifications are unavailable: {Reason}")]
    private static partial void LogNotificationsUnavailable(Microsoft.Extensions.Logging.ILogger logger, string reason);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Restart failed: {Reason}")]
    private static partial void LogRestartFailed(Microsoft.Extensions.Logging.ILogger logger, string reason);
}
