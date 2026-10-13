using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace WormholeSend;

/// The tray icon. Its menu holds switches for the fake data while the core
/// is not connected yet, like the macOS menu bar item. While a send waits in
/// the receiver's queue the icon carries an orange dot (design doc 2.1).
sealed class TrayIcon : IDisposable
{
    const string Glyph = "";

    readonly IslandModel model;
    readonly Action simulateReceive;
    readonly Action quit;
    readonly Forms.NotifyIcon icon;
    readonly Forms.ContextMenuStrip menu = new();
    bool waiting;

    public TrayIcon(IslandModel model, Action simulateReceive, Action quit)
    {
        this.model = model;
        this.simulateReceive = simulateReceive;
        this.quit = quit;
        Build();
        // Rebuilt on every open so the check marks are current.
        menu.Opening += (_, e) =>
        {
            Build();
            e.Cancel = false;
        };
        icon = new Forms.NotifyIcon
        {
            Text = "Wormhole Send",
            ContextMenuStrip = menu,
            Icon = Draw(badge: false),
            Visible = true,
        };
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IslandModel.Phase)) UpdateBadge();
        };
    }

    void UpdateBadge()
    {
        var queued = model.Phase is Phase.Queued;
        if (queued == waiting) return;
        waiting = queued;
        var old = icon.Icon;
        icon.Icon = Draw(queued);
        old?.Dispose();
    }

    void Build()
    {
        menu.Items.Clear();
        menu.Items.Add(new Forms.ToolStripMenuItem("Wormhole Send · 顶部岛验证（假数据）") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());

        var devices = new Forms.ToolStripMenuItem("配对设备");
        foreach (var count in new[] { 0, 1, 3 })
        {
            devices.DropDownItems.Add(new Forms.ToolStripMenuItem($"{count} 台", null, (_, _) =>
                model.Devices = Device.Samples.Take(count).ToList())
            { Checked = model.Devices.Count == count });
        }
        menu.Items.Add(devices);

        menu.Items.Add(new Forms.ToolStripMenuItem("默认设备离线", null, (_, _) =>
            model.Devices = model.Devices.Select(d => d.IsDefault ? d with { Online = !d.Online } : d).ToList())
        {
            Checked = model.DefaultDevice?.Online == false,
            Enabled = model.DefaultDevice is not null,
        });

        var outcomes = new Forms.ToolStripMenuItem("发送结果");
        foreach (var outcome in Enum.GetValues<IslandModel.SendOutcome>())
        {
            outcomes.DropDownItems.Add(new Forms.ToolStripMenuItem(IslandModel.Title(outcome), null, (_, _) =>
                model.NextOutcome = outcome)
            { Checked = model.NextOutcome == outcome });
        }
        menu.Items.Add(outcomes);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("模拟接收文件", null, (_, _) => simulateReceive())
        {
            Enabled = model.Phase is Phase.Idle,
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => quit()));
    }

    /// The icon is drawn from the icon font in the taskbar's text color, so
    /// the spike needs no image files.
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
