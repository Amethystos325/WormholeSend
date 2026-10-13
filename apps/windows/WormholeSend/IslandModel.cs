using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace WormholeSend;

/// The island's state machine (design doc, section 2.1), driven by drag
/// events from the windows and by core events. The drag handling is a port
/// of apps/macos/.../IslandModel.swift: keep the two in step.
sealed class IslandModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<Phase>? PhaseChanged;
    /// The user answered a pairing request on the island: pair ID, accept.
    public event Action<string, bool>? PairAnswered;

    Phase phase = new Phase.Idle();
    IReadOnlyList<Device> devices = Device.Samples;
    string dragLabel = "";
    Transfer? transfer;
    double progress;
    bool queueCollapsed;
    bool hovering;
    int waitingCount;
    string saveDirLabel = @"下载\Wormhole Send";

    IReadOnlyList<string> dragFiles = [];
    /// The drag carries no files (text, a link), as found once it reached a
    /// window; a drag seen only from outside may carry anything.
    bool dragNotFiles;
    /// Set once the cursor reached the default device row in the target
    /// state. The cursor usually comes up from below, through where the strip
    /// appears; only a move back down into the strip opens the list.
    bool armedForPicker;
    DispatcherTimer? timer;

    /// The send in progress, and the last one (for retry).
    string? sendId;
    string? lastSendId;
    /// The incoming transfer; it shows once no send is on screen.
    Incoming? receive;
    /// A pairing request that arrived while the island was busy.
    Phase.PairRequest? pendingPair;

    sealed class Incoming(string id, Transfer transfer)
    {
        public string Id { get; } = id;
        public Transfer Transfer { get; set; } = transfer;
        public double Progress { get; set; }
        /// Set when it ended: done, or anything else (interrupted).
        public string? Status { get; set; }
    }

    public IslandModel() => Transfers = Fake = new FakeTransfers(Handle);

    /// Where sends go: the fake backend until the core is connected.
    public ITransfers Transfers { get; set; }
    public FakeTransfers Fake { get; }

    public Phase Phase
    {
        get => phase;
        set
        {
            if (phase == value) return;
            phase = value;
            Changed();
            PhaseChanged?.Invoke(value);
            if (value is Phase.Idle) ShowPending();
        }
    }

    public IReadOnlyList<Device> Devices { get => devices; set => Set(ref devices, value); }
    public string DragLabel { get => dragLabel; set => Set(ref dragLabel, value); }
    public Transfer? Transfer { get => transfer; set => Set(ref transfer, value); }
    /// Progress of the transfer on screen, 0 to 1.
    public double Progress { get => progress; set => Set(ref progress, value); }
    /// The queued state shrinks to a compact badge after a few seconds.
    public bool QueueCollapsed { get => queueCollapsed; set => Set(ref queueCollapsed, value); }
    /// Paired devices waiting to send to this one.
    public int WaitingCount { get => waitingCount; set => Set(ref waitingCount, value); }
    public string SaveDirLabel { get => saveDirLabel; set => Set(ref saveDirLabel, value); }

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
    public IEnumerable<Device> OnlineDevices => devices.Where(Available);
    /// A send is on its way (queued included): drags are ignored meanwhile,
    /// one send at a time. So are they while a pairing request shows.
    public bool IsBusy => phase is Phase.Sending or Phase.Queued or Phase.PairRequest or Phase.PairWaiting;
    public bool CanDrop => phase is Phase.Target or Phase.Picker;

    static bool Available(Device device) => device.Online && !device.NeedsRepair;

    // MARK: - Drag input

    /// A drag began: over a window of this app with `files`, or elsewhere
    /// (DragMonitor) with nothing known yet of what it carries.
    public void DragStarted(IReadOnlyList<string>? files = null)
    {
        if (IsBusy) return;
        CancelTimers();
        dragFiles = files ?? [];
        dragNotFiles = false;
        DragLabel = files is null ? "" : FileLabel.Of(files);
        Phase = new Phase.Hint();
    }

    /// The drag is over a window of this app, which can tell what it
    /// carries: `files`, or null for anything else.
    public void DragCarries(IReadOnlyList<string>? files)
    {
        dragNotFiles = files is null;
        if (files is null || files.SequenceEqual(dragFiles)) return;
        dragFiles = files;
        DragLabel = FileLabel.Of(files);
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
        if (phase.AcceptsDrag)
        {
            CancelTimers();
            Phase = new Phase.Idle();
        }
        dragNotFiles = false;
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
                if (!Available(device))
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
        if (dragNotFiles)
            Phase = new Phase.Unavailable("只能发送文件");
        else if (devices.Count == 0)
            Phase = new Phase.Unavailable("还没有配对设备 · 点击配对");
        else if (!OnlineDevices.Any())
            Phase = new Phase.Unavailable(devices.Count == 1 ? $"{devices[0].Name} 离线，无法发送" : "没有在线的设备");
        else if (devices.Count == 1)
            Phase = new Phase.Picker(0);
        else if (DefaultDevice is { } device && Available(device))
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
            if (devices.Count == 1 && !Available(devices[0])) return $"{devices[0].Name} 离线，无法发送";
            if (DefaultDevice is { } device && Available(device)) return $"移到这里发送到 {device.Name}";
            return "默认设备离线，移到这里选其他设备";
        }
    }

    // MARK: - Sending

    void Send(IReadOnlyList<string> files, Device device)
    {
        CancelTimers();
        Transfer = new Transfer(device, files, FileLabel.Of(files));
        try
        {
            sendId = lastSendId = Transfers.Send(device, files);
        }
        catch (CoreException e)
        {
            Fail(e.Message);
            return;
        }
        Progress = 0;
        QueueCollapsed = false;
        Phase = new Phase.Sending();
    }

    void Refuse(Device device)
    {
        CancelTimers();
        Phase = new Phase.Refused(device.Name, device.NeedsRepair ? "需要重新配对" : "离线");
        Schedule(Spec.RefusedHold, () => Phase = new Phase.Idle());
    }

    void Fail(string reason)
    {
        Phase = new Phase.Failed(reason);
        Schedule(Spec.FailedHold, () =>
        {
            if (phase is Phase.Failed) Phase = new Phase.Idle();
        });
    }

    public void CancelTransfer()
    {
        if (sendId is { } id) Transfers.Cancel(id);
        sendId = null;
        CancelTimers();
        Phase = new Phase.Idle();
    }

    public void Retry()
    {
        if (lastSendId is not { } last) return;
        CancelTimers();
        try
        {
            sendId = lastSendId = Transfers.Retry(last);
        }
        catch (CoreException e)
        {
            Fail(e.Message);
            return;
        }
        Progress = 0;
        QueueCollapsed = false;
        Phase = new Phase.Sending();
    }

    // MARK: - Core events

    public void Handle(CoreEvent e)
    {
        switch (e)
        {
            case TransferQueued queued when queued.Id == sendId:
                if (phase is not Phase.Queued)
                {
                    QueueCollapsed = false;
                    Schedule(Spec.QueuedHint, () => QueueCollapsed = true);
                }
                Phase = new Phase.Queued(queued.Position);
                break;
            case TransferStarted { Sending: true } started when started.Id == sendId:
                CancelTimers();
                Progress = 0;
                Phase = new Phase.Sending();
                break;
            case TransferProgress { Sending: true } update when update.Id == sendId:
                if (phase is Phase.Sending) Progress = Fraction(update.Bytes, update.Total);
                break;
            case TransferFinished { Sending: true } finished when finished.Id == sendId:
                SendFinished(finished);
                break;

            case TransferStarted { Sending: false } started:
                var label = started.Files == 1 ? "1 个文件" : $"{started.Files} 个文件";
                receive = new Incoming(started.Id, new Transfer(DeviceFor(started.DeviceId, started.Alias), [], label, started.Files));
                if (CanShowReceive) ShowReceive();
                break;
            case TransferProgress { Sending: false } update when update.Id == receive?.Id:
                receive!.Progress = Fraction(update.Bytes, update.Total);
                if (phase is Phase.Receiving) Progress = receive.Progress;
                break;
            case TransferFinished { Sending: false } finished when finished.Id == receive?.Id:
                receive!.Status = finished.Status;
                receive.Transfer = receive.Transfer with { Files = finished.SavedPaths, FileCount = finished.FilesDone };
                if (phase is Phase.Receiving) ShowReceive();
                break;
            case ReceiveWaiting waiting:
                WaitingCount = waiting.Count;
                break;
            case DeviceUpdated updated when devices.Any(d => d.Id == updated.Id):
                Devices = devices.Select(d => d.Id == updated.Id ? d with { Name = updated.Alias, Online = updated.Online } : d).ToList();
                break;
        }
    }

    void SendFinished(TransferFinished finished)
    {
        sendId = null;
        CancelTimers();
        switch (finished.Status)
        {
            case "done":
                Progress = 1;
                Phase = new Phase.Done();
                Schedule(Spec.DoneHold, () => Phase = new Phase.Idle());
                break;
            case "cancelled":
                if (phase is Phase.Sending or Phase.Queued) Phase = new Phase.Idle();
                break;
            default:
                Fail(finished.Status switch
                {
                    "rejected" => "对方已解除配对，需要重新配对",
                    "busy" => "对方忙，稍后再试",
                    "offline" => "无法连接，请确认在同一网络",
                    _ => string.IsNullOrWhiteSpace(finished.Error) ? "网络中断" : finished.Error,
                });
                break;
        }
    }

    static double Fraction(long bytes, long total) => total > 0 ? Math.Clamp((double)bytes / total, 0, 1) : 0;

    Device DeviceFor(string id, string alias) =>
        devices.FirstOrDefault(d => d.Id == id) ?? new Device(id, alias, Glyphs.Desktop, Online: true, IsDefault: false);

    // MARK: - Receiving

    /// What may give way to an incoming transfer or a notice: nothing the
    /// user is doing.
    bool CanShowReceive => phase is Phase.Idle or Phase.Received or Phase.Notice or Phase.Refused or Phase.Interrupted;

    void ShowReceive()
    {
        if (receive is not { } incoming) return;
        CancelTimers();
        Transfer = incoming.Transfer;
        Progress = incoming.Progress;
        switch (incoming.Status)
        {
            case null:
                Phase = new Phase.Receiving();
                break;
            case "done":
                receive = null;
                Phase = new Phase.Received();
                HoldReceived();
                break;
            default:
                // The sender cancelled or the connection broke; the core has
                // removed the partial files.
                receive = null;
                Phase = new Phase.Interrupted(incoming.Status == "cancelled" ? "对方取消了发送" : "连接中断");
                Schedule(Spec.FailedHold, () =>
                {
                    if (phase is Phase.Interrupted) Phase = new Phase.Idle();
                });
                break;
        }
    }

    void HoldReceived()
    {
        Schedule(Spec.ReceivedHold, () =>
        {
            if (phase is Phase.Received && !hovering) Phase = new Phase.Idle();
        });
    }

    /// After the first-run guide: the hint, as a pointer to where files go.
    public void ShowIntro()
    {
        if (phase is not Phase.Idle) return;
        Phase = new Phase.Hint();
        Schedule(TimeSpan.FromSeconds(4), () =>
        {
            if (phase is Phase.Hint) Phase = new Phase.Idle();
        });
    }

    public void IslandTapped()
    {
        if (phase is Phase.Received or Phase.Notice or Phase.Interrupted)
        {
            CancelTimers();
            Phase = new Phase.Idle();
        }
    }

    public void RevealReceived()
    {
        if (transfer is { Files.Count: > 0 } received) Shell.Reveal(received.Files);
    }

    /// Once the island is idle: a pairing request first, then a transfer
    /// that came in meanwhile.
    void ShowPending()
    {
        if (pendingPair is { } request)
        {
            pendingPair = null;
            Phase = request;
        }
        else if (receive is not null)
            ShowReceive();
    }

    // MARK: - Pairing and notices

    public void ShowPairRequest(string pairId, string alias, string code)
    {
        var request = new Phase.PairRequest(pairId, alias, code);
        if (CanShowReceive)
        {
            CancelTimers();
            Phase = request;
        }
        else
            pendingPair = request;
    }

    public void AnswerPair(bool accept)
    {
        if (phase is not Phase.PairRequest request) return;
        PairAnswered?.Invoke(request.PairId, accept);
        Phase = accept ? new Phase.PairWaiting(request.Alias) : new Phase.Idle();
    }

    public void PairFinished(string pairId, string alias, bool ok, string? reason)
    {
        if (pendingPair?.PairId == pairId) pendingPair = null;
        var mine = phase is Phase.PairWaiting || (phase is Phase.PairRequest request && request.PairId == pairId);
        if (!mine) return;
        if (ok)
            ShowNotice($"已与 {alias} 配对", success: true);
        else if (phase is Phase.PairRequest)
            Phase = new Phase.Idle();
        else
            ShowNotice($"配对失败：{reason ?? "未知原因"}", success: false);
    }

    /// A short message, unless the user is busy with something else.
    public void ShowNotice(string text, bool success)
    {
        if (!(CanShowReceive || phase is Phase.PairRequest or Phase.PairWaiting)) return;
        CancelTimers();
        Phase = new Phase.Notice(text, success);
        Schedule(success ? Spec.DoneHold : Spec.FailedHold, () =>
        {
            if (phase is Phase.Notice) Phase = new Phase.Idle();
        });
    }

    // MARK: - Timers

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

    void CancelTimers()
    {
        timer?.Stop();
        timer = null;
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
            case Phase.Done or Phase.Failed or Phase.Refused or Phase.Notice or Phase.PairWaiting or Phase.Interrupted:
                content = new Size(340, 28);
                break;
            case Phase.Receiving:
                content = new Size(384, 60);
                break;
            case Phase.PairRequest:
                content = new Size(380, 78);
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
