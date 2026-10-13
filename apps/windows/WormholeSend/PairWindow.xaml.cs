using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WormholeSend;

/// Pairing with a nearby device (design doc 3.1): pick it, compare the
/// 6-digit code both screens show, confirm on both sides.
public partial class PairWindow : FluentWindow
{
    readonly ISession session;
    readonly DispatcherTimer search = new() { Interval = TimeSpan.FromSeconds(1.5) };
    /// The cards in the list, by fingerprint.
    readonly Dictionary<string, CardControl> cards = [];
    string? pairId;
    string alias = "";

    internal PairWindow(ISession session)
    {
        this.session = session;
        InitializeComponent();
        Language = L.Xml;
        FontFamily = Fonts.Ui;
        AppIcon.Show(Bar, this);
        SystemThemeWatcher.Watch(this);
        session.PairingEvent += OnPairing;
        search.Tick += (_, _) => Search();
        Closed += (_, _) =>
        {
            search.Stop();
            session.PairingEvent -= OnPairing;
            // Closing mid-way declines, so the other side doesn't wait.
            if (pairId is { } id) Command("pair.confirm", new JsonObject { ["pairId"] = id, ["accept"] = false });
        };
        ShowSearch();
    }

    void ShowSearch()
    {
        pairId = null;
        SearchPanel.Visibility = Visibility.Visible;
        CodePanel.Visibility = ResultPanel.Visibility = Visibility.Collapsed;
        Search();
        search.Start();
    }

    /// Asks for the nearby devices again (the core also announces this one;
    /// answers trickle in). Cards stay in place, so a click isn't lost.
    void Search()
    {
        if (Command("discover.list") is not JsonArray nearby) return;
        var devices = nearby.OfType<JsonNode>().Select(Device.FromCore).ToList();
        foreach (var gone in cards.Keys.Except(devices.Select(d => d.Id)).ToList())
        {
            NearbyList.Children.Remove(cards[gone]);
            cards.Remove(gone);
        }
        foreach (var device in devices.Where(d => !cards.ContainsKey(d.Id)))
        {
            var pair = new Wpf.Ui.Controls.Button { Content = L.T("配对", "Pair", "ペアリング"), Appearance = ControlAppearance.Primary };
            pair.Click += (_, _) => Start(device);
            var card = new CardControl
            {
                Margin = new Thickness(0, 0, 0, 6),
                Icon = new SymbolIcon { Symbol = device.Glyph == Glyphs.Phone ? SymbolRegular.Phone24 : SymbolRegular.Desktop24 },
                Header = new Wpf.Ui.Controls.TextBlock { Text = device.Name, FontTypography = FontTypography.BodyStrong },
                Content = pair,
            };
            cards[device.Id] = card;
            NearbyList.Children.Add(card);
        }
    }

    void Start(Device device)
    {
        search.Stop();
        alias = device.Name;
        ShowWaiting(L.T($"正在连接 {alias}…", $"Connecting to {alias}…", $"{alias} に接続しています…"));
        if (Command("pair.start", new JsonObject { ["deviceId"] = device.Id }) is JsonValue id)
            pairId = id.GetValue<string>();
        else
            ShowResult(false, L.T("无法开始配对", "Couldn't start pairing", "ペアリングを開始できません"), lastError ?? "");
    }

    void OnPairing(CoreEvent e)
    {
        switch (e)
        {
            case PairCode code when code.PairId == pairId:
                CodeTitle.Text = L.T($"核对 {code.Alias} 上的数字", $"Check the number on {code.Alias}", $"{code.Alias} に表示された数字を確認");
                CodeText.Text = code.Code.Length == 6 ? $"{code.Code[..3]} {code.Code[3..]}" : code.Code;
                AcceptButton.IsEnabled = true;
                SearchPanel.Visibility = ResultPanel.Visibility = Visibility.Collapsed;
                CodePanel.Visibility = Visibility.Visible;
                break;
            case PairResult result when result.PairId == pairId:
                pairId = null;
                if (result.Ok)
                    ShowResult(true, L.T($"已与 {result.Alias} 配对", $"Paired with {result.Alias}", $"{result.Alias} とペアリングしました"),
                    L.T("之后把文件拖到屏幕顶部中间，松手就发过去，对方不用确认。", "From now on, drag files to the top center of the screen and release them: they go over without asking.", "これからは、ファイルを画面上部の中央にドラッグして離すだけで、確認なしで送信できます。"));
                else
                    ShowResult(false, L.T("配对没有完成", "Pairing didn't finish", "ペアリングが完了しませんでした"), result.Reason ?? "");
                break;
        }
    }

    void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (pairId is not { } id) return;
        AcceptButton.IsEnabled = false;
        Command("pair.confirm", new JsonObject { ["pairId"] = id, ["accept"] = true });
        if (lastError is null)
            ShowWaiting(L.T($"等待 {alias} 确认…", $"Waiting for {alias} to confirm…", $"{alias} の確認を待っています…"));
        else
            ShowResult(false, L.T("配对没有完成", "Pairing didn't finish", "ペアリングが完了しませんでした"), lastError);
    }

    void Decline_Click(object sender, RoutedEventArgs e)
    {
        if (pairId is { } id) Command("pair.confirm", new JsonObject { ["pairId"] = id, ["accept"] = false });
        pairId = null;
        ShowResult(false, L.T("已取消配对", "Pairing cancelled", "ペアリングを中止しました"),
            L.T("数字不一致时，可能有别的设备在冒充对方。", "When the numbers differ, another device may be pretending to be it.", "数字が一致しない場合は、別のデバイスがなりすましている可能性があります。"));
    }

    void ShowWaiting(string text)
    {
        SearchPanel.Visibility = CodePanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        ResultRing.Visibility = Visibility.Visible;
        ResultIcon.Visibility = AgainButton.Visibility = DoneButton.Visibility = Visibility.Collapsed;
        ResultText.Text = text;
        ResultDetail.Text = "";
    }

    void ShowResult(bool ok, string text, string detail)
    {
        SearchPanel.Visibility = CodePanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        ResultRing.Visibility = Visibility.Collapsed;
        ResultIcon.Visibility = AgainButton.Visibility = DoneButton.Visibility = Visibility.Visible;
        ResultIcon.Symbol = ok ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.ErrorCircle24;
        ResultIcon.Filled = true;
        ResultText.Text = text;
        ResultDetail.Text = detail;
    }

    void Again_Click(object sender, RoutedEventArgs e) => ShowSearch();

    void Done_Click(object sender, RoutedEventArgs e) => Close();

    string? lastError;

    JsonNode? Command(string name, JsonObject? arguments = null)
    {
        lastError = null;
        try
        {
            return session.Core?.Command(name, arguments) ?? throw new CoreException(L.T("服务没有在运行", "The services aren't running", "サービスが動いていません"));
        }
        catch (CoreException e)
        {
            lastError = e.Message;
            return null;
        }
    }
}
