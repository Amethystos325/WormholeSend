using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WormholeSend;

/// A transparent, topmost window at the top center of one screen that never
/// takes focus and stays out of the taskbar and Alt+Tab. It has a fixed size;
/// everything but the sensor and the island is fully transparent, which
/// lets clicks and drags through to the windows below.
sealed class IslandWindow : Window
{
    readonly IDragTarget target;
    IntPtr hwnd;
    /// Counts drag events, to tell a drag leaving the window from one moving
    /// between elements inside it (which raises DragLeave, then DragEnter).
    int dragEvents;

    public IslandWindow(IslandModel model, ScreenInfo screen, IDragTarget target)
    {
        this.target = target;
        Screen = screen;
        View = new IslandView(model);
        Title = "Wormhole Send";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowDrop = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Spec.PanelSize.Width;
        Height = Spec.PanelSize.Height;
        Content = View;

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE,
                style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST);
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            Place();
        };
        // Moving onto a monitor with another scale resizes the window; put it
        // back at the top center once WPF has taken the new DPI.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place, DispatcherPriority.Background);
        Closed += (_, _) => View.Detach();
    }

    public ScreenInfo Screen { get; }
    public IslandView View { get; }

    /// Top center of the screen, sized for the screen's scale.
    public void Place()
    {
        if (hwnd == IntPtr.Zero) return;
        var scale = Screen.Scale;
        var w = (int)Math.Round(Spec.PanelSize.Width * scale);
        var h = (int)Math.Round(Spec.PanelSize.Height * scale);
        var b = Screen.Bounds;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, b.X + (b.Width - w) / 2, b.Y, w, h, Native.SWP_NOACTIVATE);
    }

    /// Other topmost windows can end up above this one; take the top back.
    public void KeepOnTop()
    {
        if (hwnd != IntPtr.Zero)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WM_MOUSEACTIVATE)
        {
            handled = true;
            return Native.MA_NOACTIVATE;
        }
        return IntPtr.Zero;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        dragEvents++;
        target.DragOver(this, e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        dragEvents++;
        target.DragOver(this, e);
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        var stamp = ++dragEvents;
        Dispatcher.BeginInvoke(() =>
        {
            if (dragEvents == stamp) target.DragLeft(this);
        }, DispatcherPriority.Input);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        dragEvents++;
        target.Drop(this, e);
    }
}

interface IDragTarget
{
    void DragOver(IslandWindow window, DragEventArgs e);
    void DragLeft(IslandWindow window);
    void Drop(IslandWindow window, DragEventArgs e);
}
