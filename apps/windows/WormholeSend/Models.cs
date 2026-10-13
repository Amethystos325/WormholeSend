using System.IO;
using System.Text.Json.Nodes;

namespace WormholeSend;

/// A paired device; `Id` is its certificate fingerprint.
sealed record Device(string Id, string Name, string Glyph, bool Online, bool IsDefault, bool NeedsRepair = false)
{
    /// Fake devices for the fake-data mode, demos and tests.
    public static readonly IReadOnlyList<Device> Samples =
    [
        new("desk", "书房台式机", Glyphs.Desktop, Online: true, IsDefault: true),
        new("air", "MacBook Air", Glyphs.Laptop, Online: true, IsDefault: false),
        new("ipad", "iPad", Glyphs.Tablet, Online: false, IsDefault: false),
    ];

    /// An entry of the core's `devices.list`.
    public static Device FromCore(JsonNode node) => new(
        node["fingerprint"]!.GetValue<string>(),
        node["alias"]?.GetValue<string>() ?? "",
        GlyphFor(node["deviceType"]?.GetValue<string>()),
        node["online"]?.GetValue<bool>() ?? false,
        node["isDefault"]?.GetValue<bool>() ?? false,
        node["needsRepair"]?.GetValue<bool>() ?? false);

    public static string GlyphFor(string? deviceType) => deviceType switch
    {
        "mobile" => Glyphs.Phone,
        _ => Glyphs.Desktop,
    };
}

/// A transfer the island shows. `Files` are the dropped files for a send,
/// and the saved files and folders once a receive is done.
sealed record Transfer(Device Device, IReadOnlyList<string> Files, string Label, int FileCount)
{
    public Transfer(Device device, IReadOnlyList<string> files, string label) : this(device, files, label, files.Count) { }
}

/// What a drag carries, named after its first item, as in the macOS app:
/// "季度报告.pdf", "文件夹 设计稿", "季度报告.pdf 等 3 个文件",
/// "设计稿 等 2 个文件夹", "季度报告.pdf 等 4 个文件或文件夹".
static class FileLabel
{
    public static string Of(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return "文件";
        var name = Path.GetFileName(paths[0].TrimEnd('\\', '/'));
        var folders = paths.Count(Directory.Exists);
        if (paths.Count == 1) return folders == 1 ? $"文件夹 {name}" : name;
        var kind = folders == 0 ? "文件" : folders == paths.Count ? "文件夹" : "文件或文件夹";
        return $"{name} 等 {paths.Count} 个{kind}";
    }
}

/// Bytes and files done of a send, and its speed, from the core's progress
/// events (TransferStats in the macOS app).
sealed class TransferStats(long total = 0, int files = 0)
{
    static readonly TimeSpan Window = TimeSpan.FromSeconds(2);
    readonly Queue<(TimeSpan Time, long Bytes)> samples = new();

    public long Bytes { get; private set; }
    public long Total { get; private set; } = total;
    public int FilesDone { get; private set; }
    /// Every file, those inside folders included.
    public int Files { get; } = files;
    /// Bytes per second over the last two seconds, once a second is measured.
    public double? Speed { get; private set; }

    public void Update(long bytes, long total, int filesDone, TimeSpan time)
    {
        Bytes = bytes;
        Total = total;
        FilesDone = filesDone;
        samples.Enqueue((time, bytes));
        while (time - samples.Peek().Time > Window) samples.Dequeue();
        var first = samples.Peek();
        if (time - first.Time >= TimeSpan.FromSeconds(1))
            Speed = (bytes - first.Bytes) / (time - first.Time).TotalSeconds;
    }

    /// "945 MB / 2.1 GB · 45 MB/s" for one file, "3 / 12 个文件 · 45 MB/s" for
    /// several or a folder; empty until the core said what it sends.
    public string Line
    {
        get
        {
            if (Total == 0 && Files == 0) return "";
            var done = Files > 1 ? $"{FilesDone} / {Files} 个文件" : $"{Size(Bytes)} / {Size(Total)}";
            return Speed is { } speed ? $"{done} · {Size((long)speed)}/s" : done;
        }
    }

    static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    /// Decimal units, as Finder: "12 KB", "45.3 MB", "2.14 GB" (at most three
    /// digits; none after the point for KB, one for MB, two above).
    public static string Size(long bytes)
    {
        if (bytes <= 0) return "0 KB";
        if (bytes < 1000) return $"{bytes} 字节";
        double value = bytes / 1000.0;
        var unit = 0;
        while (true)
        {
            var decimals = unit == 0 || value >= 100 ? 0 : value >= 10 || unit == 1 ? 1 : 2;
            var rounded = Math.Round(value, decimals);
            if (rounded < 1000 || unit == Units.Length - 1)
                return rounded.ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), System.Globalization.CultureInfo.InvariantCulture)
                    + " " + Units[unit];
            value /= 1000;
            unit++;
        }
    }
}

/// Files for the simulated receive, so they can be dragged out and revealed.
static class DemoFiles
{
    public static IReadOnlyList<string> Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WormholeSendDemo");
        Directory.CreateDirectory(dir);
        return new[] { "IMG_2041.heic", "IMG_2042.heic", "会议纪要.docx" }
            .Select(name =>
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) File.WriteAllText(path, "Wormhole Send demo file\n");
                return path;
            })
            .ToList();
    }
}
