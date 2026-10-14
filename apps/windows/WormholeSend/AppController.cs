using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;

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
    /// A language, or null for the system's.
    void ChangeLanguage(UiLanguage? language);
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
    readonly DragMonitor dragMonitor = new();
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
        model.ReceiveAnswered += AnswerReceive;
        model.Sounded += Sounds.Play;
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
        // Not while `--demo` holds a phase or `--selftest` runs its drags.
        if (options.Demo is null && !options.SelfTest)
        {
            dragMonitor.Started += DragStartedElsewhere;
            dragMonitor.Moved += () =>
            {
                if (model.Phase is Phase.Hint) FollowCursor();
            };
            dragMonitor.Ended += DragEndedElsewhere;
            dragMonitor.Start();
        }

        if (options.Fake)
        {
            tray = new TrayIcon(BuildFakeMenu, null);
            Demo.Apply(options, model, this);
            if (options.SelfTest) _ = RunSelfTest();
            if (options.Menu) ShowMenuForCheck(options.Snapshot);
            return;
        }
        model.Devices = [];
        tray = new TrayIcon(BuildMenu, OpenSettings);
        if (options.Menu) ShowMenuForCheck(options.Snapshot);
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
        config["language"] = L.Code(L.Current);
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
            LoadLocalSend();
            return null;
        }
        catch (Exception e) when (e is CoreException or DllNotFoundException or EntryPointNotFoundException)
        {
            var message = e is CoreException ? e.Message : L.T("找不到核心库 wormhole.dll", "wormhole.dll is missing", "wormhole.dll が見つかりません");
            Log.Write("core failed to start: " + message);
            if (alias is null) tray?.Notify(L.T("Wormhole Send 无法启动", "Wormhole Send can't start", "Wormhole Send を起動できません"), message);
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
                model.ShowNotice(L.T($"{unpaired.Alias} 已解除配对", $"{unpaired.Alias} unpaired", $"{unpaired.Alias} がペアリングを解除しました"), success: false);
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

    /// Once, at start: then device.updated keeps the list. localsend.list
    /// also announces this device, and every LocalSend device answers each
    /// announcement packet on a connection of its own, which it keeps open
    /// for a minute; the transfer service takes 8 per address, so a send
    /// right after an announcement could find no room.
    void LoadLocalSend()
    {
        try
        {
            if (core?.Command("localsend.list") is JsonArray localSend)
                model.LocalSendDevices = localSend.OfType<JsonNode>().Select(Device.FromCore).ToList();
        }
        catch (CoreException e)
        {
            Log.Write("localsend.list: " + e.Message);
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
            ? L.T("下载", "Downloads", "ダウンロード") + dir[downloads.Length..]
            : dir;
    }

    /// A LocalSend device's request. Accepted files come in as a receive; in
    /// `--fake`, a simulated one.
    void AnswerReceive(string requestId, bool accept)
    {
        if (core is null)
        {
            if (accept && requestId == "fake-files") model.Fake.Receive(Device.LocalSendSamples[0]);
            return;
        }
        try
        {
            core.Command("receive.respond", new JsonObject { ["requestId"] = requestId, ["accept"] = accept });
        }
        catch (CoreException e)
        {
            model.ShowNotice(e.Message, success: false);
        }
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

    /// The core's messages switch at once; the islands and the settings
    /// window are built again in the new language (the others when they
    /// next open).
    public void ChangeLanguage(UiLanguage? language)
    {
        L.Choice = language;
        Log.Write($"language {L.Code(L.Current)}");
        try
        {
            core?.Command("language.set", new JsonObject { ["language"] = L.Code(L.Current) });
        }
        catch (CoreException e)
        {
            Log.Write("language.set: " + e.Message);
        }
        SettingsSaved(settings);
        BuildWindows();
        if (settingsWindow is { } open)
        {
            open.Close();
            OpenSettings();
        }
    }

    public void OpenPairing()
    {
        if (core is null)
        {
            tray?.Notify("Wormhole Send", L.T("服务没有在运行，暂时不能配对", "The services aren't running; you can't pair now", "サービスが動いていないため、今はペアリングできません"));
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
    IReadOnlyList<MenuEntry> BuildMenu() =>
    [
        new(L.T("默认设备", "Default device", "既定のデバイス"), Glyphs.Star, Submenu: () => model.Devices.Count == 0
            ? [new MenuEntry(L.T("还没有配对设备", "No paired devices yet", "ペアリングしたデバイスはまだありません"), Enabled: false)]
            : model.Devices.Select(device => new MenuEntry(
                device.Name, device.Glyph, () => SetDefault(device), Checked: device.IsDefault, Dimmed: !device.Online)).ToList()),
        new(L.T("配对新设备…", "Pair a new device…", "新しいデバイスとペアリング…"), Glyphs.Link, OpenPairing, Enabled: core is not null),
        new(L.T("打开接收文件夹", "Open the receive folder", "受信フォルダを開く"), Glyphs.FolderOpen, OpenSaveDir, Enabled: settings is not null),
        SoundsEntry(),
        new(L.T("设置…", "Settings…", "設定…"), Glyphs.Settings, OpenSettings),
        MenuEntry.Separator,
        new(L.T("退出", "Exit", "終了"), Glyphs.Power, Quit),
    ];

    /// On by default; turning them on plays one.
    static MenuEntry SoundsEntry() => new(L.T("提示音", "Sounds", "サウンド"), Glyphs.Volume, () =>
    {
        Sounds.Enabled = !Sounds.Enabled;
        if (Sounds.Enabled) Sounds.Play(Sound.Sent);
    }, Checked: Sounds.Enabled);

    /// Switches for the simulated devices and transfers (`--fake`).
    IReadOnlyList<MenuEntry> BuildFakeMenu() =>
    [
        new("Wormhole Send · 假数据", Enabled: false),
        MenuEntry.Separator,
        new("配对设备", Glyphs.Devices, Submenu: () => new[] { 0, 1, 3 }
            .Select(count => new MenuEntry($"{count} 台", Action: () => model.Devices = Device.Samples.Take(count).ToList(),
                Checked: model.Devices.Count == count))
            .ToList()),
        new("默认设备离线", Glyphs.NoNetwork,
            () => model.Devices = model.Devices.Select(d => d.IsDefault ? d with { Online = !d.Online } : d).ToList(),
            Enabled: model.DefaultDevice is not null, Checked: model.DefaultDevice?.Online == false),
        new("发送结果", Glyphs.Send, Submenu: () => Enum.GetValues<FakeTransfers.Outcome>()
            .Select(outcome => new MenuEntry(FakeTransfers.Title(outcome), Action: () => model.Fake.NextOutcome = outcome,
                Checked: model.Fake.NextOutcome == outcome))
            .ToList()),
        MenuEntry.Separator,
        new("模拟接收文件", Glyphs.Document, () =>
        {
            Activate(Home);
            model.Fake.Receive(model.Devices.FirstOrDefault(d => d.Id == "air") ?? Device.Samples[1]);
        }),
        new("模拟配对请求", Glyphs.Link, () => model.ShowPairRequest("fake", "MacBook Air", "482019")),
        new("LocalSend", Glyphs.Phone, Submenu: () =>
        [
            new("在线设备", Action: () => model.LocalSendDevices = model.LocalSendDevices.Count == 0 ? Device.LocalSendSamples : [],
                Checked: model.LocalSendDevices.Count > 0),
            new("模拟发来文件", Action: () => model.Handle(new ReceiveRequest(
                "fake-files", "pixel", "Pixel 8", "mobile", "IMG_0001.jpg 等 3 个文件", 3, 8_400_000, null))),
            new("模拟发来文字", Action: () => model.Handle(new ReceiveRequest(
                "fake-text", "pixel", "Pixel 8", "mobile", "一段文字", 1, 0, Demo.SampleText))),
        ]),
        SoundsEntry(),
        MenuEntry.Separator,
        new(L.T("退出", "Exit", "終了"), Glyphs.Power, Quit),
    ];

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
        if (tray is not null) tray.Waiting = phase is Phase.Queued or Phase.AwaitingAcceptance;
        // What other devices start shows on the primary screen.
        if (phase is Phase.Receiving or Phase.PairRequest or Phase.ReceiveRequest && lastPhase is Phase.Idle && windows.Count > 0)
            Activate(Home);
        lastPhase = phase;
        returnHome.Stop();
        if (phase is Phase.Idle) returnHome.Start();
    }

    // MARK: - Drags anywhere

    /// A drag started anywhere: the hint shows on the screen under the
    /// cursor, as the notch opens on macOS, and moves with it between screens.
    void DragStartedElsewhere()
    {
        if (model.IsBusy || model.Phase.IsOverIsland) return;
        Log.Write("drag started elsewhere");
        FollowCursor();
        model.DragStarted();
    }

    void FollowCursor()
    {
        if (!Native.GetCursorPos(out var point)) return;
        var window = windows.FirstOrDefault(w =>
        {
            var b = w.Screen.Bounds;
            return point.X >= b.X && point.X < b.X + b.Width && point.Y >= b.Y && point.Y < b.Y + b.Height;
        });
        if (window is not null && window != active) Activate(window);
    }

    void DragEndedElsewhere()
    {
        Log.Write("drag ended");
        dragWatch.Stop();
        model.DragEnded();
    }

    // MARK: - Drags from the windows

    public void DragOver(IslandWindow window, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (FileChip.DraggingOut) return;
        var files = DragPaths.Of(e.Data);
        if (files is null) LogFormats(e.Data);
        // Anything but files (text, a link) only gets an answer when the
        // hint already shows for it.
        if (files is null && !model.Phase.AcceptsDrag) return;
        dragWatch.Stop();
        // A transfer in progress keeps its screen and ignores drags.
        if (model.IsBusy) return;
        if (window != active) Activate(window);
        // A new drag, possibly over a result still showing (as on macOS).
        if (!model.Phase.AcceptsDrag) model.DragStarted(files);
        model.DragCarries(files);
        model.DragMoved(e.GetPosition(window), Spec.PanelSize.Width);
        if (model.CanDrop) e.Effects = DragDropEffects.Copy;
    }

    string? loggedFormats;

    /// What a drag that isn't files carries, once for each kind.
    void LogFormats(IDataObject data)
    {
        var formats = string.Join(", ", data.GetFormats());
        if (formats == loggedFormats) return;
        loggedFormats = formats;
        Log.Write($"drag of {formats}");
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
        var files = DragPaths.Of(e.Data) ?? [];
        var taken = window == active && !FileChip.DraggingOut && model.Drop(files);
        e.Effects = taken ? DragDropEffects.Copy : DragDropEffects.None;
        model.DragEnded();
    }

    // MARK: - Checks

    /// `--menu`: the tray menu at the screen center with its first submenu
    /// open, for a look (and `--snapshot`) without clicking the tray.
    public void ShowMenuForCheck(string? snapshot)
    {
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var menu = TrayMenu.Show(options.Fake ? BuildFakeMenu() : BuildMenu(),
            new Drawing.Point(area.Left + area.Width / 2, area.Top + area.Height * 2 / 3));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        var step = 0;
        timer.Tick += (_, _) =>
        {
            if (step++ == 0)
            {
                menu.OpenFirstSubmenu();
                return;
            }
            timer.Stop();
            if (snapshot is null) return;
            var bounds = menu.Bounds;
            bounds.Inflate(24, 24);
            using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height);
            using (var g = Drawing.Graphics.FromImage(bitmap))
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
            bitmap.Save(snapshot, Drawing.Imaging.ImageFormat.Png);
            Quit();
        };
        timer.Start();
    }

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
