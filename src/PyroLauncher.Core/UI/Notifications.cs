using PyroLauncher.Core;

namespace PyroLauncher.UI;

public enum NotifyKind { Instance, Lobby, Other }

/// <summary>
/// OS notifications (Settings → Notifications). Only sent while the launcher window is not focused: when it is,
/// the in-app toast already shows the same thing.
/// </summary>
public static class Notifications
{
    public static void Notify(string title, string body, NotifyKind kind)
    {
        var s = SettingsService.Current.Notifications;
        if (!s.System) return;
        var wanted = kind switch
        {
            NotifyKind.Instance => s.Installs,
            NotifyKind.Lobby => s.Lobby,
            _ => s.Other,
        };
        if (!wanted || Ui.Platform == null) return;
        Ui.Run(() =>
        {
            try
            {
                if (!Ui.Platform.IsMainWindowActive) Ui.Platform.ShowSystemNotification(title, body);
            }
            catch (Exception ex) { Log.Warn("System notification failed: " + ex.Message); }
        });
    }

    /// <summary>Sends a sample notification right away, even while the window is focused (Settings → Test).</summary>
    public static void Test()
    {
        try { Ui.Platform.ShowSystemNotification("PyroLauncher", L.T("Notifications are working.")); }
        catch (Exception ex) { Dialogs.Toast(L.F("Could not show a notification: {0}", ex.Message), true); }
    }
}
