using System.Windows;

namespace WormholeSend.Tests;

/// LocalSend devices: they can't pair, so they show after the paired ones,
/// their users accept every send, and their requests are asked on the island.
public class LocalSendTests
{
    static readonly string[] Files = [@"C:\Users\Public\季度报告.pdf"];
    static readonly Point Sensor = new(280, 3);
    static readonly Device Pixel = Device.LocalSendSamples[0];

    static IslandModel Model(IReadOnlyList<Device> paired, RecordingTransfers? transfers = null) => new()
    {
        Devices = paired,
        LocalSendDevices = [Pixel],
        Transfers = transfers ?? new RecordingTransfers(),
    };

    static ReceiveRequest Request(string id = "r1", string? text = null) =>
        new(id, "pixel", "Pixel 8", "mobile", text is null ? "IMG_0001.jpg 等 3 个文件" : "一段文字", 3, 8_400_000, text);

    [Fact]
    public void WithoutPairedDevicesADragGoesToTheLocalSendDevice()
    {
        var transfers = new RecordingTransfers();
        var model = Model([], transfers);
        Assert.Equal("移到这里选择设备", model.HintText);
        model.DragStarted(Files);
        model.DragMoved(Sensor, 560);
        Assert.Equal(new Phase.Picker(0), model.Phase);
        Assert.True(model.Drop(Files));
        Assert.Equal(Pixel, transfers.Sent.Single().Device);
    }

    [Fact]
    public void LocalSendDevicesFollowThePairedOnesInTheList()
    {
        var transfers = new RecordingTransfers();
        var model = Model(Device.Samples, transfers);
        Assert.Equal(["desk", "air", "ipad", "pixel"], model.Targets.Select(d => d.Id));
        model.DragStarted(Files);
        model.DragMoved(Sensor, 560);
        // The default device is still a paired one.
        Assert.Equal(new Phase.Target(), model.Phase);
        model.DragMoved(new Point(280, 90), 560);
        model.DragMoved(new Point(440, 90), 560);
        Assert.Equal(new Phase.Picker(3), model.Phase);
        Assert.True(model.Drop(Files));
        Assert.Equal(Pixel, transfers.Sent.Single().Device);
    }

    [Fact]
    public void LocalSendDevicesComeAndGo()
    {
        var model = new IslandModel { Devices = [] };
        model.Handle(new DeviceUpdated("pixel", "Pixel 8", "mobile", Online: true, Paired: false, LocalSend: true));
        Assert.Equal(["Pixel 8"], model.LocalSendDevices.Select(d => d.Name));
        Assert.True(model.LocalSendDevices[0].LocalSend);
        model.Handle(new DeviceUpdated("pixel", "Pixel 8", "mobile", Online: false, Paired: false, LocalSend: true));
        Assert.Empty(model.LocalSendDevices);
    }

    [Fact]
    public void ASendWaitsForTheOtherUserThenRunsOrIsDeclined()
    {
        var model = Model([]);
        model.DragStarted(Files);
        model.DragMoved(Sensor, 560);
        model.Drop(Files);
        model.Handle(new TransferWaiting("send-1", "pixel", "Pixel 8"));
        Assert.Equal(new Phase.AwaitingAcceptance("Pixel 8"), model.Phase);
        Assert.True(model.IsBusy);
        model.Handle(new TransferStarted("send-1", true, "pixel", "Pixel 8", 1, 100));
        Assert.Equal(new Phase.Sending(), model.Phase);

        model.Retry();
        model.Handle(new TransferWaiting("send-2", "pixel", "Pixel 8"));
        model.Handle(new TransferFinished("send-2", true, "pixel", "Pixel 8", "declined", 0, 0, [], "Pixel 8 拒绝了接收"));
        Assert.Equal(new Phase.Failed("Pixel 8 拒绝了接收"), model.Phase);
    }

    [Fact]
    public void WaitingCanBeCancelled()
    {
        var transfers = new RecordingTransfers();
        var model = Model([], transfers);
        model.DragStarted(Files);
        model.DragMoved(Sensor, 560);
        model.Drop(Files);
        model.Handle(new TransferWaiting("send-1", "pixel", "Pixel 8"));
        model.CancelTransfer();
        Assert.Equal(["send-1"], transfers.Cancelled);
        Assert.Equal(new Phase.Idle(), model.Phase);
    }

    [Fact]
    public void AcceptingFilesLetsThemCome()
    {
        var model = Model([]);
        var sounds = new List<Sound>();
        var answers = new List<(string, bool)>();
        model.Sounded += sounds.Add;
        model.ReceiveAnswered += (id, accept) => answers.Add((id, accept));
        model.Handle(Request());
        Assert.Equal(new Phase.ReceiveRequest("r1", "Pixel 8", "IMG_0001.jpg 等 3 个文件", 8_400_000, null), model.Phase);
        Assert.Equal([Sound.PairRequest], sounds);
        Assert.True(model.IsBusy);
        model.AnswerReceive(true);
        Assert.Equal([("r1", true)], answers);
        Assert.Equal(new Phase.Idle(), model.Phase);
        model.Handle(new TransferStarted("in-1", false, "pixel", "Pixel 8", 3, 8_400_000));
        Assert.Equal(new Phase.Receiving(), model.Phase);
    }

    [Fact]
    public void AcceptingATextCopiesIt()
    {
        var copied = new List<string>();
        var model = Model([]);
        model.CopyText = text =>
        {
            copied.Add(text);
            return true;
        };
        const string text = "明天下午三点在 3 楼会议室开评审会";
        model.Handle(Request(text: text));
        model.AnswerReceive(true);
        Assert.Equal([text], copied);
        Assert.Equal(new Phase.Notice("已复制到剪贴板", true, "明天下午三点在 3 楼…"), model.Phase);
    }

    [Fact]
    public void DecliningATextCopiesNothing()
    {
        var copied = new List<string>();
        var model = Model([]);
        model.CopyText = text =>
        {
            copied.Add(text);
            return true;
        };
        model.Handle(Request(text: "你好"));
        model.AnswerReceive(false);
        Assert.Empty(copied);
        Assert.Equal(new Phase.Idle(), model.Phase);
    }

    [Fact]
    public void ARequestWaitsForADragAndGoesWhenWithdrawn()
    {
        var model = Model(Device.Samples);
        model.DragStarted(Files);
        model.Handle(Request("r1"));
        Assert.Equal(new Phase.Hint(), model.Phase);
        model.DragEnded();
        Assert.Equal(new Phase.ReceiveRequest("r1", "Pixel 8", "IMG_0001.jpg 等 3 个文件", 8_400_000, null), model.Phase);
        model.Handle(new ReceiveClosed("r1", "timeout"));
        Assert.Equal(new Phase.Idle(), model.Phase);

        // Withdrawn before it could show: it never does.
        model.DragStarted(Files);
        model.Handle(Request("r2"));
        model.Handle(new ReceiveClosed("r2", "cancelled"));
        model.DragEnded();
        Assert.Equal(new Phase.Idle(), model.Phase);
    }

    [Fact]
    public void TheDeclineButtonCountsDownTheMinute()
    {
        var now = TimeSpan.FromSeconds(100);
        var model = Model(Device.Samples);
        model.Clock = () => now;
        model.DragStarted(Files);
        model.Handle(Request("r1"));
        // Hidden behind the drag, the minute runs all the same.
        now += TimeSpan.FromSeconds(12.5);
        model.DragEnded();
        Assert.IsType<Phase.ReceiveRequest>(model.Phase);
        Assert.Equal(48, model.SecondsLeft("r1"));
        now += TimeSpan.FromSeconds(60);
        Assert.Equal(0, model.SecondsLeft("r1"));
        model.Handle(new ReceiveClosed("r1", "timeout"));
        Assert.Null(model.SecondsLeft("r1"));
    }

    [Fact]
    public void ASenderGivingUpIsSaid()
    {
        var model = Model([]);
        model.Handle(Request("r1"));
        model.Handle(new ReceiveClosed("r1", "cancelled"));
        Assert.Equal(new Phase.Notice("Pixel 8 取消了发送", Success: false), model.Phase);
    }

    [Fact]
    public void AShortTextTakesOneLine()
    {
        var model = Model([]);
        Assert.Equal(new Size(380, 102), model.BodySizeFor(new Phase.ReceiveRequest("r", "Pixel 8", "一段文字", 0, "你好")));
        Assert.Equal(new Size(380, 116), model.BodySizeFor(new Phase.ReceiveRequest("r", "Pixel 8", "一段文字", 0, new string('长', 60))));
        Assert.Equal(new Size(380, 116), model.BodySizeFor(new Phase.ReceiveRequest("r", "Pixel 8", "一段文字", 0, "两行\n文字")));
    }

    [Fact]
    public void LocalSendEventsParse()
    {
        Assert.Equal(new DeviceUpdated("D", "Pixel", "mobile", true, false, LocalSend: true),
            CoreEvent.Parse("""{"event":"device.updated","id":"D","alias":"Pixel","deviceType":"mobile","kind":"localsend","online":true,"paired":false}"""));
        Assert.Equal(new TransferWaiting("t", "D", "Pixel"),
            CoreEvent.Parse("""{"event":"transfer.waiting","id":"t","deviceId":"D","alias":"Pixel"}"""));
        Assert.Equal(new ReceiveRequest("r", "D", "Pixel", "mobile", "一段文字", 1, 0, "你好"),
            CoreEvent.Parse("""{"event":"receive.request","requestId":"r","deviceId":"D","alias":"Pixel","deviceType":"mobile","label":"一段文字","files":1,"total":0,"text":"你好"}"""));
        Assert.Equal(new ReceiveClosed("r", "timeout"),
            CoreEvent.Parse("""{"event":"receive.closed","requestId":"r","reason":"timeout"}"""));
        var device = Device.FromCore(System.Text.Json.Nodes.JsonNode.Parse(
            """{"fingerprint":"D","alias":"Pixel","deviceType":"mobile","kind":"localsend","online":true,"paired":false}""")!);
        Assert.True(device.LocalSend);
        Assert.Equal(Glyphs.Phone, device.Glyph);
    }
}
