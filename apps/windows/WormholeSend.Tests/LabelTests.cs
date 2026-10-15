using System.IO;

namespace WormholeSend.Tests;

/// The names and numbers the island shows for a drag and a send.
public sealed class LabelTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("WormholeSendTests").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string Make(string name, bool folder = false)
    {
        var path = Path.Combine(dir, name);
        if (folder) Directory.CreateDirectory(path);
        else File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void ADragIsNamedAfterItsFirstItem()
    {
        var file = Make("Form.docx");
        var other = Make("Photo.heic");
        var folder = Make("Design", folder: true);
        var folder2 = Make("Assets", folder: true);
        Assert.Equal("Form.docx", FileLabel.Of([file]));
        Assert.Equal("Design (folder)", FileLabel.Of([folder]));
        Assert.Equal("Design (folder)", FileLabel.Of([folder + "\\"]));
        Assert.Equal("Form.docx and 1 more file", FileLabel.Of([file, other]));
        Assert.Equal("Design and 1 more folder", FileLabel.Of([folder, folder2]));
        Assert.Equal("Form.docx and 2 more items", FileLabel.Of([file, folder, other]));
    }

    [Theory]
    [InlineData(0, "0 KB")]
    [InlineData(512, "512 bytes")]
    [InlineData(12_345, "12 KB")]
    [InlineData(999_600, "1 MB")]
    [InlineData(45_300_000, "45.3 MB")]
    [InlineData(945_000_000, "945 MB")]
    [InlineData(2_100_000_000, "2.1 GB")]
    [InlineData(2_144_000_000, "2.14 GB")]
    [InlineData(12_500_000_000, "12.5 GB")]
    public void SizesUseDecimalUnitsAsFinder(long bytes, string text) =>
        Assert.Equal(text, TransferStats.Size(bytes));

    [Fact]
    public void TheSpeedCoversTheLastTwoSeconds()
    {
        var stats = new TransferStats(1_000_000_000, files: 1);
        stats.Update(0, 1_000_000_000, 0, TimeSpan.Zero);
        stats.Update(10_000_000, 1_000_000_000, 0, TimeSpan.FromSeconds(0.5));
        Assert.Null(stats.Speed);
        stats.Update(100_000_000, 1_000_000_000, 0, TimeSpan.FromSeconds(1));
        Assert.Equal(100_000_000, stats.Speed);
        // Three seconds on, the first second's slow start no longer counts.
        stats.Update(300_000_000, 1_000_000_000, 0, TimeSpan.FromSeconds(2.5));
        stats.Update(400_000_000, 1_000_000_000, 0, TimeSpan.FromSeconds(3));
        Assert.Equal(150_000_000, stats.Speed);
    }

    [Fact]
    public void PathsAsTextAreFilesWhenTheyAllExist()
    {
        // As VS Code gives them: one per line.
        var file = Make("Form.docx");
        var folder = Make("Design", folder: true);
        Assert.Equal([file, folder], DragPaths.FromText(file + "\r\n" + folder, Path.Exists)!);
        Assert.Equal([file], DragPaths.FromText(new Uri(file).AbsoluteUri, Path.Exists)!);
        Assert.Equal([file], DragPaths.FromText($"\"{file}\"\n", Path.Exists)!);
        Assert.Null(DragPaths.FromText(file + "\r\n" + Path.Combine(dir, "Missing.txt"), Path.Exists));
        Assert.Null(DragPaths.FromText("Form.docx", _ => true));
        Assert.Null(DragPaths.FromText("Hello, world", _ => true));
        Assert.Null(DragPaths.FromText("https://example.com/a.txt", _ => true));
        Assert.Null(DragPaths.FromText(" \r\n", _ => true));
    }
}
