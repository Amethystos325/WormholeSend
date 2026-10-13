using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WormholeSend;

/// The settings window (design doc 7.1): name, save location, default
/// device, paired devices, start with Windows.
public partial class SettingsWindow : FluentWindow
{
    readonly ISession session;
    bool refreshing;

    internal SettingsWindow(ISession session)
    {
        this.session = session;
        InitializeComponent();
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
        refreshing = false;
        LoadSettings();
        Refresh();
    }

    void LoadSettings()
    {
        if (session.Settings is not { } settings) return;
        AliasBox.Text = settings["alias"]?.GetValue<string>();
        SaveDirText.Text = settings["saveDir"]?.GetValue<string>();
        var fingerprint = settings["fingerprint"]?.GetValue<string>() ?? "";
        About.Text = $"本机指纹 {Group(fingerprint)}\n端口 {settings["port"]} · {Core.Version}";
    }

    static string Group(string fingerprint) =>
        string.Join(" ", Enumerable.Range(0, fingerprint.Length / 4).Take(8).Select(i => fingerprint.Substring(i * 4, 4))) + (fingerprint.Length > 32 ? " …" : "");

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
            var unpair = new Wpf.Ui.Controls.Button { Content = "解除配对" };
            unpair.Click += async (_, _) => await Unpair(device);
            var status = device.NeedsRepair ? "需要重新配对" : device.Online ? "在线" : "离线";
            if (device.IsDefault) status += " · 默认";
            DeviceList.Children.Add(new CardControl
            {
                Margin = new Thickness(0, 0, 0, 6),
                Icon = new SymbolIcon { Symbol = device.Glyph == Glyphs.Phone ? SymbolRegular.Phone24 : SymbolRegular.Desktop24 },
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
            result = session.Core?.Command(command, arguments) ?? throw new CoreException("服务没有在运行");
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
            Show("设备名称已保存", InfoBarSeverity.Success);
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
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择保存位置", InitialDirectory = SaveDirText.Text };
        if (dialog.ShowDialog(this) != true) return;
        if (Run("settings.set", new JsonObject { ["saveDir"] = dialog.FolderName }, out var settings))
        {
            session.SettingsSaved(settings);
            Show("之后收到的文件会存到新位置", InfoBarSeverity.Success);
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
            Title = "解除配对",
            Content = $"解除与 {device.Name} 的配对？之后需要重新配对才能互传文件。对方会收到通知。",
            PrimaryButtonText = "解除配对",
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "取消",
            Owner = this,
        };
        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        if (Run("device.unpair", new JsonObject { ["deviceId"] = device.Id }, out _)) session.ReloadDevices();
    }

    void PairNew_Click(object sender, RoutedEventArgs e) => session.OpenPairing();

    void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (refreshing) return;
        try
        {
            Autostart.Enabled = AutostartSwitch.IsChecked == true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        {
            Show("无法修改开机启动：" + error.Message, InfoBarSeverity.Error);
        }
    }
}
