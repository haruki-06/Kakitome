using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Windows.AppNotifications;
using Kakitome.Presentation.Services;

namespace Kakitome.App.Services;

/// <summary>Windows notifications through <see cref="AppNotificationManager"/> (docs/07).</summary>
internal sealed partial class AppNotificationSink : INotificationSink
{
    /// <summary>Main window handle; set once the window exists (read from the coalescing timer thread).</summary>
    public nint WindowHandle { get; set; }

    /// <summary>False when registration failed (e.g. notifications disabled by policy); nothing is shown then.</summary>
    public bool IsRegistered { get; set; }

    public bool IsAppInForeground => WindowHandle != 0 && GetForegroundWindow() == WindowHandle;

    public void Show(AppNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (!IsRegistered)
        {
            return;
        }

        var xml =
            $"<toast launch=\"{SecurityElement.Escape(notice.Arguments)}\">" +
            "<visual><binding template=\"ToastGeneric\">" +
            $"<text>{SecurityElement.Escape(notice.Title)}</text>" +
            $"<text>{SecurityElement.Escape(notice.Body)}</text>" +
            "</binding></visual></toast>";
        try
        {
            AppNotificationManager.Default.Show(new AppNotification(xml));
        }
        catch (COMException ex)
        {
            CrashLog.Write(ex); // a notification is never worth crashing over
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
}
