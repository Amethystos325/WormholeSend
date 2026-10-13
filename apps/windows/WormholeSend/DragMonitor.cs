using System.Windows.Threading;

namespace WormholeSend;

/// Notices drags anywhere on screen, before they reach the island, as the
/// macOS app's DragMonitor does.
///
/// Windows has no global drag events without hooks, but while an OLE drag
/// (DoDragDrop) runs, ole32 creates a drag window, of class CLIPBRDWNDCLASS,
/// that has the mouse captured on the thread that started the drag. While a
/// mouse button is held this looks for such a window; the drag ends when the
/// capture goes, on a drop or on Esc. What a drag carries is only known once
/// it reaches a window of this app, so text and links start one too. Drags
/// that don't go through DoDragDrop are only noticed by the sensor.
sealed class DragMonitor
{
    const string DragWindowClass = "CLIPBRDWNDCLASS";
    static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(100);
    static readonly TimeSpan Pressed = TimeSpan.FromMilliseconds(30);

    readonly DispatcherTimer timer = new() { Interval = Idle };
    uint thread;
    IntPtr capture;

    public DragMonitor() => timer.Tick += (_, _) => Tick();

    public event Action? Started;
    /// Raised on every poll while the drag goes on.
    public event Action? Moved;
    public event Action? Ended;

    public bool IsDragging => capture != IntPtr.Zero;

    public void Start() => timer.Start();

    void Tick()
    {
        if (IsDragging)
        {
            if (Native.CaptureOf(thread) == capture)
            {
                Moved?.Invoke();
                return;
            }
            capture = IntPtr.Zero;
            Ended?.Invoke();
        }
        // Physical buttons: either one may be the primary when swapped, and
        // Explorer drags with the right one too.
        var pressed = Native.IsPressed(Native.VK_LBUTTON) || Native.IsPressed(Native.VK_RBUTTON);
        timer.Interval = pressed ? Pressed : Idle;
        if (!pressed || FindDrag() is not { } drag) return;
        (thread, capture) = drag;
        Started?.Invoke();
    }

    /// The drag window of a drag running in another app, whichever thread
    /// runs it: looking only at the foreground window's thread missed drags
    /// from the desktop. The window is message-only (from Explorer at
    /// least), and there are only a few windows of this class at any time.
    static (uint Thread, IntPtr Window)? FindDrag()
    {
        foreach (var parent in new[] { IntPtr.Zero, Native.HWND_MESSAGE })
        {
            var window = IntPtr.Zero;
            while ((window = Native.FindWindowEx(parent, window, DragWindowClass, null)) != IntPtr.Zero)
            {
                var thread = Native.GetWindowThreadProcessId(window, out var process);
                // Drags of this app's own (the received files) are handled by it.
                if (process != Environment.ProcessId && Native.CaptureOf(thread) == window)
                    return (thread, window);
            }
        }
        return null;
    }
}
