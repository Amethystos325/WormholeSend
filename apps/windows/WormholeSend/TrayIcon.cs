using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace WormholeSend;

/// The tray icon. Its menu (TrayMenu) is built by the owner each time it
/// opens, so the check marks are current. While a send waits in the
/// receiver's queue the icon carries an orange dot.
sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon icon;
    readonly System.Windows.Threading.Dispatcher ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    bool waiting;

    public TrayIcon(Func<IReadOnlyList<MenuEntry>> buildMenu, Action? open)
    {
        icon = new Forms.NotifyIcon
        {
            Text = "Wormhole Send",
            Icon = Draw(badge: false),
            Visible = true,
        };
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right) TrayMenu.Show(buildMenu());
        };
        if (open is not null) icon.MouseDoubleClick += (_, _) => open();
        // Light or dark taskbar: redraw in its text color.
        SystemEvents.UserPreferenceChanged += Redraw;
    }

    /// Raised on a thread of SystemEvents; the icon changes on the UI thread.
    void Redraw(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        ui.BeginInvoke(() =>
        {
            var old = icon.Icon;
            icon.Icon = Draw(waiting);
            old?.Dispose();
        });
    }

    /// The waiting mark of a queued send.
    public bool Waiting
    {
        set
        {
            if (waiting == value) return;
            waiting = value;
            var old = icon.Icon;
            icon.Icon = Draw(value);
            old?.Dispose();
        }
    }

    public void Notify(string title, string text) =>
        icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);

    /// The black hole (TrayGlyph) in the taskbar's text color, with the
    /// orange of the island's warnings for the waiting mark.
    static Drawing.Icon Draw(bool badge) =>
        TrayGlyph.Icon(
            TaskbarIsLight() ? Drawing.Color.Black : Drawing.Color.White,
            badge ? Drawing.Color.FromArgb(255, 158, 10) : null);

    static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= Redraw;
        icon.Visible = false;
        icon.Dispose();
    }
}
