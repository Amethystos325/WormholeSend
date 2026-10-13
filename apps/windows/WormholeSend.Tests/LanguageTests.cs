using System.IO;
using System.Runtime.CompilerServices;

// The interface language is the process's: tests take turns.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WormholeSend.Tests;

static class TestLanguage
{
    /// The tests read the Chinese texts, whatever Windows' language.
    [ModuleInitializer]
    internal static void UseChinese() => L.Use(UiLanguage.Zh);
}

/// The English and Japanese texts that follow rules: labels, counts, sizes.
public sealed class LanguageTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("WormholeSendTests").FullName;

    public void Dispose()
    {
        L.Use(UiLanguage.Zh);
        Directory.Delete(dir, recursive: true);
    }

    string Make(string name, bool folder = false)
    {
        var path = Path.Combine(dir, name);
        if (folder) Directory.CreateDirectory(path);
        else File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void LabelsInEnglishCountTheOthers()
    {
        L.Use(UiLanguage.En);
        var file = Make("Report.docx");
        var other = Make("Photo.heic");
        var third = Make("Notes.txt");
        var folder = Make("Design", folder: true);
        var folder2 = Make("Assets", folder: true);
        Assert.Equal("Report.docx", FileLabel.Of([file]));
        Assert.Equal("Design (folder)", FileLabel.Of([folder]));
        Assert.Equal("Report.docx and 1 more file", FileLabel.Of([file, other]));
        Assert.Equal("Report.docx and 2 more files", FileLabel.Of([file, other, third]));
        Assert.Equal("Design and 1 more folder", FileLabel.Of([folder, folder2]));
        Assert.Equal("Report.docx and 2 more items", FileLabel.Of([file, folder, other]));
        Assert.Equal("1 file", L.Files(1));
        Assert.Equal("3 files", L.Files(3));
        Assert.Equal("512 bytes", TransferStats.Size(512));
    }

    [Fact]
    public void LabelsInJapaneseCountThemAll()
    {
        L.Use(UiLanguage.Ja);
        var file = Make("報告.docx");
        var other = Make("写真.heic");
        var folder = Make("デザイン", folder: true);
        Assert.Equal("フォルダ「デザイン」", FileLabel.Of([folder]));
        Assert.Equal("報告.docx など 2 個のファイル", FileLabel.Of([file, other]));
        Assert.Equal("報告.docx など 3 個の項目", FileLabel.Of([file, folder, other]));
        Assert.Equal("3 個のファイル", L.Files(3));
    }

    [Fact]
    public void TheIslandSpeaksTheLanguage()
    {
        L.Use(UiLanguage.En);
        var model = new IslandModel();
        Assert.Equal("Move here to send to Study PC", model.HintText);
        L.Use(UiLanguage.Ja);
        model.Devices = Device.Samples;
        Assert.Equal("ここに移動して 書斎の PC に送信", model.HintText);
    }
}
