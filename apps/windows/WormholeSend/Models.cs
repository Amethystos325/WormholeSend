using System.IO;

namespace WormholeSend;

/// A paired device. Fake data for now; the Rust core provides the real list.
sealed record Device(string Id, string Name, string Glyph, bool Online, bool IsDefault)
{
    public static readonly IReadOnlyList<Device> Samples =
    [
        new("desk", "书房台式机", Glyphs.Desktop, Online: true, IsDefault: true),
        new("air", "MacBook Air", Glyphs.Laptop, Online: true, IsDefault: false),
        new("ipad", "iPad", Glyphs.Tablet, Online: false, IsDefault: false),
    ];
}

sealed record Transfer(Device Device, IReadOnlyList<string> Files, string Label)
{
    public int FileCount => Files.Count;
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
