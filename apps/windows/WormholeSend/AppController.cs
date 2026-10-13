using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;

namespace WormholeSend;

/// Owns one island window per screen, the tray icon, and routes the drags
/// the windows receive to the model. The island shows on the screen whose
/// sensor the drag entered; incoming transfers show on the primary screen.
sealed class AppController(Options options) : IDragTarget
{
    readonly IslandModel model = new();
    readonly DragWatch dragWatch = new();
    readonly DispatcherTimer guard = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer returnHome = new() { Interval = TimeSpan.FromMilliseconds(700) };
    List<IslandWindow> windows = [];
    IslandWindow? active;
    TrayIcon? tray;

    public void Start()
    {
        model.PhaseChanged += PhaseChanged;
        BuildWindows();
        SystemEvents.DisplaySettingsChanged += (_, _) =>
            Application.Current.Dispatcher.BeginInvoke(BuildWindows);
        guard.Tick += (_, _) => Guard();
        guard.Start();
        returnHome.Tick += (_, _) =>
        {
            returnHome.Stop();
            if (model.Phase is Phase.Idle && windows.Count > 0) Activate(Home);
        };
        tray = new TrayIcon(model, SimulateReceive, Quit);
        Demo.Apply(options, model, this);
        if (options.SelfTest) _ = RunSelfTest();
    }

    async Task RunSelfTest()
    {
        var code = await SelfTest.Run(model, Home);
        tray?.Dispose();
        Application.Current.Shutdown(code);
    }

    IslandWindow Home => windows.FirstOrDefault(w => w.Screen.Primary) ?? windows[0];

    void BuildWindows()
    {
        var previous = active?.Screen.DeviceName;
        foreach (var window in windows) window.Close();
        windows = ScreenInfo.All().Select(screen => new IslandWindow(model, screen, this)).ToList();
        foreach (var window in windows) window.Show();
        Activate(windows.FirstOrDefault(w => w.Screen.DeviceName == previous) ?? Home);
        Log.Write("screens " + string.Join(", ", windows.Select(w =>
            $"{w.Screen.DeviceName} {w.Screen.Bounds.Width}x{w.Screen.Bounds.Height}@{w.Screen.Scale:0.##}{(w.Screen.Primary ? " primary" : "")}")));
    }

    void Activate(IslandWindow window)
    {
        active = window;
        foreach (var w in windows) w.View.IsActiveScreen = w == window;
    }

    /// For `--screen`: "external" (the first secondary screen) or an index.
    public void ActivateScreen(string? which)
    {
        var window = which switch
        {
            "external" => windows.FirstOrDefault(w => !w.Screen.Primary),
            _ when int.TryParse(which, out var index) && index >= 0 && index < windows.Count => windows[index],
            _ => null,
        };
        if (window is not null) Activate(window);
    }

    /// Full-screen apps hide the windows on their screen; the others take
    /// back the top of the z-order.
    void Guard()
    {
        foreach (var window in windows)
        {
            var hide = window.Screen.HasFullScreenApp();
            if (hide && window.IsVisible)
            {
                window.Hide();
                Log.Write($"hidden on {window.Screen.DeviceName}: full-screen app");
            }
            else if (!hide && !window.IsVisible)
            {
                window.Show();
                Log.Write($"shown on {window.Screen.DeviceName}");
            }
            if (!hide) window.KeepOnTop();
        }
    }

    void PhaseChanged(Phase phase)
    {
        Log.Write($"phase {phase.Describe()}");
        returnHome.Stop();
        // Back to the primary screen, where incoming transfers show.
        if (phase is Phase.Idle) returnHome.Start();
    }

    void SimulateReceive()
    {
        Activate(Home);
        model.SimulateReceive();
    }

    void Quit()
    {
        tray?.Dispose();
        Application.Current.Shutdown();
    }

    // MARK: - Drags from the windows

    public void DragOver(IslandWindow window, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (FileChip.DraggingOut || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        dragWatch.Stop();
        // A transfer in progress keeps its screen and ignores drags.
        if (model.IsBusy) return;
        if (window != active) Activate(window);
        // A new drag, possibly over a result still showing (as on macOS).
        if (!model.Phase.AcceptsDrag) model.DragStarted(files);
        model.DragMoved(e.GetPosition(window), Spec.PanelSize.Width);
        if (model.CanDrop) e.Effects = DragDropEffects.Copy;
    }

    public void DragLeft(IslandWindow window)
    {
        if (window != active) return;
        model.DragLeftPanel();
        // The drag goes on elsewhere; the hint stays until the button is up.
        if (model.Phase is Phase.Hint) dragWatch.Start(model.DragEnded);
    }

    public void Drop(IslandWindow window, DragEventArgs e)
    {
        e.Handled = true;
        dragWatch.Stop();
        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var taken = window == active && !FileChip.DraggingOut && model.Drop(files);
        e.Effects = taken ? DragDropEffects.Copy : DragDropEffects.None;
        model.DragEnded();
    }

    // MARK: - Checks

    /// Saves what the active window shows on screen, then quits.
    public void Snapshot(string path)
    {
        if (active is not null)
        {
            Native.GetWindowRect(new WindowInteropHelper(active).Handle, out var rect);
            using var bitmap = new Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
            using (var g = Drawing.Graphics.FromImage(bitmap))
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
            bitmap.Save(path, Drawing.Imaging.ImageFormat.Png);
            Log.Write($"snapshot {path}");
        }
        Quit();
    }
}
