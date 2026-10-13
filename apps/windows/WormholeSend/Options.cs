using System.IO;
using System.Text;
using System.Windows.Threading;

namespace WormholeSend;

/// Command line:
///
///     WormholeSend [--home DIR] [--port N] [--log]
///     WormholeSend --fake [--log]
///     WormholeSend --demo picker [--screen external|N] [--devices 1] [--log] [--snapshot out.png]
///     WormholeSend --selftest --log
///     WormholeSend --fake --menu [--snapshot out.png]
///
/// `--home` and `--port` run a second device on the same PC, as with `whs`.
/// `--fake` uses simulated devices and transfers, switched from the tray
/// menu, without the core. `--demo` freezes one phase, as in the macOS
/// app: hint, target, picker, picker-offline, single, unavailable, sending,
/// queued, queued-compact, done, failed, refused, receiving, received,
/// pair-request, notice; and for LocalSend devices receive-request,
/// receive-text, copied, awaiting, picker-localsend. `--snapshot` captures the island's window from the
/// screen once the animation settled, then quits. `--selftest` runs the
/// drag checks in SelfTest.cs and exits with their result. `--menu` opens
/// the tray menu with a submenu at the screen center, for a look.
sealed record Options(
    string? Demo, string? Screen, int? Devices, bool Log, string? Snapshot, bool SelfTest,
    bool FakeData, string? Home, int? Port, bool Menu)
{
    /// Simulated devices and transfers instead of the core.
    public bool Fake => FakeData || Demo is not null || SelfTest;

    public static Options Parse(string[] args)
    {
        string? Value(string flag)
        {
            var index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        return new Options(
            Value("--demo"),
            Value("--screen"),
            int.TryParse(Value("--devices"), out var count) ? count : null,
            args.Contains("--log"),
            Value("--snapshot") is { } path ? Path.GetFullPath(path) : null,
            args.Contains("--selftest"),
            args.Contains("--fake"),
            Value("--home") is { } home ? Path.GetFullPath(home) : null,
            int.TryParse(Value("--port"), out var port) ? port : null,
            args.Contains("--menu"));
    }
}

static class Log
{
    static TextWriter? writer;

    /// `--log` prints every phase change and core event to stderr. A GUI
    /// app gets no console of its own; use the parent's when stderr isn't
    /// redirected.
    public static void Enable()
    {
        var handle = Native.GetStdHandle(Native.STD_ERROR_HANDLE);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
        writer = TextWriter.Synchronized(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
    }

    public static void Write(string line) => writer?.WriteLine(line);
}

static class Demo
{
    /// Freezes the island in one phase, as `--demo` in the macOS app.
    public static void Apply(Options options, IslandModel model, AppController app)
    {
        if (options.Devices is { } count) model.Devices = Device.Samples.Take(count).ToList();
        if (options.Demo is not { } name) return;
        app.ActivateScreen(options.Screen);
        var files = new[] { @"C:\Users\Public\Documents\季度报告.pdf" };
        var sample = new Transfer(Device.Samples[0], files, "季度报告.pdf");

        // Let the island grow out of its idle size, as it would for real.
        After(300, () =>
        {
            model.DragLabel = "季度报告.pdf";
            switch (name)
            {
                case "hint": model.Phase = new Phase.Hint(); break;
                case "target": model.Phase = new Phase.Target(); break;
                case "picker": model.Phase = new Phase.Picker(1); break;
                case "picker-offline": model.Phase = new Phase.Picker(2); break;
                case "single":
                    model.Devices = [Device.Samples[0]];
                    model.Phase = new Phase.Picker(0);
                    break;
                case "unavailable": model.Phase = new Phase.Unavailable("没有在线的设备"); break;
                case "sending":
                    model.Transfer = sample;
                    model.Progress = 0.45;
                    model.Phase = new Phase.Sending();
                    // Two progress reports a second apart, so the speed shows.
                    var stats = new TransferStats(2_100_000_000, files: 1);
                    stats.Update(900_000_000, 2_100_000_000, 0, TimeSpan.Zero);
                    stats.Update(945_000_000, 2_100_000_000, 0, TimeSpan.FromSeconds(1));
                    model.ShowSendStats(stats);
                    break;
                case "queued":
                    model.Transfer = sample;
                    model.Phase = new Phase.Queued(1);
                    break;
                case "queued-compact":
                    model.Transfer = sample;
                    model.QueueCollapsed = true;
                    model.Phase = new Phase.Queued(1);
                    break;
                case "done":
                    model.Transfer = sample;
                    model.Phase = new Phase.Done();
                    break;
                case "failed":
                    model.Transfer = sample;
                    model.Phase = new Phase.Failed("网络中断");
                    break;
                case "refused": model.Phase = new Phase.Refused("iPad"); break;
                case "receiving":
                case "received":
                    var received = DemoFiles.Make();
                    model.Transfer = new Transfer(Device.Samples[1], received, FileLabel.Of(received));
                    model.Progress = name == "receiving" ? 0.62 : 1;
                    model.WaitingCount = name == "receiving" ? 1 : 0;
                    model.Phase = name == "receiving" ? new Phase.Receiving() : new Phase.Received();
                    break;
                case "pair-request": model.Phase = new Phase.PairRequest("demo", "MacBook Air", "482019"); break;
                // As the core reports them, so the decline button counts down.
                case "receive-request":
                    model.Handle(new ReceiveRequest("demo", "pixel", "Pixel 8", "mobile", "IMG_0001.jpg 等 3 个文件", 3, 8_400_000, null));
                    break;
                case "receive-text":
                    model.Handle(new ReceiveRequest("demo", "pixel", "Pixel 8", "mobile", "一段文字", 1, 0, SampleText));
                    break;
                case "copied": model.Phase = new Phase.Notice("已复制到剪贴板", true, IslandModel.Preview(SampleText)); break;
                case "awaiting": model.Phase = new Phase.AwaitingAcceptance("Pixel 8"); break;
                case "picker-localsend":
                    model.LocalSendDevices = Device.LocalSendSamples;
                    model.Phase = new Phase.Picker(3);
                    break;
                case "notice": model.Phase = new Phase.Notice("已与 MacBook Air 配对", Success: true); break;
                default:
                    Log.Write($"Unknown demo phase: {name}");
                    break;
            }
        });
        if (options.Snapshot is { } path) After(1800, () => app.Snapshot(path));
    }

    /// A text message from a LocalSend device, for the demos and `--fake`.
    public const string SampleText = "明天下午三点在 3 楼会议室开评审会，记得带上投影转接头和上周的评审记录，会后一起把发布日期定下来。";

    static void After(int milliseconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}
