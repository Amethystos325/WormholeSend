using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace WormholeSend;

/// Where the island's sends go. Progress and results come back as core
/// events (`IslandModel.Handle`).
interface ITransfers
{
    /// Starts a send; returns the transfer ID.
    string Send(Device device, IReadOnlyList<string> files);
    void Cancel(string transferId);
    /// Sends the same files to the same device again; returns the new ID.
    string Retry(string transferId);
}

sealed class CoreTransfers(Core core) : ITransfers
{
    public string Send(Device device, IReadOnlyList<string> files) =>
        core.Command("send", new JsonObject
        {
            ["deviceId"] = device.Id,
            ["paths"] = new JsonArray(files.Select(f => (JsonNode)f).ToArray()),
        })!.GetValue<string>();

    public void Cancel(string transferId) =>
        core.Command("transfer.cancel", new JsonObject { ["transferId"] = transferId });

    public string Retry(string transferId) =>
        core.Command("transfer.retry", new JsonObject { ["transferId"] = transferId })!.GetValue<string>();
}

/// Simulated transfers for the fake-data mode, demos and the self-test:
/// the same events as the core, on timers.
sealed class FakeTransfers(Action<CoreEvent> emit) : ITransfers
{
    public enum Outcome { Success, Busy, Failure, Declined }

    public static string Title(Outcome outcome) => outcome switch
    {
        Outcome.Success => "成功",
        Outcome.Busy => "对方忙（排队）",
        Outcome.Declined => "LocalSend 设备拒绝",
        _ => "失败",
    };

    readonly Dictionary<string, (Device Device, IReadOnlyList<string> Files)> sends = [];
    readonly Dictionary<string, List<DispatcherTimer>> timers = [];

    public Outcome NextOutcome { get; set; } = Outcome.Success;

    public string Send(Device device, IReadOnlyList<string> files) => Start(device, files, NextOutcome);

    public string Retry(string transferId) =>
        sends.TryGetValue(transferId, out var send) ? Start(send.Device, send.Files, Outcome.Success) : throw new CoreException(L.T("找不到这次发送", "No such send", "この送信が見つかりません"));

    public void Cancel(string transferId)
    {
        if (!timers.Remove(transferId, out var list)) return;
        foreach (var timer in list) timer.Stop();
        var (device, _) = sends[transferId];
        emit(new TransferFinished(transferId, true, device.Id, device.Name, "cancelled", 0, 0, [], null));
    }

    string Start(Device device, IReadOnlyList<string> files, Outcome outcome)
    {
        var id = Guid.NewGuid().ToString();
        sends[id] = (device, files);
        timers[id] = [];
        const long total = 24_000_000;
        void Run(double seconds, double? failAt) =>
            Progress(id, true, device, total, seconds, failAt, files.Count, []);
        // A LocalSend device's user accepts first, or declines.
        if (device.LocalSend)
        {
            After(id, 0.05, () => emit(new TransferWaiting(id, device.Id, device.Name)));
            After(id, 2.5, () =>
            {
                if (outcome != Outcome.Declined)
                    Run(2.4, outcome == Outcome.Failure ? 0.42 : null);
                else
                {
                    timers.Remove(id);
                    emit(new TransferFinished(id, true, device.Id, device.Name, "declined", 0, 0, [], L.T($"{device.Name} 拒绝了接收", $"{device.Name} declined", $"{device.Name} が受信を拒否しました")));
                }
            });
            return id;
        }
        switch (outcome)
        {
            case Outcome.Success:
                After(id, 0.05, () => Run(2.4, null));
                break;
            case Outcome.Failure:
                After(id, 0.05, () => Run(2.4, 0.42));
                break;
            case Outcome.Busy:
                After(id, 0.05, () => emit(new TransferQueued(id, device.Id, 1)));
                After(id, 7, () => Run(2.4, null));
                break;
        }
        return id;
    }

    /// An incoming transfer from `device`, saved as the demo files.
    public void Receive(Device device)
    {
        var id = Guid.NewGuid().ToString();
        timers[id] = [];
        var files = DemoFiles.Make();
        After(id, 0.05, () => Progress(id, false, device, 3_600_000, 1.6, null, files.Count, files));
    }

    void Progress(string id, bool sending, Device device, long total, double seconds, double? failAt, int count, IReadOnlyList<string> saved)
    {
        emit(new TransferStarted(id, sending, device.Id, device.Name, count, total));
        var clock = Stopwatch.StartNew();
        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timers[id].Add(tick);
        tick.Tick += (_, _) =>
        {
            var fraction = Math.Min(1, clock.Elapsed.TotalSeconds / seconds);
            var bytes = (long)(total * fraction);
            emit(new TransferProgress(id, sending, device.Id, bytes, total, (int)(count * fraction)));
            if (failAt is { } at && fraction >= at)
            {
                tick.Stop();
                timers.Remove(id);
                emit(new TransferFinished(id, sending, device.Id, device.Name, "failed", 0, bytes, [], L.T("网络中断", "The network dropped", "ネットワークが切断されました")));
            }
            else if (fraction >= 1)
            {
                tick.Stop();
                timers.Remove(id);
                emit(new TransferFinished(id, sending, device.Id, device.Name, "done", count, total, saved, null));
            }
        };
        tick.Start();
    }

    void After(string id, double seconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timers[id].Add(timer);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}
