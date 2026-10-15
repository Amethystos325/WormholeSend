using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WormholeSend;

/// The settings window: name, save location, default
/// device, paired devices, start with Windows, language.
public partial class SettingsWindow : FluentWindow
{
    readonly ISession session;
    bool refreshing;

    /// A language, or null for the system's.
    sealed record LanguageItem(UiLanguage? Language, string Name);

    static string NativeName(UiLanguage language) => language switch
    {
        UiLanguage.Zh => "简体中文",
        UiLanguage.Ja => "日本語",
        _ => "English",
    };

    internal SettingsWindow(ISession session)
    {
        this.session = session;
        InitializeComponent();
        Language = L.Xml;
        FontFamily = Fonts.Ui;
        AppIcon.Show(Bar, this);
        SystemThemeWatcher.Watch(this);
        session.DevicesChanged += Refresh;
        session.SettingsChanged += LoadSettings;
        Closed += (_, _) =>
        {
            session.DevicesChanged -= Refresh;
            session.SettingsChanged -= LoadSettings;
        };
        refreshing = true;
        AutostartSwitch.IsChecked = Autostart.Enabled;
        var system = NativeName(L.SystemLanguage);
        LanguageItem[] languages =
        [
            new(null, L.T($"跟随系统（{system}）", $"System ({system})", $"システム（{system}）")),
            .. new[] { UiLanguage.En, UiLanguage.Zh, UiLanguage.Ja }.Select(language => new LanguageItem(language, NativeName(language))),
        ];
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedItem = languages.First(item => item.Language == L.Choice);
        refreshing = false;
        LoadSettings();
        Refresh();
    }

    void LoadSettings()
    {
        if (session.Settings is not { } settings) return;
        AliasBox.Text = settings["alias"]?.GetValue<string>();
        SaveDirText.Text = settings["saveDir"]?.GetValue<string>();
        About.Text = L.T($"端口 {settings["port"]} · {Core.Version}", $"Port {settings["port"]} · {Core.Version}", $"ポート {settings["port"]} · {Core.Version}");
    }

    void Refresh()
    {
        refreshing = true;
        var devices = session.Devices;
        DefaultBox.ItemsSource = devices;
        DefaultBox.SelectedItem = devices.FirstOrDefault(d => d.IsDefault);
        DefaultBox.IsEnabled = devices.Count > 1;
        refreshing = false;

        DeviceList.Children.Clear();
        foreach (var device in devices)
        {
            var unpair = new Wpf.Ui.Controls.Button { Content = L.T("解除配对", "Unpair", "ペアリング解除") };
            unpair.Click += async (_, _) => await Unpair(device);
            var status = device.NeedsRepair ? L.T("需要重新配对", "Needs re-pairing", "再ペアリングが必要")
                : device.Online ? L.T("在线", "Online", "オンライン") : L.T("离线", "Offline", "オフライン");
            if (device.IsDefault) status += L.T(" · 默认", " · Default", " · 既定");
            DeviceList.Children.Add(new CardControl
            {
                Margin = new Thickness(0, 0, 0, 6),
                Icon = new DeviceSymbolIcon(device.Glyph),
                Header = new StackPanel
                {
                    Children =
                    {
                        new Wpf.Ui.Controls.TextBlock { Text = device.Name, FontTypography = FontTypography.BodyStrong },
                        new Wpf.Ui.Controls.TextBlock { Text = status, FontTypography = FontTypography.Caption, Appearance = TextColor.Secondary },
                    },
                },
                Content = unpair,
            });
        }
        NoDevices.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Show(string message, InfoBarSeverity severity)
    {
        Status.Message = message;
        Status.Severity = severity;
        Status.IsOpen = true;
    }

    bool Run(string command, JsonObject arguments, out JsonNode? result)
    {
        try
        {
            result = session.Core?.Command(command, arguments) ?? throw new CoreException(L.T("服务没有在运行", "The services aren't running", "サービスが動いていません"));
            return true;
        }
        catch (CoreException e)
        {
            Show(e.Message, InfoBarSeverity.Error);
            result = null;
            return false;
        }
    }

    void SaveAlias_Click(object sender, RoutedEventArgs e) => SaveAlias();

    void AliasBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveAlias();
    }

    void SaveAlias()
    {
        var alias = AliasBox.Text.Trim();
        if (alias == session.Settings?["alias"]?.GetValue<string>()) return;
        if (Run("settings.set", new JsonObject { ["alias"] = alias }, out var settings))
        {
            session.SettingsSaved(settings);
            Show(L.T("设备名称已保存", "Device name saved", "デバイス名を保存しました"), InfoBarSeverity.Success);
        }
    }

    void OpenSaveDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = SaveDirText.Text;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false });
    }

    void ChangeSaveDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L.T("选择保存位置", "Choose where to save", "保存先を選択"), InitialDirectory = SaveDirText.Text };
        if (dialog.ShowDialog(this) != true) return;
        if (Run("settings.set", new JsonObject { ["saveDir"] = dialog.FolderName }, out var settings))
        {
            session.SettingsSaved(settings);
            Show(L.T("之后收到的文件会存到新位置", "Files you receive from now on go here", "今後受信するファイルはここに保存されます"), InfoBarSeverity.Success);
        }
    }

    void DefaultBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || DefaultBox.SelectedItem is not Device device || device.IsDefault) return;
        if (Run("device.setDefault", new JsonObject { ["deviceId"] = device.Id }, out _)) session.ReloadDevices();
    }

    async Task Unpair(Device device)
    {
        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = L.T("解除配对", "Unpair", "ペアリング解除"),
            Content = L.T($"解除与 {device.Name} 的配对？之后需要重新配对才能互传文件。对方会收到通知。", $"Unpair {device.Name}? You'll need to pair again to send files to each other. {device.Name} will be told.", $"{device.Name} とのペアリングを解除しますか？ 再びファイルをやり取りするには、もう一度ペアリングが必要です。相手にも通知されます。"),
            PrimaryButtonText = L.T("解除配对", "Unpair", "ペアリング解除"),
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = L.T("取消", "Cancel", "キャンセル"),
            Owner = this,
        };
        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        if (Run("device.unpair", new JsonObject { ["deviceId"] = device.Id }, out _)) session.ReloadDevices();
    }

    void PairNew_Click(object sender, RoutedEventArgs e) => session.OpenPairing();

    /// The windows open again in the new language.
    void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || LanguageBox.SelectedItem is not LanguageItem item || item.Language == L.Choice) return;
        session.ChangeLanguage(item.Language);
    }

    void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (refreshing) return;
        try
        {
            Autostart.Enabled = AutostartSwitch.IsChecked == true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        {
            Show(L.T("无法修改开机启动：", "Couldn't change starting with Windows: ", "自動起動を変更できません：") + error.Message, InfoBarSeverity.Error);
        }
    }
}
