using System.Globalization;
using System.Windows.Markup;
using Microsoft.Win32;

namespace WormholeSend;

/// The languages of the interface.
enum UiLanguage { En, Zh, Ja }

/// The interface language and its texts: `L.T("中文", "English", "日本語")`
/// picks one, written out in all three where it is used, as `tr!` does in
/// the core (which is told the same language). The language follows
/// Windows' display language unless one is chosen in the settings.
static class L
{
    const string Key = @"Software\WormholeSend";
    const string Value = "Language";

    public static UiLanguage Current { get; private set; } = Choice ?? SystemLanguage;

    /// The language chosen in the settings, kept in HKCU; null follows the
    /// system's.
    public static UiLanguage? Choice
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Value) is string code ? Parse(code) : null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (value is { } language)
                key.SetValue(Value, Code(language));
            else
                key.DeleteValue(Value, throwOnMissingValue: false);
            Current = value ?? SystemLanguage;
        }
    }

    /// Windows' display language: Chinese or Japanese, else English.
    public static UiLanguage SystemLanguage => Parse(CultureInfo.CurrentUICulture.Name);

    /// For this run only (the tests).
    public static void Use(UiLanguage language) => Current = language;

    public static string T(string zh, string en, string ja) => Current switch
    {
        UiLanguage.Zh => zh,
        UiLanguage.Ja => ja,
        _ => en,
    };

    /// "zh", "en" or "ja": what the core takes.
    public static string Code(UiLanguage language) => language switch
    {
        UiLanguage.Zh => "zh",
        UiLanguage.Ja => "ja",
        _ => "en",
    };

    static UiLanguage Parse(string tag) =>
        tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? UiLanguage.Zh
        : tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? UiLanguage.Ja
        : UiLanguage.En;

    /// For WPF's choice of a font for Chinese characters, which have
    /// Chinese and Japanese forms: the interface's, or with English the
    /// first of the two among the user's languages (names can be either).
    public static XmlLanguage Xml => XmlLanguage.GetLanguage(Han == UiLanguage.Ja ? "ja-JP" : "zh-CN");

    /// Which forms Chinese characters take.
    public static UiLanguage Han => Current != UiLanguage.En ? Current : UserHan;

    static readonly UiLanguage UserHan = FirstHanLanguage();

    static UiLanguage FirstHanLanguage()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile");
            if (key?.GetValue("Languages") is string[] languages)
                foreach (var tag in languages)
                    if (Parse(tag) is var language && language != UiLanguage.En) return language;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
        return Parse(CultureInfo.InstalledUICulture.Name) is UiLanguage.Ja ? UiLanguage.Ja : UiLanguage.Zh;
    }

    // Counts, with English plurals.

    public static string Files(int count) =>
        T($"{count} 个文件", count == 1 ? "1 file" : $"{count} files", $"{count} 個のファイル");

    public static string Plural(int count, string one, string many) => count == 1 ? one : many;
}
