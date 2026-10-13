using System.Windows;
using Forms = System.Windows.Forms;

namespace WormholeSend;

/// A monitor, in physical pixels (the process is per-monitor DPI aware).
sealed record ScreenInfo(string DeviceName, Int32Rect Bounds, bool Primary, IntPtr Monitor)
{
    /// The monitor's current scale factor (1.5 at 150%). Read live: the user
    /// can change it while the app runs.
    public double Scale =>
        Native.GetDpiForMonitor(Monitor, Native.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1;

    public static IReadOnlyList<ScreenInfo> All() =>
        Forms.Screen.AllScreens
            .Select(screen =>
            {
                var b = screen.Bounds;
                var center = new Native.POINT { X = b.X + b.Width / 2, Y = b.Y + b.Height / 2 };
                return new ScreenInfo(
                    screen.DeviceName,
                    new Int32Rect(b.X, b.Y, b.Width, b.Height),
                    screen.Primary,
                    Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTONEAREST));
            })
            .ToList();

    /// Whether a full-screen app (a game, a video, a slideshow) covers this
    /// monitor; the island stays out of its way then.
    public bool HasFullScreenApp()
    {
        var busy = false;
        if (Native.SHQueryUserNotificationState(out var state) == 0)
        {
            if (state is Native.QUNS_RUNNING_D3D_FULL_SCREEN or Native.QUNS_PRESENTATION_MODE) return true;
            busy = state == Native.QUNS_BUSY;
        }

        var window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        // The desktop and the taskbar cover the monitor too.
        if (Native.ClassName(window) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;
        if (Native.MonitorFromWindow(window, Native.MONITOR_DEFAULTTONEAREST) != Monitor) return false;
        if (!Native.GetWindowRect(window, out var rect)) return false;
        var covers = rect.Left <= Bounds.X && rect.Top <= Bounds.Y
            && rect.Right >= Bounds.X + Bounds.Width && rect.Bottom >= Bounds.Y + Bounds.Height;
        // The shell's own verdict (it says busy for full-screen windows,
        // borderless maximized ones included), or a window without a title
        // bar over the whole screen. A maximized window keeps its title bar,
        // and covers the screen when the taskbar hides itself.
        var caption = (Native.GetWindowLongPtr(window, Native.GWL_STYLE) & Native.WS_CAPTION) == Native.WS_CAPTION;
        return covers && (busy || !caption);
    }
}
