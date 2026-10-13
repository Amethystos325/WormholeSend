using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace WormholeSend;

/// What the settings, pairing and welcome windows use.
interface ISession
{
    Core? Core { get; }
    /// The result of `settings.get`.
    JsonNode? Settings { get; }
    IReadOnlyList<Device> Devices { get; }
    event Action? DevicesChanged;
    event Action? SettingsChanged;
    /// `pair.code` and `pair.result` of pairings this device started.
    event Action<CoreEvent>? PairingEvent;
    void ReloadDevices();
    void SettingsSaved(JsonNode? settings);
    void OpenPairing();
}

/// Owns one island window per screen, the tray icon, the core and the
/// windows. Routes drags from the island windows to the model and core
/// events to the model and the windows. The island shows on the screen
/// whose sensor a drag entered; incoming transfers and pairing requests
/// show on the primary screen.
sealed class AppController(Options options) : IDragTarget, ISession
{
    readonly IslandModel model = new();
    readonly DragWatch dragWatch = new();
    readonly DispatcherTimer guard = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer returnHome = new() { Interval = TimeSpan.FromMilliseconds(700) };
    /// Pairings other devices started; their codes show on the island.
    readonly HashSet<string> incomingPairs = [];
    List<IslandWindow> windows = [];
    IslandWindow? active;
    Phase lastPhase = new Phase.Idle();
    TrayIcon? tray;
    Core? core;
    JsonNode? settings;
    SettingsWindow? settingsWindow;
    PairWindow? pairWindow;

    public event Action? DevicesChanged;
    public event Action? SettingsChanged;
    public event Action<CoreEvent>? PairingEvent;

    public Core? Core => core;
    public JsonNode? Settings => settings;
    public IReadOnlyList<Device> Devices => model.Devices;

    public void Start()
    {
        model.PhaseChanged += PhaseChanged;
        model.PairAnswered += AnswerPair;
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

        if (options.Fake)
        {
            tray = new TrayIcon(BuildFakeMenu, null);
            Demo.Apply(options, model, this);
            if (options.SelfTest) _ = RunSelfTest();
            return;
        }
        model.Devices = [];
        tray = new TrayIcon(BuildMenu, OpenSettings);
        var home = options.Home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WormholeSend");
        // No identity yet: the first run, which starts with the guide.
        if (File.Exists(Path.Combine(home, "identity.pem")))
            _ = StartCore(alias: null);
        else
            ShowWelcome();
    }

    async Task RunSelfTest()
    {
        var code = await SelfTest.Run(model, Home);
        tray?.Dispose();
        Application.Current.Shutdown(code);
    }

    // MARK: - Core

    /// Starts the core off the UI thread. Returns an error message, or null.
    async Task<string?> StartCore(string? alias)
    {
        if (core is not null) return null;
        var config = new JsonObject();
        if (options.Home is { } home) config["home"] = home;
        if (options.Port is { } port) config["port"] = port;
        if (alias is not null) config["alias"] = alias;
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            var (started, startSettings) = await Task.Run(() =>
                Core.Start(config, e => dispatcher.BeginInvoke(() => OnCoreEvent(e))));
            core = started;
            model.Transfers = new CoreTransfers(started);
            Log.Write($"core started: {Core.Version}");
            SettingsSaved(startSettings);
            ReloadDevices();
            return null;
        }
        catch (Exception e) when (e is CoreException or DllNotFoundException or EntryPointNotFoundException)
        {
            var message = e is CoreException ? e.Message : "找不到核心库 wormhole.dll";
            Log.Write("core failed to start: " + message);
            if (alias is null) tray?.Notify("Wormhole Send 无法启动", message);
            return message;
        }
    }

    void OnCoreEvent(CoreEvent e)
    {
        model.Handle(e);
        switch (e)
        {
            case DeviceUpdated { Paired: true }:
            case TransferFinished { Sending: true, Status: "rejected" }:
                ReloadDevices();
                break;
            case PairIncoming incoming:
                incomingPairs.Add(incoming.PairId);
                break;
            case PairCode code when incomingPairs.Contains(code.PairId):
                model.ShowPairRequest(code.PairId, code.Alias, code.Code);
                break;
            case PairResult result when incomingPairs.Remove(result.PairId):
                model.PairFinished(result.PairId, result.Alias, result.Ok, result.Reason);
                if (result.Ok) ReloadDevices();
                break;
            case PairCode or PairResult:
                if (e is PairResult { Ok: true }) ReloadDevices();
                PairingEvent?.Invoke(e);
                break;
            case DeviceUnpaired unpaired:
                ReloadDevices();
                model.ShowNotice($"{unpaired.Alias} 已解除配对", success: false);
                break;
        }
    }

    public void ReloadDevices()
    {
        if (core is null) return;
        try
        {
            if (core.Command("devices.list") is JsonArray list)
                model.Devices = list.OfType<JsonNode>().Select(Device.FromCore).ToList();
            DevicesChanged?.Invoke();
        }
        catch (CoreException e)
        {
            Log.Write("devices.list: " + e.Message);
        }
    }

    public void SettingsSaved(JsonNode? saved)
    {
        if (saved is null) return;
        settings = saved;
        model.SaveDirLabel = SaveDirLabel(saved["saveDir"]?.GetValue<string>() ?? "");
        SettingsChanged?.Invoke();
    }

    /// "下载\Wormhole Send" for the default, else the path.
    static string SaveDirLabel(string dir)
    {
        var downloads = Native.KnownFolder(Native.FOLDERID_Downloads);
        return downloads is not null && dir.StartsWith(downloads, StringComparison.OrdinalIgnoreCase)
            ? "下载" + dir[downloads.Length..]
            : dir;
    }

    void AnswerPair(string pairId, bool accept)
    {
        try
        {
            core?.Command("pair.confirm", new JsonObject { ["pairId"] = pairId, ["accept"] = accept });
        }
        catch (CoreException e)
        {
            model.ShowNotice(e.Message, success: false);
        }
    }

    // MARK: - Windows and menu

    void ShowWelcome()
    {
        var welcome = new WelcomeWindow(this, alias => StartCore(alias));
        Application.Current.MainWindow = welcome;
        welcome.Closed += async (_, _) =>
        {
            if (core is null && await StartCore(null) is not null) return;
            if (!welcome.Finished) return;
            try
            {
                Autostart.Enabled = welcome.StartWithWindows;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Write("autostart: " + e.Message);
            }
            // The name was used for this run only; keep it if it was changed.
            if (welcome.Alias != System.Net.Dns.GetHostName())
            {
                try
                {
                    SettingsSaved(core?.Command("settings.set", new JsonObject { ["alias"] = welcome.Alias }));
                }
                catch (CoreException e)
                {
                    Log.Write("settings.set: " + e.Message);
                }
            }
            Activate(Home);
            model.ShowIntro();
        };
        welcome.Show();
        welcome.Activate();
    }

    public void OpenSettings()
    {
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        Application.Current.MainWindow = settingsWindow;
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    public void OpenPairing()
    {
        if (core is null)
        {
            tray?.Notify("Wormhole Send", "服务没有在运行，暂时不能配对");
            return;
        }
        if (pairWindow is null)
        {
            pairWindow = new PairWindow(this);
            pairWindow.Closed += (_, _) => pairWindow = null;
        }
        Application.Current.MainWindow = pairWindow;
        pairWindow.Show();
        pairWindow.Activate();
    }

    void OpenSaveDir()
    {
        if (settings?["saveDir"]?.GetValue<string>() is not { } dir) return;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false });
    }

    /// Design doc 7.2.
    void BuildMenu(Forms.ToolStripItemCollection items)
    {
        var defaults = new Forms.ToolStripMenuItem("默认设备");
        foreach (var device in model.Devices)
        {
            var item = Menu.Item(device.Name, () => SetDefault(device), check: device.IsDefault);
            if (!device.Online) item.ForeColor = Drawing.SystemColors.GrayText;
            defaults.DropDownItems.Add(item);
        }
        if (model.Devices.Count == 0) defaults.DropDownItems.Add(Menu.Title("还没有配对设备"));
        items.Add(defaults);
        items.Add(Menu.Item("配对新设备…", OpenPairing, enabled: core is not null));
        items.Add(Menu.Item("打开接收文件夹", OpenSaveDir, enabled: settings is not null));
        items.Add(Menu.Item("设置…", OpenSettings));
        items.Add(Menu.Separator());
        items.Add(Menu.Item("退出", Quit));
    }

    /// Switches for the simulated devices and transfers (`--fake`).
    void BuildFakeMenu(Forms.ToolStripItemCollection items)
    {
        items.Add(Menu.Title("Wormhole Send · 假数据"));
        items.Add(Menu.Separator());
        var devices = new Forms.ToolStripMenuItem("配对设备");
        foreach (var count in new[] { 0, 1, 3 })
            devices.DropDownItems.Add(Menu.Item($"{count} 台", () => model.Devices = Device.Samples.Take(count).ToList(), check: model.Devices.Count == count));
        items.Add(devices);
        items.Add(Menu.Item("默认设备离线",
            () => model.Devices = model.Devices.Select(d => d.IsDefault ? d with { Online = !d.Online } : d).ToList(),
            enabled: model.DefaultDevice is not null, check: model.DefaultDevice?.Online == false));
        var outcomes = new Forms.ToolStripMenuItem("发送结果");
        foreach (var outcome in Enum.GetValues<FakeTransfers.Outcome>())
            outcomes.DropDownItems.Add(Menu.Item(FakeTransfers.Title(outcome), () => model.Fake.NextOutcome = outcome, check: model.Fake.NextOutcome == outcome));
        items.Add(outcomes);
        items.Add(Menu.Separator());
        items.Add(Menu.Item("模拟接收文件", () =>
        {
            Activate(Home);
            model.Fake.Receive(model.Devices.FirstOrDefault(d => d.Id == "air") ?? Device.Samples[1]);
        }));
        items.Add(Menu.Item("模拟配对请求", () => model.ShowPairRequest("fake", "MacBook Air", "482019")));
        items.Add(Menu.Separator());
        items.Add(Menu.Item("退出", Quit));
    }

    void SetDefault(Device device)
    {
        try
        {
            core?.Command("device.setDefault", new JsonObject { ["deviceId"] = device.Id });
            ReloadDevices();
        }
        catch (CoreException e)
        {
            tray?.Notify("Wormhole Send", e.Message);
        }
    }

    public void Quit()
    {
        tray?.Dispose();
        tray = null;
        foreach (var window in Application.Current.Windows.OfType<Window>().ToList()) window.Close();
        core?.Dispose();
        core = null;
        Application.Current.Shutdown();
    }

    // MARK: - Screens

    IslandWindow Home => windows.FirstOrDefault(w => w.Screen.Primary) ?? windows[0];

    void BuildWindows()
    {
        var previous = active?.Screen.DeviceName;
        foreach (var window in windows) window.Close();
        windows = ScreenInfo.All().Select(screen => new IslandWindow(model, screen, this)).ToList();
        foreach (var window in windows) window.Show();
        // WPF makes the first window the main one, and WPF-UI paints the
        // main window's background when the theme changes.
        if (Application.Current.MainWindow is IslandWindow) Application.Current.MainWindow = null;
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
        if (tray is not null) tray.Waiting = phase is Phase.Queued;
        // What other devices start shows on the primary screen.
        if (phase is Phase.Receiving or Phase.PairRequest && lastPhase is Phase.Idle && windows.Count > 0) Activate(Home);
        lastPhase = phase;
        returnHome.Stop();
        if (phase is Phase.Idle) returnHome.Start();
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
