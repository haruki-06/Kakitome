using Kakitome.Application.Settings;
using Kakitome.Application.Updates;

namespace Kakitome.Presentation.Services;

/// <summary>A Windows notification when a newer release is found while Kakitome is in the background (once per version).</summary>
public sealed class UpdateNotifier
{
    private string? _notified;

    public UpdateNotifier(UpdateChecker updates, INotificationSink sink, ISettingsStore settings, ILocalizer text)
    {
        ArgumentNullException.ThrowIfNull(updates);
        updates.AvailableChanged += (_, _) =>
        {
            if (updates.Available is not { } release || release.Version == _notified || release.Version == settings.Current.General.DismissedUpdate
                || !settings.Current.General.Notifications || sink.IsAppInForeground)
            {
                return;
            }

            _notified = release.Version;
            sink.Show(new AppNotice(text.GetString("Notify_Update"), text.Format("Update_Available", release.Version, updates.CurrentVersion),
                NoticeArguments.OpenPage(PageKey.Home)));
        };
    }
}
