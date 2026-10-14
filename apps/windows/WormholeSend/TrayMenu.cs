using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace WormholeSend;

/// One line of the tray menu, with a Segoe glyph or a device's own icon. A
/// checked entry shows a check mark where its icon would be; a dimmed one
/// (an offline device) stays clickable.
sealed record MenuEntry(
    string Text,
    string? Glyph = null,
    Action? Action = null,
    bool Enabled = true,
    bool Checked = false,
    bool Dimmed = false,
    Func<IReadOnlyList<MenuEntry>>? Submenu = null,
    DeviceGlyph? Device = null)
{
    public static readonly MenuEntry Separator = new("—");
}

/// The tray menu in the look of Windows 11's own menus: acrylic, rounded,
/// an icon column, chevrons for submenus, a rounded highlight under the
/// pointer, a short slide-in. WinForms' ContextMenuStrip can't look like
/// that. Submenus are menus of their own beside their entry that never take
/// focus, so the root keeps the keyboard and closes the whole chain once
/// the user clicks elsewhere.
sealed class TrayMenu : FluentWindow
{
    const double RowHeight = 34;
    const string Check = "";
    const string Chevron = "";

    readonly TrayMenu? parent;
    readonly List<(MenuEntry Entry, Border Row)> rows = [];
    readonly DispatcherTimer hoverDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    TrayMenu? child;
    int hot = -1;
    IntPtr hwnd;
    bool closing;

    TrayMenu(IReadOnlyList<MenuEntry> entries, TrayMenu? parent)
    {
        this.parent = parent;
        // Submenus never take focus, and DWM draws the acrylic of inactive
        // windows as a flat grey: they get the tint alone, opaque.
        WindowBackdropType = parent is null ? WindowBackdropType.Acrylic : WindowBackdropType.None;
        ExtendsContentIntoTitleBar = true;
        WindowStyle = WindowStyle.None;
        Language = L.Xml;
        FontFamily = Fonts.Ui;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        // FluentWindow's style has a minimum size; a menu is as big as its rows.
        MinWidth = MinHeight = 0;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;
        ShowActivated = parent is null;
        Title = "Wormhole Send";

        var stack = new StackPanel { Margin = new Thickness(4) };
        foreach (var entry in entries)
        {
            if (entry == MenuEntry.Separator)
            {
                var line = new Border { Height = 1, Margin = new Thickness(-4, 4, -4, 4) };
                line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
                stack.Children.Add(line);
                continue;
            }
            var row = Row(entry, rows.Count);
            rows.Add((entry, row));
            stack.Children.Add(row);
        }
        var content = new Border
        {
            MinWidth = 220,
            Child = stack,
            Background = Tint(opaque: parent is not null),
            Opacity = 0,
            RenderTransform = new TranslateTransform(0, parent is null ? 10 : 0),
        };
        Content = content;

        hoverDelay.Tick += (_, _) =>
        {
            hoverDelay.Stop();
            OpenChild(keyboard: false);
        };
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            if (parent is not null)
            {
                // Submenus never take focus: the root keeps it, and with it
                // the "clicked elsewhere" signal.
                var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
                HwndSource.FromHwnd(hwnd).AddHook((IntPtr _, int message, IntPtr _, IntPtr _, ref bool handled) =>
                {
                    if (message != Native.WM_MOUSEACTIVATE) return IntPtr.Zero;
                    handled = true;
                    return Native.MA_NOACTIVATE;
                });
            }
        };
        ContentRendered += (_, _) =>
        {
            Ui.Animate(content, OpacityProperty, 1, 0.12);
            if (content.RenderTransform is TranslateTransform slide) Ui.Animate(slide, TranslateTransform.YProperty, 0, 0.2);
        };
        if (parent is null)
        {
            Activated += (_, _) => Log.Write("tray menu active");
            Deactivated += (_, _) => CloseAll();
            PreviewKeyDown += (_, e) => e.Handled = Deepest.HandleKey(e.Key);
        }
    }

    /// Opens the menu at the pointer (or `at`, in physical pixels), above
    /// the taskbar as Windows does.
    public static TrayMenu Show(IReadOnlyList<MenuEntry> entries, System.Drawing.Point? at = null)
    {
        Native.GetCursorPos(out var pointer);
        var point = at ?? new System.Drawing.Point(pointer.X, pointer.Y);
        var menu = new TrayMenu(entries, null);
        menu.Show();
        menu.PlaceAt(point.X, point.Y, above: true);
        menu.Activate();
        Native.SetForegroundWindow(menu.hwnd);
        return menu;
    }

    /// For `--menu`: the first submenu opened, as from the keyboard.
    public void OpenFirstSubmenu()
    {
        var index = rows.FindIndex(r => r.Entry.Submenu is not null);
        if (index < 0) return;
        SetHot(index);
        OpenChild(keyboard: true);
    }

    /// The menus' bounds on screen, this one and its open submenus.
    public System.Drawing.Rectangle Bounds
    {
        get
        {
            Native.GetWindowRect(hwnd, out var r);
            var rect = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            return child is null ? rect : System.Drawing.Rectangle.Union(rect, child.Bounds);
        }
    }

    TrayMenu Deepest => child?.Deepest ?? this;

    /// Windows 11's menus lay a bright tint over their acrylic; the bare
    /// system backdrop looks grey over a dark wallpaper. Nearly opaque, so
    /// only a hint of the blur comes through, as in the system's menus.
    static Brush Tint(bool opaque)
    {
        var dark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;
        var alpha = (byte)(opaque ? 0xFF : 0xF0);
        var brush = new SolidColorBrush(dark ? Color.FromArgb(alpha, 0x2C, 0x2C, 0x2C) : Color.FromArgb(alpha, 0xF9, 0xF9, 0xF9));
        brush.Freeze();
        return brush;
    }

    Border Row(MenuEntry entry, int index)
    {
        var grid = new Grid { Height = RowHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        var foreground = !entry.Enabled || entry.Dimmed ? "TextFillColorDisabledBrush" : "TextFillColorPrimaryBrush";
        if ((entry.Checked ? Check : entry.Glyph) is { } icon)
        {
            var iconText = new System.Windows.Controls.TextBlock
            {
                Text = icon,
                FontFamily = Glyphs.Font,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            iconText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, foreground);
            grid.Children.Add(iconText);
        }
        else if (entry.Device is { } device)
        {
            var deviceIcon = new DeviceIcon(device, 16);
            deviceIcon.SetBrushResource(foreground);
            grid.Children.Add(deviceIcon);
        }
        var text = new System.Windows.Controls.TextBlock
        {
            Text = entry.Text,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 320,
            Margin = new Thickness(0, 0, 12, 1),
        };
        text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, foreground);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (entry.Submenu is not null)
        {
            var chevron = new System.Windows.Controls.TextBlock
            {
                Text = Chevron,
                FontFamily = Glyphs.Font,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            chevron.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);
        }

        var row = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 1, 0, 1),
        };
        System.Windows.Automation.AutomationProperties.SetName(row, entry.Text);
        if (!entry.Enabled) return row;
        row.MouseEnter += (_, _) =>
        {
            SetHot(index);
            hoverDelay.Stop();
            if (child is not null && child.parentRow != index) CloseChild();
            if (entry.Submenu is not null && child is null) hoverDelay.Start();
        };
        row.MouseLeftButtonUp += (_, _) => Invoke(index, keyboard: false);
        return row;
    }

    int parentRow = -1;

    void SetHot(int index)
    {
        if (hot == index) return;
        if (hot >= 0) rows[hot].Row.Background = Brushes.Transparent;
        hot = index;
        if (hot >= 0) rows[hot].Row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
    }

    void Invoke(int index, bool keyboard)
    {
        var entry = rows[index].Entry;
        if (!entry.Enabled) return;
        if (entry.Submenu is not null)
        {
            SetHot(index);
            OpenChild(keyboard);
            return;
        }
        Root.CloseAll();
        // After the menu is gone, so a window the action opens comes forward.
        if (entry.Action is { } action) Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    TrayMenu Root => parent?.Root ?? this;

    void OpenChild(bool keyboard)
    {
        if (hot < 0 || rows[hot].Entry.Submenu is not { } build || child?.parentRow == hot) return;
        CloseChild();
        var entries = build();
        if (entries.Count == 0) return;
        child = new TrayMenu(entries, this) { parentRow = hot };
        child.Show();
        // Beside the entry, its first row level with the entry.
        var row = rows[hot].Row;
        var topLeft = row.PointToScreen(new Point(0, 0));
        var bottomRight = row.PointToScreen(new Point(row.ActualWidth, row.ActualHeight));
        child.PlaceBeside(topLeft, bottomRight);
        if (keyboard) child.SetHot(0);
    }

    void CloseChild()
    {
        child?.CloseAll();
        child = null;
    }

    /// Handles a key for the menu that has it (the deepest one open).
    bool HandleKey(Key key)
    {
        switch (key)
        {
            case System.Windows.Input.Key.Escape:
                if (parent is null) CloseAll();
                else
                {
                    parent.child = null;
                    CloseAll();
                }
                return true;
            case System.Windows.Input.Key.Left when parent is not null:
                parent.child = null;
                CloseAll();
                return true;
            case System.Windows.Input.Key.Down:
            case System.Windows.Input.Key.Up:
                var step = key == System.Windows.Input.Key.Down ? 1 : -1;
                var next = hot;
                for (var i = 0; i < rows.Count; i++)
                {
                    next = (next + step + rows.Count) % rows.Count;
                    if (rows[next].Entry.Enabled) break;
                }
                SetHot(next);
                return true;
            case System.Windows.Input.Key.Right when hot >= 0 && rows[hot].Entry.Submenu is not null:
            case System.Windows.Input.Key.Enter when hot >= 0:
            case System.Windows.Input.Key.Space when hot >= 0:
                Invoke(hot, keyboard: true);
                return true;
        }
        return false;
    }

    void CloseAll()
    {
        if (closing) return;
        closing = true;
        hoverDelay.Stop();
        child?.CloseAll();
        child = null;
        Close();
    }

    /// Puts the menu at a point in physical pixels, inside the work area of
    /// that point's monitor: above and to the right of it for the root,
    /// flipping when it doesn't fit.
    void PlaceAt(int x, int y, bool above)
    {
        Native.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var area = WorkArea(x, y);
        var left = x + w <= area.Right ? x : x - w;
        var top = above ? y - h : y;
        if (top < area.Top) top = y;
        if (top + h > area.Bottom) top = area.Bottom - h;
        left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - w));
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void PlaceBeside(Point entryTopLeft, Point entryBottomRight)
    {
        Native.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var area = WorkArea((int)entryBottomRight.X, (int)entryTopLeft.Y);
        // The child's own padding (4) plus the row margin (1), in pixels.
        var inset = (int)Math.Round(5 * VisualTreeHelper.GetDpi(this).DpiScaleY);
        var left = (int)entryBottomRight.X + 2;
        if (left + w > area.Right) left = (int)entryTopLeft.X - w - 2;
        var top = Math.Clamp((int)entryTopLeft.Y - inset, area.Top, Math.Max(area.Top, area.Bottom - h));
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// In physical pixels (the process is per-monitor DPI aware).
    static System.Drawing.Rectangle WorkArea(int x, int y) =>
        System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
}
