using System.Windows.Threading;

namespace WormholeSend;

/// Tells when a drag that left the island ends somewhere else: no drag
/// events reach this app then, so it polls the mouse buttons, like the
/// macOS app's DragMonitor.
sealed class DragWatch
{
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    Action? onEnd;

    public DragWatch() => timer.Tick += (_, _) => Tick();

    public void Start(Action onEnd)
    {
        this.onEnd = onEnd;
        timer.Start();
    }

    public void Stop()
    {
        timer.Stop();
        onEnd = null;
    }

    void Tick()
    {
        // Physical buttons: either one may be the primary when swapped.
        if (Native.IsPressed(Native.VK_LBUTTON) || Native.IsPressed(Native.VK_RBUTTON) || Native.IsPressed(Native.VK_MBUTTON))
            return;
        var action = onEnd;
        Stop();
        action?.Invoke();
    }
}
