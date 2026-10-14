using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
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
    /// The user answered a LocalSend device's request: request ID, accept.
    public event Action<string, bool>? ReceiveAnswered;
    /// A moment that sounds (Sounds.cs).
    public event Action<Sound>? Sounded;

    Phase phase = new Phase.Idle();
    IReadOnlyList<Device> devices = Device.Samples;
    IReadOnlyList<Device> localSend = [];
    string dragLabel = "";
    Transfer? transfer;
    double progress;
    TransferStats? sendStats;
    bool queueCollapsed;
    bool hovering;
    int waitingCount;
    string saveDirLabel = L.T("下载\\Wormhole Send", "Downloads\\Wormhole Send", "ダウンロード\\Wormhole Send");

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
    /// A LocalSend device's request that arrived while the island was busy.
    Phase.ReceiveRequest? pendingRequest;
    /// When the requests not answered yet are declined, by `Clock`.
    readonly Dictionary<string, TimeSpan> requestDeadlines = [];

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

    /// The paired devices, default first.
    public IReadOnlyList<Device> Devices { get => devices; set => Set(ref devices, value); }
    /// LocalSend devices online: they can be sent to, after the paired ones.
    public IReadOnlyList<Device> LocalSendDevices { get => localSend; set => Set(ref localSend, value); }
    /// What a drag can go to: the paired devices, then the LocalSend ones.
    public IReadOnlyList<Device> Targets => [.. devices, .. localSend];
    /// Copies a text message to the clipboard; a test sets its own.
    public Func<string, bool> CopyText { get; set; } = text =>
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    };
    public string DragLabel { get => dragLabel; set => Set(ref dragLabel, value); }
    public Transfer? Transfer { get => transfer; set => Set(ref transfer, value); }
    /// Progress of the transfer on screen, 0 to 1.
    public double Progress { get => progress; set => Set(ref progress, value); }
    /// Sizes or files done of the send, and its speed: the line under its
    /// progress bar.
    public string SendDetail => sendStats?.Line ?? "";
    /// For the progress events' times; a test sets its own.
    public Func<TimeSpan> Clock { get; set; } = () => TimeSpan.FromMilliseconds(Environment.TickCount64);
    /// The queued state shrinks to a compact badge after a few seconds.
    public bool QueueCollapsed { get => queueCollapsed; set => Set(ref queueCollapsed, value); }
    /// Paired devices waiting to send to this one.
    public int WaitingCount { get => waitingCount; set => Set(ref waitingCount, value); }
    public string SaveDirLabel { get => saveDirLabel; set => Set(ref saveDirLabel, value); }

    /// Starts the line under the send's progress bar afresh (null: empty
    /// until the core says what it sends); `--demo sending` passes its own.
    public void ShowSendStats(TransferStats? stats)
    {
        sendStats = stats;
        Changed(nameof(SendDetail));
    }

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
    /// A send is on its way (queued, or waiting for a LocalSend user, too):
    /// drags are ignored meanwhile, one send at a time. So are they while a
    /// pairing request or a LocalSend device's request shows.
    public bool IsBusy => phase is Phase.Sending or Phase.Queued or Phase.AwaitingAcceptance
        or Phase.PairRequest or Phase.PairWaiting or Phase.ReceiveRequest;
    public bool CanDrop => phase is Phase.Target or Phase.Picker;

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
        var targets = Targets;
        if (phase is Phase.Picker && targets.Count > 1)
        {
            body = IslandRect(panelWidth);
            var columnWidth = (body.Width - 28) / targets.Count;
            var index = (int)Math.Floor((point.X - body.Left - 14) / columnWidth);
            Phase = new Phase.Picker(Math.Clamp(index, 0, targets.Count - 1));
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
                var targets = Targets;
                int? index = targets.Count == 1 ? 0 : hot;
                if (index is not { } i || i >= targets.Count)
                {
                    Phase = new Phase.Idle();
                    return false;
                }
                var device = targets[i];
                if (!device.Usable)
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
        var targets = Targets;
        if (dragNotFiles)
            Phase = new Phase.Unavailable(L.T("只能发送文件", "Only files can be sent", "送信できるのはファイルだけです"));
        else if (targets.Count == 0)
            Phase = new Phase.Unavailable(L.T("还没有配对设备 · 点击配对", "No paired devices · Click to pair", "ペアリングしたデバイスがありません · クリックしてペアリング"));
        else if (!targets.Any(d => d.Usable))
            Phase = new Phase.Unavailable(targets.Count == 1
                ? L.T($"{targets[0].Name} 离线，无法发送", $"{targets[0].Name} is offline", $"{targets[0].Name} はオフラインです")
                : L.T("没有在线的设备", "No devices online", "オンラインのデバイスがありません"));
        else if (targets.Count == 1)
            Phase = new Phase.Picker(0);
        else if (DefaultDevice is { } device && device.Usable)
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
            if (devices.Count == 0)
                return localSend.Count == 0
                    ? L.T("还没有配对设备", "No paired devices yet", "ペアリングしたデバイスがありません")
                    : L.T("移到这里选择设备", "Move here to choose a device", "ここに移動してデバイスを選択");
            if (devices.Count == 1 && !devices[0].Usable && localSend.Count == 0) return L.T($"{devices[0].Name} 离线，无法发送", $"{devices[0].Name} is offline", $"{devices[0].Name} はオフラインです");
            if (DefaultDevice is { } device && device.Usable)
                return L.T($"移到这里发送到 {device.Name}", $"Move here to send to {device.Name}", $"ここに移動して {device.Name} に送信");
            return L.T("默认设备离线，移到这里选其他设备", "Default device offline. Move here to choose another", "既定のデバイスはオフラインです。ここに移動して別のデバイスを選択");
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
        ShowSendStats(null);
        QueueCollapsed = false;
        Phase = new Phase.Sending();
    }

    void Refuse(Device device)
    {
        CancelTimers();
        Phase = new Phase.Refused(device.Name, device.NeedsRepair);
        Schedule(Spec.RefusedHold, () => Phase = new Phase.Idle());
    }

    void Fail(string reason, bool canRetry = true)
    {
        Sounded?.Invoke(Sound.Failed);
        Phase = new Phase.Failed(reason, canRetry);
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
        ShowSendStats(null);
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
            case TransferWaiting waiting when waiting.Id == sendId:
                CancelTimers();
                Phase = new Phase.AwaitingAcceptance(waiting.Alias);
                break;
            case TransferStarted { Sending: true } started when started.Id == sendId:
                CancelTimers();
                Progress = 0;
                ShowSendStats(new TransferStats(started.Total, started.Files));
                Phase = new Phase.Sending();
                break;
            case TransferProgress { Sending: true } update when update.Id == sendId:
                if (phase is not Phase.Sending) break;
                Progress = Fraction(update.Bytes, update.Total);
                sendStats ??= new TransferStats();
                sendStats.Update(update.Bytes, update.Total, update.FilesDone, Clock());
                Changed(nameof(SendDetail));
                break;
            case TransferFinished { Sending: true } finished when finished.Id == sendId:
                SendFinished(finished);
                break;

            case TransferStarted { Sending: false } started:
                var label = L.Files(started.Files);
                receive = new Incoming(started.Id, new Transfer(DeviceFor(started.DeviceId, started.Alias), [], label, started.Files));
                if (CanShowReceive) ShowReceive();
                break;
            case TransferProgress { Sending: false } update when update.Id == receive?.Id:
                receive!.Progress = Fraction(update.Bytes, update.Total);
                if (phase is Phase.Receiving) Progress = receive.Progress;
                break;
            case TransferFinished { Sending: false } finished when finished.Id == receive?.Id:
                // A cancel was the sender's choice; only a broken connection sounds.
                if (finished.Status != "cancelled")
                    Sounded?.Invoke(finished.Status == "done" ? Sound.Received : Sound.Failed);
                receive!.Status = finished.Status;
                receive.Transfer = receive.Transfer with { Files = finished.SavedPaths, FileCount = finished.FilesDone };
                if (phase is Phase.Receiving) ShowReceive();
                break;
            case ReceiveWaiting waiting:
                WaitingCount = waiting.Count;
                break;
            case DeviceUpdated { LocalSend: true } updated:
                var others = localSend.Where(d => d.Id != updated.Id);
                LocalSendDevices = updated.Online
                    ? [.. others, new Device(updated.Id, updated.Alias, DeviceGlyphs.For(updated.DeviceType), Online: true, IsDefault: false, LocalSend: true)]
                    : [.. others];
                break;
            case ReceiveRequest request:
                requestDeadlines[request.RequestId] = Clock() + Spec.RequestTimeout;
                ShowReceiveRequest(new Phase.ReceiveRequest(request.RequestId, request.Alias, request.Label, request.Total, request.Text));
                break;
            // Unanswered for a minute, it just goes; the sender giving up is said.
            case ReceiveClosed closed:
                requestDeadlines.Remove(closed.RequestId);
                if (pendingRequest?.RequestId == closed.RequestId) pendingRequest = null;
                if (phase is not Phase.ReceiveRequest shown || shown.RequestId != closed.RequestId) break;
                if (closed.Reason == "cancelled")
                    Notify(new Phase.Notice(L.T($"{shown.Alias} 取消了发送", $"{shown.Alias} cancelled", $"{shown.Alias} が送信をキャンセルしました"), Success: false));
                else
                    Phase = new Phase.Idle();
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
                Sounded?.Invoke(Sound.Sent);
                Progress = 1;
                Phase = new Phase.Done();
                Schedule(Spec.DoneHold, () => Phase = new Phase.Idle());
                break;
            case "cancelled":
                if (phase is Phase.Sending or Phase.Queued or Phase.AwaitingAcceptance) Phase = new Phase.Idle();
                break;
            // Asking again would only ask the same user again: no retry.
            case "declined":
                Fail(string.IsNullOrWhiteSpace(finished.Error) ? L.T("对方拒绝了接收", "Declined", "相手が受信を拒否しました") : finished.Error, canRetry: false);
                break;
            default:
                Fail(finished.Status switch
                {
                    "rejected" => L.T("对方已解除配对，需要重新配对", "It unpaired this device; pair again", "相手がペアリングを解除しました。再ペアリングしてください"),
                    "busy" => L.T("对方忙，稍后再试", "It's busy; try again later", "相手は取り込み中です。しばらくしてからやり直してください"),
                    "offline" => L.T("无法连接，请确认在同一网络", "Can't connect; make sure you're on the same network", "接続できません。同じネットワークにいるか確認してください"),
                    _ => string.IsNullOrWhiteSpace(finished.Error) ? L.T("网络中断", "The network dropped", "ネットワークが切断されました") : finished.Error,
                });
                break;
        }
    }

    static double Fraction(long bytes, long total) => total > 0 ? Math.Clamp((double)bytes / total, 0, 1) : 0;

    Device DeviceFor(string id, string alias) =>
        devices.FirstOrDefault(d => d.Id == id) ?? new Device(id, alias, DeviceGlyph.Desktop, Online: true, IsDefault: false);

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
                Phase = new Phase.Interrupted(incoming.Status == "cancelled"
                    ? L.T("对方取消了发送", "The sender cancelled", "送信者がキャンセルしました")
                    : L.T("连接中断", "The connection dropped", "接続が切れました"));
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

    /// Once the island is idle: a pairing request first, then a LocalSend
    /// device's request, then a transfer that came in meanwhile.
    void ShowPending()
    {
        if (pendingPair is { } pair)
        {
            pendingPair = null;
            Phase = pair;
        }
        else if (pendingRequest is { } request)
        {
            pendingRequest = null;
            Phase = request;
        }
        else if (receive is not null)
            ShowReceive();
    }

    // MARK: - Pairing and notices

    public void ShowPairRequest(string pairId, string alias, string code)
    {
        Sounded?.Invoke(Sound.PairRequest);
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
            ShowNotice(L.T($"已与 {alias} 配对", $"Paired with {alias}", $"{alias} とペアリングしました"), success: true);
        else if (phase is Phase.PairRequest)
            Phase = new Phase.Idle();
        else
            ShowNotice(L.T($"配对失败：{reason ?? "未知原因"}", $"Pairing failed: {reason ?? "unknown reason"}", $"ペアリングに失敗しました：{reason ?? "原因不明"}"), success: false);
    }

    // MARK: - LocalSend requests

    /// A LocalSend device asks to send; it shows when nothing else does, as
    /// a pairing request, and is declined after a minute without an answer.
    void ShowReceiveRequest(Phase.ReceiveRequest request)
    {
        Sounded?.Invoke(Sound.PairRequest);
        if (CanShowReceive)
        {
            CancelTimers();
            Phase = request;
        }
        else
            pendingRequest = request;
    }

    /// Accepting files lets them come in as a receive; accepting a text
    /// copies it, and the island says so with the start of the text.
    public void AnswerReceive(bool accept)
    {
        if (phase is not Phase.ReceiveRequest request) return;
        requestDeadlines.Remove(request.RequestId);
        ReceiveAnswered?.Invoke(request.RequestId, accept);
        if (!accept || request.Text is not { } text)
        {
            Phase = new Phase.Idle();
            return;
        }
        Notify(CopyText(text)
            ? new Phase.Notice(L.T("已复制到剪贴板", "Copied", "コピーしました"), Success: true, Preview(text))
            : new Phase.Notice(L.T("无法写入剪贴板", "Couldn't copy to the clipboard", "クリップボードにコピーできませんでした"), Success: false));
    }

    /// Whole seconds until the core declines the request, for its button;
    /// null once it is answered or closed.
    public int? SecondsLeft(string requestId) =>
        requestDeadlines.TryGetValue(requestId, out var deadline)
            ? Math.Max(0, (int)Math.Ceiling((deadline - Clock()).TotalSeconds))
            : null;

    /// The start of a text, on one line; the island shortens it further to
    /// what fits.
    public static string Preview(string text)
    {
        var line = string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var starts = System.Globalization.StringInfo.ParseCombiningCharacters(line);
        return starts.Length <= 40 ? line : line[..starts[40]] + "…";
    }

    /// A short message, unless the user is busy with something else.
    public void ShowNotice(string text, bool success)
    {
        if (!(CanShowReceive || phase is Phase.PairRequest or Phase.PairWaiting)) return;
        Notify(new Phase.Notice(text, success));
    }

    void Notify(Phase.Notice notice)
    {
        CancelTimers();
        Phase = notice;
        Schedule(notice.Success ? Spec.DoneHold : Spec.FailedHold, () =>
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
        // The one-line states are as wide as what they say (StatusLines).
        var fit = StatusLines.Fit(this, phase)?.Width;
        switch (phase)
        {
            case Phase.Idle:
                return new Size(160, 30);
            case Phase.Hint:
                content = new Size(fit ?? 260, 22);
                break;
            // The default device row, then the strip (`Spec.MoreStripHeight`
            // including the bottom padding) along the bottom edge.
            case Phase.Target:
                content = new Size(360, 92);
                break;
            case Phase.Picker:
                var count = Targets.Count;
                content = count == 1 ? new Size(380, 64) : new Size(PickerWidth(count), 122);
                break;
            case Phase.Unavailable:
                content = new Size(fit ?? 300, 24);
                break;
            case Phase.Queued when queueCollapsed:
                return new Size(150, 30);
            // The sending island has a line of sizes and speed under the bar.
            case Phase.Sending:
                content = new Size(340, 56);
                break;
            case Phase.Queued or Phase.AwaitingAcceptance:
                content = new Size(340, 40);
                break;
            case Phase.Done or Phase.Failed or Phase.Refused or Phase.Notice or Phase.PairWaiting or Phase.Interrupted:
                content = new Size(fit ?? 340, 28);
                break;
            case Phase.Receiving:
                content = new Size(384, 60);
                break;
            case Phase.PairRequest:
                content = new Size(380, 78);
                break;
            // A text message shows its first two lines.
            case Phase.ReceiveRequest { Text: { } text } when !OneLine(text):
                content = new Size(380, 92);
                break;
            case Phase.ReceiveRequest:
                content = new Size(380, 78);
                break;
            default:
                content = new Size(400, 76);
                break;
        }
        return new Size(content.Width, content.Height + 24);
    }

    /// Whether a text message fits on one line of its request (12 px, the
    /// island's 380 less the padding).
    static bool OneLine(string text) =>
        !text.Contains('\n') && new FormattedText(
            text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Fonts.Ui, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, Palette.Text, 1)
            .WidthIncludingTrailingWhitespace <= 348;

    /// The device list grows past its usual width from five devices on, up
    /// to the window's.
    static double PickerWidth(int count) =>
        Math.Min(Spec.PanelSize.Width - 32, Math.Max(Spec.PickerWidth, 28 + 84 * count));

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
