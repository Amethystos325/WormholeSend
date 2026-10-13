using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace WormholeSend;

/// The island's state machine (design doc, section 2.1), driven by drag
/// events from the windows. Transfers are simulated. A port of
/// apps/macos/.../IslandModel.swift: keep the two in step.
sealed class IslandModel : INotifyPropertyChanged
{
    public enum SendOutcome { Success, Busy, Failure }

    public static string Title(SendOutcome outcome) => outcome switch
    {
        SendOutcome.Success => "成功",
        SendOutcome.Busy => "对方忙（排队）",
        _ => "失败",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<Phase>? PhaseChanged;

    Phase phase = new Phase.Idle();
    IReadOnlyList<Device> devices = Device.Samples;
    string dragLabel = "";
    Transfer? transfer;
    double progress;
    bool queueCollapsed;
    bool hovering;
    SendOutcome nextOutcome = SendOutcome.Success;

    IReadOnlyList<string> dragFiles = [];
    /// Set once the cursor reached the default device row in the target
    /// state. The cursor usually comes up from below, through where the strip
    /// appears; only a move back down into the strip opens the list.
    bool armedForPicker;
    DispatcherTimer? timer;
    DispatcherTimer? progressTimer;

    public Phase Phase
    {
        get => phase;
        set
        {
            if (phase == value) return;
            phase = value;
            Changed();
            PhaseChanged?.Invoke(value);
        }
    }

    public IReadOnlyList<Device> Devices { get => devices; set => Set(ref devices, value); }
    public string DragLabel { get => dragLabel; set => Set(ref dragLabel, value); }
    public Transfer? Transfer { get => transfer; set => Set(ref transfer, value); }
    /// Progress of the current transfer, 0 to 1.
    public double Progress { get => progress; set => Set(ref progress, value); }
    /// The queued state shrinks to a compact badge after a few seconds.
    public bool QueueCollapsed { get => queueCollapsed; set => Set(ref queueCollapsed, value); }
    public SendOutcome NextOutcome { get => nextOutcome; set => Set(ref nextOutcome, value); }

    public bool Hovering
    {
        get => hovering;
        set
        {
            if (!Set(ref hovering, value)) return;
            if (phase is Phase.Received && !value) HoldReceived();
        }
    }

    public Device? DefaultDevice => devices.FirstOrDefault(d => d.IsDefault);
    public IEnumerable<Device> OnlineDevices => devices.Where(d => d.Online);
    public bool IsBusy => phase is Phase.Sending or Phase.Queued or Phase.Receiving;
    public bool CanDrop => phase is Phase.Target or Phase.Picker;

    // MARK: - Drag input

    public void DragStarted(IReadOnlyList<string> files)
    {
        if (IsBusy) return;
        CancelTimers();
        dragFiles = files;
        DragLabel = FileLabel.Of(files);
        Phase = new Phase.Hint();
    }

    /// The cursor moved over the window; `point` is in window coordinates
    /// with the origin at the top left.
    public void DragMoved(Point point, double panelWidth)
    {
        var body = IslandRect(panelWidth);
        var rect = body;
        rect.Inflate(Spec.HitOutset, Spec.HitOutset);
        var over = rect.Contains(point);
        if (over && phase is Phase.Hint)
            EnterIsland();
        else if (!over && phase.IsOverIsland)
            LeaveIsland();

        if (phase is Phase.Target)
        {
            var stripTop = IslandRect(panelWidth).Bottom - Spec.MoreStripHeight;
            if (point.Y < stripTop - 4)
                armedForPicker = true;
            else if (armedForPicker)
                Phase = new Phase.Picker(null);
        }
        if (phase is Phase.Picker && devices.Count > 1)
        {
            body = IslandRect(panelWidth);
            var columnWidth = (body.Width - 28) / devices.Count;
            var index = (int)Math.Floor((point.X - body.Left - 14) / columnWidth);
            Phase = new Phase.Picker(Math.Clamp(index, 0, devices.Count - 1));
        }
    }

    public void DragLeftPanel()
    {
        if (phase.IsOverIsland) LeaveIsland();
    }

    /// The mouse button was released. A drop, if any, has been handled.
    public void DragEnded()
    {
        if (phase is Phase.Hint or Phase.Target or Phase.Picker or Phase.Unavailable)
        {
            CancelTimers();
            Phase = new Phase.Idle();
        }
    }

    /// Returns whether the drop was taken.
    public bool Drop(IReadOnlyList<string> paths)
    {
        var files = paths.Count == 0 ? dragFiles : paths;
        DragLabel = FileLabel.Of(files);
        switch (phase)
        {
            case Phase.Target:
                if (DefaultDevice is not { } target) return false;
                Send(files, target);
                return true;
            case Phase.Picker(var hot):
                int? index = devices.Count == 1 ? 0 : hot;
                if (index is not { } i)
                {
                    Phase = new Phase.Idle();
                    return false;
                }
                var device = devices[i];
                if (!device.Online)
                {
                    Refuse(device);
                    return false;
                }
                Send(files, device);
                return true;
            default:
                return false;
        }
    }

    void EnterIsland()
    {
        if (devices.Count == 0)
            Phase = new Phase.Unavailable("还没有配对设备 · 点击配对");
        else if (!OnlineDevices.Any())
            Phase = new Phase.Unavailable(devices.Count == 1 ? $"{devices[0].Name} 离线，无法发送" : "没有在线的设备");
        else if (devices.Count == 1)
            Phase = new Phase.Picker(0);
        else if (DefaultDevice?.Online == true)
        {
            armedForPicker = false;
            Phase = new Phase.Target();
        }
        else
            Phase = new Phase.Picker(null);
    }

    void LeaveIsland()
    {
        CancelTimers();
        Phase = new Phase.Hint();
    }

    // MARK: - Hint text

    public string HintText
    {
        get
        {
            if (devices.Count == 0) return "还没有配对设备";
            if (devices.Count == 1 && !devices[0].Online) return $"{devices[0].Name} 离线，无法发送";
            if (DefaultDevice is { Online: true } device) return $"移到这里发送到 {device.Name}";
            return "默认设备离线，移到这里选其他设备";
        }
    }

    // MARK: - Simulated transfers

    void Send(IReadOnlyList<string> files, Device device)
    {
        CancelTimers();
        Transfer = new Transfer(device, files, FileLabel.Of(files));
        switch (nextOutcome)
        {
            case SendOutcome.Success:
                StartSending(failAt: null);
                break;
            case SendOutcome.Failure:
                StartSending(failAt: 0.42);
                break;
            case SendOutcome.Busy:
                QueueCollapsed = false;
                Phase = new Phase.Queued(1);
                Schedule(Spec.QueuedHint, () =>
                {
                    QueueCollapsed = true;
                    Schedule(TimeSpan.FromSeconds(4), () => StartSending(failAt: null));
                });
                break;
        }
    }

    void StartSending(double? failAt)
    {
        Progress = 0;
        Phase = new Phase.Sending();
        AnimateProgress(2.4, failAt, () =>
        {
            Phase = new Phase.Done();
            Schedule(Spec.DoneHold, () => Phase = new Phase.Idle());
        });
    }

    void Refuse(Device device)
    {
        CancelTimers();
        Phase = new Phase.Refused(device.Name);
        Schedule(Spec.RefusedHold, () => Phase = new Phase.Idle());
    }

    public void CancelTransfer()
    {
        CancelTimers();
        Phase = new Phase.Idle();
    }

    public void Retry()
    {
        if (transfer is null) return;
        CancelTimers();
        StartSending(failAt: null);
    }

    public void SimulateReceive()
    {
        if (phase is not Phase.Idle) return;
        var files = DemoFiles.Make();
        var sender = devices.FirstOrDefault(d => d.Id == "air") ?? Device.Samples[1];
        Transfer = new Transfer(sender, files, FileLabel.Of(files));
        Progress = 0;
        Phase = new Phase.Receiving();
        AnimateProgress(1.6, null, () =>
        {
            Phase = new Phase.Received();
            HoldReceived();
        });
    }

    void HoldReceived()
    {
        Schedule(Spec.ReceivedHold, () =>
        {
            if (phase is Phase.Received && !hovering) Phase = new Phase.Idle();
        });
    }

    public void IslandTapped()
    {
        if (phase is Phase.Received)
        {
            CancelTimers();
            Phase = new Phase.Idle();
        }
    }

    public void RevealReceived()
    {
        if (transfer is { Files.Count: > 0 } received) Shell.Reveal(received.Files);
    }

    void AnimateProgress(double seconds, double? failAt, Action completion)
    {
        progressTimer?.Stop();
        var clock = Stopwatch.StartNew();
        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        tick.Tick += (_, _) =>
        {
            if (progressTimer != tick) return;
            var value = Math.Min(1, clock.Elapsed.TotalSeconds / seconds);
            Progress = value;
            if (failAt is { } at && value >= at)
            {
                StopProgress();
                Phase = new Phase.Failed("网络中断");
                Schedule(Spec.FailedHold, () =>
                {
                    if (phase is Phase.Failed) Phase = new Phase.Idle();
                });
            }
            else if (value >= 1)
            {
                StopProgress();
                completion();
            }
        };
        progressTimer = tick;
        tick.Start();
    }

    void Schedule(TimeSpan delay, Action action)
    {
        timer?.Stop();
        var once = new DispatcherTimer { Interval = delay };
        once.Tick += (_, _) =>
        {
            once.Stop();
            if (timer != once) return;
            timer = null;
            action();
        };
        timer = once;
        once.Start();
    }

    void StopProgress()
    {
        progressTimer?.Stop();
        progressTimer = null;
    }

    void CancelTimers()
    {
        timer?.Stop();
        timer = null;
        StopProgress();
    }

    // MARK: - Geometry

    /// The island body for the current phase.
    public Size BodySize => BodySizeFor(phase);

    public Size BodySizeFor(Phase phase)
    {
        Size content;
        switch (phase)
        {
            case Phase.Idle:
                return new Size(160, 30);
            case Phase.Hint:
                content = new Size(260, 22);
                break;
            // The default device row, then the strip (`Spec.MoreStripHeight`
            // including the bottom padding) along the bottom edge.
            case Phase.Target:
                content = new Size(360, 92);
                break;
            case Phase.Picker:
                content = devices.Count == 1 ? new Size(380, 64) : new Size(Spec.PickerWidth, 122);
                break;
            case Phase.Unavailable:
                content = new Size(300, 24);
                break;
            case Phase.Queued when queueCollapsed:
                return new Size(150, 30);
            case Phase.Sending or Phase.Queued:
                content = new Size(340, 40);
                break;
            case Phase.Done or Phase.Failed or Phase.Refused:
                content = new Size(340, 28);
                break;
            case Phase.Receiving:
                content = new Size(384, 60);
                break;
            default:
                content = new Size(400, 76);
                break;
        }
        return new Size(content.Width, content.Height + 24);
    }

    /// The island body in window coordinates (origin top left).
    public Rect IslandRect(double panelWidth)
    {
        var size = BodySize;
        return new Rect((panelWidth - size.Width) / 2, Spec.CapsuleTop, size.Width, size.Height);
    }

    bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }

    void Changed([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
