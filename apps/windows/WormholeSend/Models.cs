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

static class FileLabel
{
    /// "季度报告.pdf" or "3 个文件".
    public static string Of(IReadOnlyList<string> paths) => paths.Count switch
    {
        0 => "文件",
        1 => Path.GetFileName(paths[0].TrimEnd('\\', '/')),
        _ => $"{paths.Count} 个文件",
    };
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
