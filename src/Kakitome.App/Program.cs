using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace Kakitome.App;

/// <summary>
/// Custom entry point for single-instance activation (docs/09): a second launch (Start menu, notification click while
/// starting, shortcut) is redirected to the running Kakitome, which brings its window forward.
/// </summary>
public static partial class Program
{
    /// <summary>The activation that started this process (launch or notification click).</summary>
    internal static AppActivationArguments? LaunchActivation { get; private set; }

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToRunningInstance())
        {
            return 0;
        }

        Microsoft.UI.Xaml.Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    private static bool RedirectToRunningInstance()
    {
        try
        {
            return TryRedirect();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Activation services unavailable: start normally rather than not at all.
            CrashLog.Write(ex);
            return false;
        }
    }

    private static bool TryRedirect()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var key = AppInstance.FindOrRegisterForKey(InstanceKey());
        if (key.IsCurrent)
        {
            LaunchActivation = activation;
            return false;
        }

        // Let the running instance take the foreground, then hand it this activation.
        _ = AllowSetForegroundWindow(key.ProcessId);
        Task.Run(() => key.RedirectActivationToAsync(activation).AsTask()).Wait();
        return true;
    }

    /// <summary>One instance per AppData root, so isolated test/automation instances do not capture each other.</summary>
    private static string InstanceKey()
    {
        var root = Environment.GetEnvironmentVariable(App.AppDataRootVariable);
        if (string.IsNullOrEmpty(root))
        {
            return "Kakitome.Main";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()));
        return "Kakitome.Main." + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
