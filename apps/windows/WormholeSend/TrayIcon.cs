using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace WormholeSend;

/// The tray icon. The menu is built by the owner each time it opens. While
/// a send waits in the receiver's queue the icon carries an orange dot
/// (design doc 2.1).
sealed class TrayIcon : IDisposable
{
    const string Glyph = "";

    readonly Forms.NotifyIcon icon;
    readonly Forms.ContextMenuStrip menu = new();
    bool waiting;

    public TrayIcon(Action<Forms.ToolStripItemCollection> buildMenu, Action? open)
    {
        buildMenu(menu.Items);
        // Rebuilt on every open so the check marks are current.
        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();
            buildMenu(menu.Items);
            e.Cancel = false;
        };
        icon = new Forms.NotifyIcon
        {
            Text = "Wormhole Send",
            ContextMenuStrip = menu,
            Icon = Draw(badge: false),
            Visible = true,
        };
        if (open is not null) icon.MouseDoubleClick += (_, _) => open();
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

    /// The icon is drawn from the icon font in the taskbar's text color, so
    /// the app needs no image files.
    static Drawing.Icon Draw(bool badge)
    {
        var primary = ScreenInfo.All().FirstOrDefault(s => s.Primary);
        var size = (int)Math.Round(16 * (primary?.Scale ?? 1));
        using var bitmap = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var color = TaskbarIsLight() ? Drawing.Color.Black : Drawing.Color.White;
            using var font = new Drawing.Font(IconFontName(), size * 0.8f, Drawing.GraphicsUnit.Pixel);
            using var brush = new Drawing.SolidBrush(color);
            using var format = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center,
            };
            g.DrawString(Glyph, font, brush, new Drawing.RectangleF(0, 0, size, size), format);
            if (badge)
            {
                var dot = size * 0.42f;
                using var orange = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 158, 10));
                g.FillEllipse(orange, size - dot, size - dot, dot, dot);
            }
        }
        var handle = bitmap.GetHicon();
        try
        {
            return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    static string IconFontName()
    {
        using var fonts = new Drawing.Text.InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }

    static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
    }
}

static class Menu
{
    public static Forms.ToolStripMenuItem Item(string text, Action action, bool enabled = true, bool check = false) =>
        new(text, null, (_, _) => action()) { Enabled = enabled, Checked = check };

    public static Forms.ToolStripMenuItem Title(string text) => new(text) { Enabled = false };

    public static Forms.ToolStripSeparator Separator() => new();
}
