using System.Windows;

namespace WormholeSend.Tests;

/// The state machine of design doc section 2.1, driven the way the windows
/// drive it: drags enter through the sensor at the top center.
public class IslandModelTests
{
    const double PanelWidth = 560;
    static readonly Point Sensor = new(280, 3);
    static readonly string[] Files = [@"C:\Users\Public\季度报告.pdf"];

    static IslandModel Entered(IslandModel? model = null)
    {
        model ??= new IslandModel();
        model.DragStarted(Files);
        model.DragMoved(Sensor, PanelWidth);
        return model;
    }

    [Fact]
    public void EnteringTheSensorTargetsTheDefaultDevice()
    {
        var model = Entered();
        Assert.Equal(new Phase.Target(), model.Phase);
        Assert.True(model.CanDrop);
        Assert.Equal("季度报告.pdf", model.DragLabel);
    }

    [Fact]
    public void DraggingDownIntoTheStripOpensTheDeviceList()
    {
        var model = Entered();
        // Target island: 360 x 116 at y 8, so the strip starts at y 84.
        model.DragMoved(new Point(280, 90), PanelWidth);
        Assert.Equal(new Phase.Picker(1), model.Phase);
    }

    [Fact]
    public void ComingBackUpFromBelowLandsOnTheTargetNotTheList()
    {
        var model = Entered();
        model.DragMoved(new Point(280, 200), PanelWidth);
        Assert.Equal(new Phase.Hint(), model.Phase);
        // The hint capsule reaches y 54, its drop area y 78: the cursor gets
        // there above where the strip of the grown island begins (y 84).
        model.DragMoved(new Point(280, 76), PanelWidth);
        Assert.Equal(new Phase.Target(), model.Phase);
        model.DragMoved(new Point(280, 90), PanelWidth);
        Assert.IsType<Phase.Picker>(model.Phase);
    }

    [Theory]
    [InlineData(90, 0)]
    [InlineData(280, 1)]
    [InlineData(470, 2)]
    // Left of the island, still inside its drop area.
    [InlineData(50, 0)]
    public void TheHotDeviceFollowsTheColumns(double x, int hot)
    {
        var model = Entered();
        model.DragMoved(new Point(280, 90), PanelWidth);
        model.DragMoved(new Point(x, 90), PanelWidth);
        Assert.Equal(new Phase.Picker(hot), model.Phase);
    }

    [Fact]
    public void DroppingOnTheTargetSendsToTheDefaultDevice()
    {
        var model = Entered();
        Assert.True(model.Drop(Files));
        Assert.Equal(new Phase.Sending(), model.Phase);
        Assert.Equal("书房台式机", model.Transfer?.Device.Name);
        Assert.Equal(0, model.Progress);
    }

    [Fact]
    public void DroppingOnAnOfflineDeviceDoesNotSend()
    {
        var model = Entered();
        model.DragMoved(new Point(280, 90), PanelWidth);
        model.DragMoved(new Point(470, 90), PanelWidth);
        Assert.False(model.Drop(Files));
        Assert.Equal(new Phase.Refused("iPad"), model.Phase);
    }

    [Fact]
    public void LeavingTheIslandFallsBackToTheHintUntilTheDragEnds()
    {
        var model = Entered();
        model.DragMoved(new Point(280, 200), PanelWidth);
        Assert.Equal(new Phase.Hint(), model.Phase);
        Assert.False(model.CanDrop);
        model.DragEnded();
        Assert.Equal(new Phase.Idle(), model.Phase);
    }

    [Fact]
    public void LeavingTheWindowFallsBackToTheHint()
    {
        var model = Entered();
        model.DragLeftPanel();
        Assert.Equal(new Phase.Hint(), model.Phase);
        Assert.Equal("移到这里发送到 书房台式机", model.HintText);
    }

    [Fact]
    public void WithoutDevicesNothingIsSent()
    {
        var model = Entered(new IslandModel { Devices = [] });
        Assert.Equal(new Phase.Unavailable("还没有配对设备 · 点击配对"), model.Phase);
        Assert.False(model.CanDrop);
        Assert.False(model.Drop(Files));
    }

    [Fact]
    public void ASingleOfflineDeviceIsUnavailable()
    {
        var model = new IslandModel { Devices = [Device.Samples[0] with { Online = false }] };
        Entered(model);
        Assert.Equal(new Phase.Unavailable("书房台式机 离线，无法发送"), model.Phase);
    }

    [Fact]
    public void ASingleDeviceIsOneLargeTarget()
    {
        var model = Entered(new IslandModel { Devices = [Device.Samples[0]] });
        Assert.Equal(new Phase.Picker(0), model.Phase);
        Assert.True(model.Drop(Files));
        Assert.Equal(new Phase.Sending(), model.Phase);
    }

    [Fact]
    public void AnOfflineDefaultDeviceOpensTheListRightAway()
    {
        var model = new IslandModel();
        model.Devices = model.Devices.Select(d => d.IsDefault ? d with { Online = false } : d).ToList();
        Entered(model);
        // The list, with the device under the cursor already hot.
        Assert.Equal(new Phase.Picker(1), model.Phase);
        Assert.Equal("默认设备离线，移到这里选其他设备", model.HintText);
    }

    [Fact]
    public void ABusyReceiverQueuesTheSend()
    {
        var model = Entered(new IslandModel { NextOutcome = IslandModel.SendOutcome.Busy });
        Assert.True(model.Drop(Files));
        Assert.Equal(new Phase.Queued(1), model.Phase);
        Assert.False(model.QueueCollapsed);
    }

    [Fact]
    public void DragsAreIgnoredWhileSending()
    {
        var model = Entered();
        model.Drop(Files);
        model.DragStarted(Files);
        model.DragMoved(Sensor, PanelWidth);
        Assert.Equal(new Phase.Sending(), model.Phase);
        Assert.False(model.CanDrop);
    }

    [Fact]
    public void CancelReturnsToIdle()
    {
        var model = Entered();
        model.Drop(Files);
        model.CancelTransfer();
        Assert.Equal(new Phase.Idle(), model.Phase);
    }

    [Fact]
    public void BodySizesMatchTheSpec()
    {
        var model = new IslandModel();
        // Capsule sizes: content + 24 high, as in the macOS app.
        Assert.Equal(new Size(160, 30), model.BodySizeFor(new Phase.Idle()));
        Assert.Equal(new Size(260, 46), model.BodySizeFor(new Phase.Hint()));
        Assert.Equal(new Size(360, 116), model.BodySizeFor(new Phase.Target()));
        Assert.Equal(new Size(420, 146), model.BodySizeFor(new Phase.Picker(null)));
        Assert.Equal(new Size(300, 48), model.BodySizeFor(new Phase.Unavailable("")));
        Assert.Equal(new Size(340, 64), model.BodySizeFor(new Phase.Sending()));
        Assert.Equal(new Size(340, 52), model.BodySizeFor(new Phase.Done()));
        Assert.Equal(new Size(384, 84), model.BodySizeFor(new Phase.Receiving()));
        Assert.Equal(new Size(400, 100), model.BodySizeFor(new Phase.Received()));
        model.QueueCollapsed = true;
        Assert.Equal(new Size(150, 30), model.BodySizeFor(new Phase.Queued(1)));
        model.Devices = [Device.Samples[0]];
        Assert.Equal(new Size(380, 88), model.BodySizeFor(new Phase.Picker(0)));
    }

    [Fact]
    public void PhaseChangesAreReported()
    {
        var model = new IslandModel();
        var seen = new List<string>();
        model.PhaseChanged += phase => seen.Add(phase.Describe());
        Entered(model);
        model.DragMoved(new Point(280, 90), PanelWidth);
        Assert.Equal(["hint", "target", "picker(hot: nil)", "picker(hot: 1)"], seen);
    }
}

public class SpringTests
{
    [Fact]
    public void SettlesOnTheTargetWithLittleOvershoot()
    {
        var spring = new Spring(30) { Target = 146 };
        var peak = 30.0;
        var elapsed = 0.0;
        while (spring.Step(1.0 / 60))
        {
            peak = Math.Max(peak, spring.Value);
            elapsed += 1.0 / 60;
            Assert.True(elapsed < 2, "did not settle");
        }
        Assert.Equal(146, spring.Value);
        // A damping ratio of 0.78 overshoots by about 2%.
        Assert.InRange(peak, 146, 146 + 116 * 0.05);
        Assert.InRange(elapsed, 0.4, 1.5);
    }
}
