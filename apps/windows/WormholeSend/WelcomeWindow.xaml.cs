using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WormholeSend;

/// The first-run guide (design doc 7.3): confirm the name, explain the
/// firewall prompt before the services start (and trigger it), pair the
/// first device. Shown once, before the core has created an identity.
public partial class WelcomeWindow : FluentWindow
{
    readonly ISession session;
    readonly Func<string, Task<string?>> start;
    int page;

    /// `start` starts the services under the given name and returns an
    /// error message, or null.
    internal WelcomeWindow(ISession session, Func<string, Task<string?>> start)
    {
        this.session = session;
        this.start = start;
        InitializeComponent();
        AppIcon.Show(Bar, this);
        SystemThemeWatcher.Watch(this);
        AliasBox.Text = System.Net.Dns.GetHostName();
        session.DevicesChanged += ShowPaired;
        Closed += (_, _) => session.DevicesChanged -= ShowPaired;
    }

    /// Whether the guide was finished rather than closed.
    public bool Finished { get; private set; }

    /// Whether to start with Windows, as chosen on the last page.
    public bool StartWithWindows => AutostartBox.IsChecked == true;

    public string Alias => AliasBox.Text.Trim().Length > 0 ? AliasBox.Text.Trim() : System.Net.Dns.GetHostName();

    async void Next_Click(object sender, RoutedEventArgs e)
    {
        switch (page)
        {
            case 0:
                NamePage.Visibility = Visibility.Collapsed;
                NetworkPage.Visibility = Visibility.Visible;
                page = 1;
                break;
            case 1:
                NextButton.IsEnabled = false;
                NetworkError.Visibility = Visibility.Collapsed;
                if (await start(Alias) is { } error)
                {
                    NetworkError.Text = $"无法启动：{error}";
                    NetworkError.Visibility = Visibility.Visible;
                    NextButton.IsEnabled = true;
                    return;
                }
                NextButton.IsEnabled = true;
                NetworkPage.Visibility = Visibility.Collapsed;
                PairPage.Visibility = Visibility.Visible;
                SkipButton.Visibility = Visibility.Visible;
                NextButton.Content = "完成";
                page = 2;
                ShowPaired();
                break;
            default:
                Finish_Click(sender, e);
                break;
        }
    }

    void ShowPaired()
    {
        var names = session.Devices.Select(d => d.Name).ToList();
        PairedText.Text = names.Count == 0 ? "" : $"已配对：{string.Join("、", names)}";
        SkipButton.Visibility = names.Count == 0 && page == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Pair_Click(object sender, RoutedEventArgs e) => session.OpenPairing();

    void Finish_Click(object sender, RoutedEventArgs e)
    {
        Finished = true;
        Close();
    }
}
