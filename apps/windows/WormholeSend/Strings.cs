namespace WormholeSend;

/// The texts of the settings, pairing and welcome windows' XAML, as
/// `{x:Static l:S.Name}`: read when a window opens, in the language then.
public static class S
{
    // Settings.
    public static string SettingsTitle => L.T("Wormhole Send 设置", "Wormhole Send settings", "Wormhole Send の設定");
    public static string DeviceName => L.T("设备名称", "Device name", "デバイス名");
    public static string DeviceNameHint => L.T("对方刘海和顶部岛里显示的名字", "Shown to other devices", "ほかのデバイスに表示される名前");
    public static string Save => L.T("保存", "Save", "保存");
    public static string SaveLocation => L.T("保存位置", "Save location", "保存先");
    public static string Open => L.T("打开", "Open", "開く");
    public static string Change => L.T("更改…", "Change…", "変更…");
    public static string DefaultDevice => L.T("默认设备", "Default device", "既定のデバイス");
    public static string DefaultDeviceHint => L.T("拖进顶部岛松手时发给它", "Files released on the island go to it", "アイランドで離したファイルの送信先");
    public static string StartWithWindows => L.T("开机时启动", "Start with Windows", "Windows の起動時に開始");
    public static string Language => L.T("语言", "Language", "言語");
    public static string LanguageHint => L.T("界面和提示用的语言", "Of the app and its messages", "アプリとメッセージの言語");
    public static string PairNew => L.T("配对新设备…", "Pair a new device…", "新しいデバイスとペアリング…");
    public static string PairedDevices => L.T("已配对设备", "Paired devices", "ペアリング済みのデバイス");
    public static string NoPairedDevices => L.T(
        "还没有配对设备。在另一台电脑上打开 Wormhole Send，再点“配对新设备”。",
        "No paired devices yet. Open Wormhole Send on another computer, then click “Pair a new device”.",
        "ペアリングしたデバイスはまだありません。別のパソコンで Wormhole Send を開き、「新しいデバイスとペアリング」をクリックしてください。");

    // Pairing.
    public static string PairTitle => L.T("配对新设备", "Pair a new device", "新しいデバイスとペアリング");
    public static string PairIntro => L.T(
        "在另一台电脑上打开 Wormhole Send，两台电脑要连在同一个网络里。配对一次之后，互传文件不用对方确认。",
        "Open Wormhole Send on the other computer, on the same network. Once paired, files go both ways without asking.",
        "もう一方のパソコンで Wormhole Send を開き、同じネットワークに接続してください。一度ペアリングすれば、確認なしでファイルをやり取りできます。");
    public static string Searching => L.T("正在搜索附近的设备…", "Looking for nearby devices…", "近くのデバイスを探しています…");
    public static string CodeExplained => L.T(
        "对方屏幕上会显示一个 6 位数。两边一致才说明连上的是它，而不是别人冒充的设备。",
        "The other screen shows a 6-digit number. Only if both match are you connected to that device, and not to one pretending to be it.",
        "相手の画面に 6 桁の数字が表示されます。両方が一致すれば、なりすましではなく目的のデバイスにつながっています。");
    public static string Mismatch => L.T("不一致", "They differ", "一致しない");
    public static string MatchPair => L.T("一致，配对", "They match, pair", "一致する、ペアリング");
    public static string PairAnother => L.T("配对其他设备", "Pair another device", "別のデバイスとペアリング");
    public static string Done => L.T("完成", "Done", "完了");

    // Welcome.
    public static string WelcomeTitle => L.T("欢迎使用 Wormhole Send", "Welcome to Wormhole Send", "Wormhole Send へようこそ");
    public static string WelcomeIntro => L.T(
        "把文件拖到屏幕顶部中间，松手就发给你配对过的电脑，对方不用确认。",
        "Drag files to the top center of the screen and release them: they go to a computer you've paired, without asking.",
        "ファイルを画面上部の中央にドラッグして離すと、ペアリングしたパソコンに確認なしで送信できます。");
    public static string ThisComputersName => L.T("这台电脑的名字", "This computer's name", "このパソコンの名前");
    public static string NameHint => L.T("其他设备会看到这个名字，以后可以在设置里改。", "Other devices see this name. You can change it later in the settings.", "ほかのデバイスに表示される名前です。あとで設定から変更できます。");
    public static string AllowNetwork => L.T("允许网络访问", "Allow network access", "ネットワークへのアクセスを許可");
    public static string LocalOnly => L.T(
        "Wormhole Send 只在局域网里直接传文件，不经过任何服务器。",
        "Wormhole Send sends files directly over your local network, through no server.",
        "Wormhole Send はローカル ネットワーク内で直接ファイルを送り、サーバーを経由しません。");
    public static string FirewallHint => L.T(
        "点“下一步”后，Windows 可能会问是否允许它通过防火墙。请勾选“专用网络”并点“允许”，否则其他设备找不到这台电脑。",
        "After you click Next, Windows may ask whether to let it through the firewall. Check “Private networks” and click “Allow”, or other devices won't find this computer.",
        "「次へ」をクリックすると、ファイアウォールの許可を求められることがあります。「プライベート ネットワーク」にチェックを入れて「許可」をクリックしてください。許可しないと、ほかのデバイスからこのパソコンが見つかりません。");
    public static string FirstDevice => L.T("配对第一台设备", "Pair your first device", "最初のデバイスとペアリング");
    public static string FirstDeviceIntro => L.T(
        "在另一台电脑上也打开 Wormhole Send，然后在这里配对。只需要一次，之后互传都不用确认。",
        "Open Wormhole Send on another computer too, and pair here. Once is enough: after that, files go both ways without asking.",
        "別のパソコンでも Wormhole Send を開いて、ここでペアリングしてください。一度だけで、その後は確認なしでファイルをやり取りできます。");
    public static string StartAppWithWindows => L.T("开机时启动 Wormhole Send", "Start Wormhole Send with Windows", "Windows の起動時に Wormhole Send を開始");
    public static string Skip => L.T("跳过", "Skip", "スキップ");
    public static string Next => L.T("下一步", "Next", "次へ");
}
