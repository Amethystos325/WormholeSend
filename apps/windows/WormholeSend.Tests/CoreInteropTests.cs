using System.Collections.Concurrent;
using System.IO;
using System.Text.Json.Nodes;

namespace WormholeSend.Tests;

/// The real core through P/Invoke (wormhole.dll is copied with the app):
/// two devices in this process pair and send through `Core` and
/// `CoreTransfers`, as the app does.
public sealed class CoreInteropTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "WormholeSendTests", Guid.NewGuid().ToString("N"));
    readonly List<Core> cores = [];

    sealed class Peer(Core core, BlockingCollection<CoreEvent> events)
    {
        public Core Core { get; } = core;

        public T Wait<T>(Func<T, bool>? match = null) where T : CoreEvent
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (events.TryTake(out var e, TimeSpan.FromMilliseconds(200)) && e is T found && (match?.Invoke(found) ?? true))
                    return found;
            }
            throw new TimeoutException($"no {typeof(T).Name}");
        }
    }

    Peer Start(string name, int port)
    {
        var events = new BlockingCollection<CoreEvent>();
        var config = new JsonObject
        {
            ["home"] = Path.Combine(root, name),
            ["port"] = port,
            ["portFallback"] = true,
            ["alias"] = name,
            ["saveDir"] = Path.Combine(root, name, "received"),
        };
        var (core, settings) = Core.Start(config, events.Add);
        cores.Add(core);
        Assert.Equal(name, settings?["alias"]?.GetValue<string>());
        return new Peer(core, events);
    }

    [Fact]
    public void VersionAndErrors()
    {
        Assert.StartsWith("wormhole-ffi ", Core.Version);
        var a = Start("ErrA", 53520);
        var error = Assert.Throws<CoreException>(() => a.Core.Command("nope"));
        Assert.Equal("未知命令：nope", error.Message);
    }

    [Fact]
    public void PairAndSendThroughTheTransfers()
    {
        var a = Start("WinA", 53500);
        var b = Start("WinB", 53510);

        string? peer = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (peer is null && DateTime.UtcNow < deadline)
        {
            peer = (a.Core.Command("discover.list") as JsonArray)?
                .FirstOrDefault(d => d?["alias"]?.GetValue<string>() == "WinB")?["fingerprint"]?.GetValue<string>();
            if (peer is null) Thread.Sleep(300);
        }
        Assert.NotNull(peer);

        var pairId = a.Core.Command("pair.start", new JsonObject { ["deviceId"] = peer })!.GetValue<string>();
        var codeA = a.Wait<PairCode>(c => c.PairId == pairId);
        var incoming = b.Wait<PairIncoming>();
        var codeB = b.Wait<PairCode>(c => c.PairId == incoming.PairId);
        Assert.Equal(codeA.Code, codeB.Code);
        a.Core.Command("pair.confirm", new JsonObject { ["pairId"] = pairId, ["accept"] = true });
        b.Core.Command("pair.confirm", new JsonObject { ["pairId"] = incoming.PairId, ["accept"] = true });
        Assert.True(a.Wait<PairResult>().Ok);
        Assert.True(b.Wait<PairResult>().Ok);

        var devices = (JsonArray)a.Core.Command("devices.list")!;
        var device = Device.FromCore(devices.Single()!);
        Assert.Equal(("WinB", true), (device.Name, device.IsDefault));

        var file = Path.Combine(root, "季度报告.txt");
        File.WriteAllText(file, "Wormhole");
        var transfers = new CoreTransfers(a.Core);
        var id = transfers.Send(device, [file]);
        Assert.Equal("done", a.Wait<TransferFinished>(f => f.Id == id).Status);
        var received = b.Wait<TransferFinished>(f => !f.Sending);
        Assert.Equal("done", received.Status);
        Assert.Equal("Wormhole", File.ReadAllText(received.SavedPaths.Single()));

        var again = transfers.Retry(id);
        Assert.NotEqual(id, again);
        Assert.Equal("done", a.Wait<TransferFinished>(f => f.Id == again).Status);

        Assert.True(a.Core.Command("device.unpair", new JsonObject { ["deviceId"] = peer })!["wasPaired"]!.GetValue<bool>());
        Assert.Equal("WinA", b.Wait<DeviceUnpaired>().Alias);
    }

    public void Dispose()
    {
        foreach (var core in cores) core.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
