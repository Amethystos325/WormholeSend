using System.Windows;

namespace WormholeSend.Tests;

/// Records the sends instead of transferring anything.
sealed class RecordingTransfers : ITransfers
{
    public List<(Device Device, IReadOnlyList<string> Files)> Sent { get; } = [];
    public List<string> Cancelled { get; } = [];

    public string Send(Device device, IReadOnlyList<string> files)
    {
        Sent.Add((device, files));
        return $"send-{Sent.Count}";
    }

    public void Cancel(string transferId) => Cancelled.Add(transferId);

    public string Retry(string transferId) => Send(Sent[^1].Device, Sent[^1].Files);
}

/// How the island follows the core's events (design doc 2.1, 2.2, 4.2).
public class CoreEventTests
{
    static readonly string[] Files = [@"C:\Users\Public\季度报告.pdf"];
    static readonly Device Desk = Device.Samples[0];

    static (IslandModel Model, RecordingTransfers Transfers) Sending()
    {
        var transfers = new RecordingTransfers();
        var model = new IslandModel { Transfers = transfers };
        model.DragStarted(Files);
        model.DragMoved(new Point(280, 3), 560);
        Assert.True(model.Drop(Files));
        return (model, transfers);
    }

    static TransferFinished Finished(string id, bool sending, string status, IReadOnlyList<string>? saved = null) =>
        new(id, sending, Desk.Id, Desk.Name, status, 1, 100, saved ?? [], status == "failed" ? "连接被重置" : null);

    [Fact]
    public void ASendGoesThroughTheQueueToDone()
    {
        var (model, transfers) = Sending();
        Assert.Equal(Desk, transfers.Sent.Single().Device);
        Assert.Equal(new Phase.Sending(), model.Phase);

        model.Handle(new TransferQueued("send-1", Desk.Id, 2));
        Assert.Equal(new Phase.Queued(2), model.Phase);
        model.Handle(new TransferQueued("send-1", Desk.Id, 1));
        Assert.Equal(new Phase.Queued(1), model.Phase);
        Assert.True(model.IsBusy);

        model.Handle(new TransferStarted("send-1", true, Desk.Id, Desk.Name, 1, 200));
        model.Handle(new TransferProgress("send-1", true, Desk.Id, 50, 200));
        Assert.Equal(new Phase.Sending(), model.Phase);
        Assert.Equal(0.25, model.Progress);

        model.Handle(Finished("send-1", true, "done"));
        Assert.Equal(new Phase.Done(), model.Phase);
    }

    [Theory]
    [InlineData("rejected", "对方已解除配对，需要重新配对")]
    [InlineData("busy", "对方忙，稍后再试")]
    [InlineData("offline", "无法连接，请确认在同一网络")]
    [InlineData("failed", "连接被重置")]
    public void FailuresSayWhy(string status, string reason)
    {
        var (model, _) = Sending();
        model.Handle(Finished("send-1", true, status));
        Assert.Equal(new Phase.Failed(reason), model.Phase);
    }

    [Fact]
    public void RetrySendsTheSameFilesAgain()
    {
        var (model, transfers) = Sending();
        model.Handle(Finished("send-1", true, "offline"));
        model.Retry();
        Assert.Equal(new Phase.Sending(), model.Phase);
        Assert.Equal(2, transfers.Sent.Count);
        // Events of the new transfer are followed, the old one's ignored.
        model.Handle(Finished("send-1", true, "failed"));
        Assert.Equal(new Phase.Sending(), model.Phase);
        model.Handle(Finished("send-2", true, "done"));
        Assert.Equal(new Phase.Done(), model.Phase);
    }

    [Fact]
    public void CancelStopsTheSend()
    {
        var (model, transfers) = Sending();
        model.CancelTransfer();
        Assert.Equal(["send-1"], transfers.Cancelled);
        Assert.Equal(new Phase.Idle(), model.Phase);
        model.Handle(Finished("send-1", true, "cancelled"));
        Assert.Equal(new Phase.Idle(), model.Phase);
    }

    [Fact]
    public void ReceivingShowsProgressThenTheFiles()
    {
        var model = new IslandModel { Devices = Device.Samples };
        model.Handle(new TransferStarted("in-1", false, "air", "MacBook Air", 3, 400));
        Assert.Equal(new Phase.Receiving(), model.Phase);
        Assert.Equal("MacBook Air", model.Transfer?.Device.Name);
        Assert.Equal(3, model.Transfer?.FileCount);

        model.Handle(new ReceiveWaiting(2));
        model.Handle(new TransferProgress("in-1", false, "air", 100, 400));
        Assert.Equal(0.25, model.Progress);
        Assert.Equal(2, model.WaitingCount);

        string[] saved = [@"C:\Users\Public\Downloads\Wormhole Send\设计稿"];
        model.Handle(new TransferFinished("in-1", false, "air", "MacBook Air", "done", 3, 400, saved, null));
        Assert.Equal(new Phase.Received(), model.Phase);
        Assert.Equal(saved, model.Transfer?.Files);
    }

    [Fact]
    public void AnInterruptedReceiveSaysSo()
    {
        var model = new IslandModel();
        model.Handle(new TransferStarted("in-1", false, "air", "MacBook Air", 1, 400));
        model.Handle(Finished("in-1", false, "cancelled"));
        Assert.Equal(new Phase.Interrupted("对方取消了发送"), model.Phase);
    }

    [Fact]
    public void ASendShowsFirstAndTheReceiveAfterIt()
    {
        var (model, _) = Sending();
        model.Handle(new TransferStarted("in-1", false, "air", "MacBook Air", 1, 400));
        model.Handle(new TransferProgress("in-1", false, "air", 200, 400));
        Assert.Equal(new Phase.Sending(), model.Phase);
        Assert.Equal(0, model.Progress);

        model.CancelTransfer();
        Assert.Equal(new Phase.Receiving(), model.Phase);
        Assert.Equal(0.5, model.Progress);
    }

    [Fact]
    public void AReceiveDoesNotInterruptADrag()
    {
        var model = new IslandModel { Transfers = new RecordingTransfers() };
        model.DragStarted(Files);
        model.DragMoved(new Point(280, 3), 560);
        model.Handle(new TransferStarted("in-1", false, "air", "MacBook Air", 1, 400));
        Assert.Equal(new Phase.Target(), model.Phase);
        model.DragEnded();
        Assert.Equal(new Phase.Receiving(), model.Phase);
    }

    [Fact]
    public void APairingRequestIsAnsweredOnTheIsland()
    {
        var model = new IslandModel();
        string? answered = null;
        model.PairAnswered += (id, accept) => answered = $"{id}:{accept}";
        model.ShowPairRequest("p1", "MacBook Air", "482019");
        Assert.Equal(new Phase.PairRequest("p1", "MacBook Air", "482019"), model.Phase);
        Assert.True(model.IsBusy);

        model.AnswerPair(true);
        Assert.Equal("p1:True", answered);
        Assert.Equal(new Phase.PairWaiting("MacBook Air"), model.Phase);
        model.PairFinished("p1", "MacBook Air", ok: true, reason: null);
        Assert.Equal(new Phase.Notice("已与 MacBook Air 配对", Success: true), model.Phase);
    }

    [Fact]
    public void APairingRequestWaitsForTheSendToEnd()
    {
        var (model, _) = Sending();
        model.ShowPairRequest("p1", "MacBook Air", "482019");
        Assert.Equal(new Phase.Sending(), model.Phase);
        model.CancelTransfer();
        Assert.Equal(new Phase.PairRequest("p1", "MacBook Air", "482019"), model.Phase);
    }

    [Fact]
    public void AnExpiredRequestDisappears()
    {
        var model = new IslandModel();
        model.ShowPairRequest("p1", "MacBook Air", "482019");
        model.PairFinished("p1", "MacBook Air", ok: false, reason: "配对已超时");
        Assert.Equal(new Phase.Idle(), model.Phase);
    }

    [Fact]
    public void ADeviceThatNeedsPairingAgainIsRefused()
    {
        var transfers = new RecordingTransfers();
        var model = new IslandModel { Transfers = transfers };
        model.Devices = model.Devices.Select(d => d.Id == "air" ? d with { NeedsRepair = true } : d).ToList();
        model.DragStarted(Files);
        model.DragMoved(new Point(280, 3), 560);
        model.DragMoved(new Point(280, 90), 560);
        Assert.Equal(new Phase.Picker(1), model.Phase);
        Assert.False(model.Drop(Files));
        Assert.Equal(new Phase.Refused("MacBook Air", NeedsRepair: true), model.Phase);
        Assert.Empty(transfers.Sent);
    }

    [Fact]
    public void OnlineChangesUpdateTheDevices()
    {
        var model = new IslandModel();
        model.Handle(new DeviceUpdated("desk", "书房台式机", "desktop", Online: false, Paired: true));
        Assert.False(model.DefaultDevice?.Online);
        Assert.Equal("默认设备离线，移到这里选其他设备", model.HintText);
    }

    [Fact]
    public void TheSendShowsSizesThenTheSpeed()
    {
        var (model, _) = Sending();
        var now = TimeSpan.Zero;
        model.Clock = () => now;
        Assert.Equal("", model.SendDetail);
        model.Handle(new TransferStarted("send-1", true, Desk.Id, Desk.Name, 1, 2_100_000_000));
        model.Handle(new TransferProgress("send-1", true, Desk.Id, 900_000_000, 2_100_000_000));
        Assert.Equal("900 MB / 2.1 GB", model.SendDetail);
        now = TimeSpan.FromSeconds(1);
        model.Handle(new TransferProgress("send-1", true, Desk.Id, 945_000_000, 2_100_000_000));
        Assert.Equal("945 MB / 2.1 GB · 45 MB/s", model.SendDetail);
    }

    [Fact]
    public void SeveralFilesCountInsteadOfSizes()
    {
        var (model, _) = Sending();
        var now = TimeSpan.Zero;
        model.Clock = () => now;
        model.Handle(new TransferStarted("send-1", true, Desk.Id, Desk.Name, 12, 600_000_000));
        model.Handle(new TransferProgress("send-1", true, Desk.Id, 0, 600_000_000, FilesDone: 0));
        now = TimeSpan.FromSeconds(1.5);
        model.Handle(new TransferProgress("send-1", true, Desk.Id, 150_000_000, 600_000_000, FilesDone: 3));
        Assert.Equal("3 / 12 个文件 · 100 MB/s", model.SendDetail);
    }

    [Fact]
    public void TheMomentsThatSound()
    {
        var sounds = new List<Sound>();
        var (model, _) = Sending();
        model.Sounded += sounds.Add;
        model.Handle(Finished("send-1", true, "done"));
        model.Handle(new TransferStarted("in-1", false, "air", "MacBook Air", 1, 400));
        model.Handle(Finished("in-1", false, "done"));
        model.Handle(new TransferStarted("in-2", false, "air", "MacBook Air", 1, 400));
        model.Handle(Finished("in-2", false, "failed"));
        model.Handle(new TransferStarted("in-3", false, "air", "MacBook Air", 1, 400));
        model.Handle(Finished("in-3", false, "cancelled"));
        model.ShowPairRequest("p1", "MacBook Air", "482019");
        Assert.Equal([Sound.Sent, Sound.Received, Sound.Failed, Sound.PairRequest], sounds);

        sounds.Clear();
        var (failing, _) = Sending();
        failing.Sounded += sounds.Add;
        failing.Handle(Finished("send-1", true, "offline"));
        Assert.Equal([Sound.Failed], sounds);
    }

    [Fact]
    public void CoreEventsParse()
    {
        Assert.Equal(new TransferProgress("t", true, "D", 5, 9, 2), CoreEvent.Parse("""{"event":"transfer.progress","id":"t","direction":"send","deviceId":"D","bytes":5,"total":9,"filesDone":2}"""));
        var finished = Assert.IsType<TransferFinished>(CoreEvent.Parse("""{"event":"transfer.finished","id":"t","direction":"receive","deviceId":"D","alias":"Mac","status":"done","filesDone":2,"bytes":9,"savedPaths":["C:\\a","C:\\b"],"error":null}"""));
        Assert.Equal(("t", false, "D", "done", 2, 9L), (finished.Id, finished.Sending, finished.DeviceId, finished.Status, finished.FilesDone, finished.Bytes));
        Assert.Equal(["C:\\a", "C:\\b"], finished.SavedPaths);
        Assert.Null(finished.Error);
        Assert.Equal(new PairCode("p", "D", "Mac", "064054"), CoreEvent.Parse("""{"event":"pair.code","pairId":"p","deviceId":"D","alias":"Mac","code":"064054"}"""));
        Assert.Equal(new DeviceUpdated("D", "Mac", null, true, false), CoreEvent.Parse("""{"event":"device.updated","id":"D","alias":"Mac","deviceType":null,"online":true,"paired":false}"""));
        Assert.Null(CoreEvent.Parse("""{"event":"something.new"}"""));
    }
}
