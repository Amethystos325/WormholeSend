import AppKit

@main
struct WormholeSendApp {
    static func main() {
        MainActor.assumeIsolated {
            let app = NSApplication.shared
            let controller = AppController()
            app.delegate = controller
            app.setActivationPolicy(.accessory)
            app.run()
        }
    }
}

/// Owns the core, the panel, the drag monitor and the menu bar item.
///
/// Launch arguments: `--home <dir>` and `--port <n>` (another data directory
/// and port, e.g. to test against `whs` on the same Mac), `--demo <phase>`
/// (sample data, no core), `--log` (state changes and core events on stderr).
@MainActor
final class AppController: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let model = IslandModel()
    private lazy var panel = NotchPanel(model: model)
    private let dragMonitor = DragMonitor()
    private var statusItem: NSStatusItem?
    private var returnHome: Task<Void, Never>?
    private var core: Core?
    private var settings: Settings?
    private var coreError: String?
    /// Pairings started from the menu: their code shows in a dialog. Incoming
    /// requests show on the island instead (`IslandModel`).
    private var startedPairs: Set<String> = []
    /// The pairing whose code dialog is open, so it can be closed when the
    /// pairing ends elsewhere (declined, expired).
    private var openPairing: String?
    /// Local Network access is denied (`LocalNetwork`): the menu says so.
    private var networkDenied = false
    private var menuIsOpen = false
    /// Refreshes the open "配对新设备" submenu; what it shows now.
    private var nearbyRefresh: Task<Void, Never>?
    private var shownNearby: [String] = []

    func applicationDidFinishLaunching(_ notification: Notification) {
        place(on: homeScreen)
        panel.ignoresMouseEvents = true
        panel.orderFrontRegardless()

        model.onPhaseChange = { [weak self] phase in self?.phaseChanged(phase) }
        dragMonitor.onStart = { [weak self] urls, location in self?.dragStarted(urls, at: location) }
        dragMonitor.onEnd = { [weak self] in self?.model.dragEnded() }
        dragMonitor.start()

        NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.model.phase == .idle else { return }
                self.place(on: self.homeScreen)
            }
        }

        setUpStatusItem()
        if Arguments.value(of: "--demo") != nil {
            model.devices = Device.samples
            Demo.apply(to: model) { [weak self] screen in self?.place(on: screen) }
        } else {
            welcomeIfNeeded()
            startCore()
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        core?.stop()
    }

    // MARK: - Core

    private func startCore() {
        // The core's messages in the app's language.
        var config: [String: Any] = ["portFallback": false, "language": Language.current.rawValue]
        if let home = Arguments.value(of: "--home") { config["home"] = home }
        if let port = Arguments.value(of: "--port").flatMap(Int.init) { config["port"] = port }
        let json = (try? JSONSerialization.data(withJSONObject: config)).map { String(decoding: $0, as: UTF8.self) } ?? "{}"
        Task { [weak self] in
            do {
                let core = try await Core.start(configJSON: json) { [weak self] event in self?.handle(event) }
                guard let self else { return core.stop() }
                self.core = core
                model.core = core
                if case .success(let settings) = core.call("settings.get", as: Settings.self) {
                    self.settings = settings
                }
                model.reloadDevices()
                log("core \(Core.version) started, language \(Language.current.rawValue)")
                checkLocalNetwork()
            } catch {
                self?.coreError = "\(error)"
                self?.log("core failed: \(error)")
            }
        }
    }

    private func handle(_ event: CoreEvent) {
        log(describe(event))
        model.handle(event)
        guard let pairId = event.pairId, startedPairs.contains(pairId) else { return }
        switch event.event {
        case "pair.code":
            if let code = event.code {
                askToConfirm(pairId: pairId, alias: event.alias ?? tr("对方", "the other device", "相手"), code: code)
            }
        case "pair.result":
            startedPairs.remove(pairId)
            if pairId == openPairing {
                // The dialog is still open: the other side declined or it expired.
                NSApp.stopModal(withCode: .abort)
            }
            let alias = event.alias ?? tr("对方", "the other device", "相手")
            if event.ok == true {
                inform(
                    tr("已与 \(alias) 配对", "Paired with \(alias)", "\(alias) とペアリングしました"),
                    tr("之后互传文件不用确认。", "From now on, files go back and forth without asking.", "今後、ファイルのやり取りに確認は不要です。"))
            } else {
                inform(Self.pairingFailed, event.reason ?? tr("未知原因", "Unknown reason", "原因不明"))
            }
        default:
            break
        }
    }

    private func describe(_ event: CoreEvent) -> String {
        var parts = ["event", event.event]
        if let id = event.id ?? event.pairId ?? event.requestId { parts.append(id) }
        if let kind = event.kind { parts.append(kind) }
        if let count = event.count { parts.append("count \(count)") }
        if let position = event.position { parts.append("position \(position)") }
        if let status = event.status { parts.append(status) }
        if let reason = event.reason { parts.append(reason) }
        return parts.joined(separator: " ")
    }

    // MARK: - First launch and Local Network access

    /// Says what the app does and, before the core's first announcement
    /// makes macOS ask, why it needs Local Network access.
    private func welcomeIfNeeded() {
        let key = "welcomed"
        guard !UserDefaults.standard.bool(forKey: key) else { return }
        UserDefaults.standard.set(true, forKey: key)
        var text = [tr(
            "把文件拖到屏幕顶部中间，松手就发给配对过的电脑，对方不用确认。",
            "Drag files to the top center of the screen and let go: they go straight to a paired computer, with no confirming on the other side.",
            "ファイルを画面上部の中央にドラッグして離すと、ペアリング済みのコンピュータにそのまま届きます。相手側での確認は不要です。")]
        if LocalNetwork.isAsked {
            text.append(tr(
                "接下来 macOS 会问是否允许 Wormhole Send 查找本地网络上的设备，请点“允许”。不允许的话，其他电脑找不到这台 Mac，这台 Mac 也发不出文件。",
                "Next, macOS asks whether Wormhole Send may find devices on your local network. Please click “Allow”. Otherwise other computers can't find this Mac, and this Mac can't send files.",
                "このあと macOS が、Wormhole Send にローカルネットワーク上のデバイスの検索を許可するか確認します。「許可」をクリックしてください。許可しないと、ほかのコンピュータからこの Mac が見えず、この Mac からファイルを送ることもできません。"))
        }
        text.append(tr(
            "之后点菜单栏的 Wormhole Send 图标，选“配对新设备”，和另一台电脑配对一次就好。",
            "Then click the Wormhole Send icon in the menu bar, choose “Pair New Device”, and pair with another computer once.",
            "その後、メニューバーの Wormhole Send アイコンをクリックし、「新しいデバイスとペアリング」を選んで、もう一台のコンピュータと一度ペアリングすれば準備完了です。"))
        let alert = NSAlert()
        alert.messageText = tr("欢迎使用 Wormhole Send", "Welcome to Wormhole Send", "Wormhole Send へようこそ")
        alert.informativeText = text.joined(separator: "\n\n")
        alert.addButton(withTitle: tr("继续", "Continue", "続ける"))
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }

    /// Runs at start and whenever the menu opens, so the warning goes away
    /// once access is allowed. An open menu is rebuilt when the answer changes.
    private func checkLocalNetwork() {
        guard LocalNetwork.isAsked else { return }
        Task { [weak self] in
            let denied = await LocalNetwork.isDenied()
            self?.log("local network \(denied ? "denied" : "allowed")")
            guard let self, denied != self.networkDenied else { return }
            self.networkDenied = denied
            self.statusItem?.button?.image = StatusIcon.image(warning: denied)
            if self.menuIsOpen, let menu = self.statusItem?.menu {
                menu.removeAllItems()
                self.buildMainMenu(menu)
            }
        }
    }

    private func addNetworkWarning(to menu: NSMenu, _ text: String) {
        menu.addItem(disabledItem(text, symbol: "exclamationmark.triangle.fill"))
        let title = tr("打开“本地网络”设置…", "Open Local Network Settings…", "「ローカルネットワーク」設定を開く…")
        let open = NSMenuItem(title: title, action: #selector(openNetworkSettings), keyEquivalent: "")
        open.target = self
        open.image = icon("gearshape")
        menu.addItem(open)
    }

    @objc private func openNetworkSettings() {
        LocalNetwork.openSettings()
    }

    // MARK: - Pairing started from the menu (incoming requests show on the island)

    private func askToConfirm(pairId: String, alias: String, code: String) {
        let alert = NSAlert()
        alert.messageText = tr("与 \(alias) 配对", "Pair with \(alias)", "\(alias) とペアリング")
        alert.informativeText = tr(
            "确认 \(alias) 上显示的是同一个数字：", "Check that \(alias) shows the same number:",
            "\(alias) に同じ数字が表示されていることを確認してください：") + "\n\n\(formatCode(code))"
        alert.addButton(withTitle: tr("一致，配对", "They Match, Pair", "一致する、ペアリング"))
        alert.addButton(withTitle: tr("拒绝", "Decline", "拒否"))
        NSApp.activate(ignoringOtherApps: true)
        openPairing = pairId
        let response = alert.runModal()
        openPairing = nil
        guard response != .abort else { return }
        let accept = response == .alertFirstButtonReturn
        if case .failure(let failure) = core?.call("pair.confirm", ["pairId": pairId, "accept": accept]) ?? .success(Nothing()) {
            log("pair.confirm failed: \(failure)")
        }
    }

    private func inform(_ title: String, _ text: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = text
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }

    private static var pairingFailed: String { tr("配对失败", "Pairing Failed", "ペアリングできませんでした") }

    private func formatCode(_ code: String) -> String {
        code.count == 6 ? "\(code.prefix(3)) \(code.suffix(3))" : code
    }

    // MARK: - Screens

    /// The notched screen if there is one: the island lives in the notch
    /// while idle and shows incoming transfers there.
    private var homeScreen: NSScreen {
        NSScreen.screens.first { ScreenMode(screen: $0).isNotch } ?? NSScreen.main ?? NSScreen.screens[0]
    }

    private func screen(at point: NSPoint) -> NSScreen {
        NSScreen.screens.first { NSMouseInRect(point, $0.frame, false) } ?? homeScreen
    }

    private func place(on screen: NSScreen) {
        model.mode = ScreenMode(screen: screen)
        panel.place(on: screen)
    }

    private func dragStarted(_ urls: [URL], at location: NSPoint) {
        guard !model.isBusy else { return }
        returnHome?.cancel()
        // The island shows on the screen the drag starts on.
        place(on: screen(at: location))
        model.dragStarted(files: urls)
    }

    private func phaseChanged(_ phase: IslandModel.Phase) {
        log("phase \(phase)")
        // Idle: invisible and transparent to the mouse. Otherwise the island
        // takes drops and clicks.
        panel.ignoresMouseEvents = phase == .idle
        returnHome?.cancel()
        if phase == .idle {
            returnHome = Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(700))
                guard let self, !Task.isCancelled, self.model.phase == .idle else { return }
                self.place(on: self.homeScreen)
            }
        }
    }

    // MARK: - Menu bar item

    private func setUpStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.image = StatusIcon.image(warning: false)
        let menu = NSMenu()
        menu.delegate = self
        item.menu = menu
        statusItem = item
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        if menu === statusItem?.menu {
            buildMainMenu(menu)
            checkLocalNetwork()
            // Announce now, so nearby devices have answered by the time the
            // pointer reaches "配对新设备".
            core?.call("devices.refresh")
        } else {
            let nearby = nearbyDevices()
            shownNearby = nearby.map(\.menuKey)
            buildPairingMenu(menu, nearby: nearby)
        }
    }

    func menuWillOpen(_ menu: NSMenu) {
        guard menu !== statusItem?.menu else {
            menuIsOpen = true
            return
        }
        // Nearby devices only count while they were heard from in the last
        // minute, and answers to the announcement take a moment: keep asking
        // while the submenu is open.
        nearbyRefresh?.cancel()
        nearbyRefresh = Task { [weak self, weak menu] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(1))
                guard let self, let menu, !Task.isCancelled else { return }
                let nearby = self.nearbyDevices()
                guard nearby.map(\.menuKey) != self.shownNearby else { continue }
                self.shownNearby = nearby.map(\.menuKey)
                menu.removeAllItems()
                self.buildPairingMenu(menu, nearby: nearby)
            }
        }
    }

    func menuDidClose(_ menu: NSMenu) {
        if menu === statusItem?.menu {
            menuIsOpen = false
        } else {
            nearbyRefresh?.cancel()
        }
    }

    private func buildMainMenu(_ menu: NSMenu) {
        let name = settings.map { "Wormhole Send · \($0.alias)" } ?? "Wormhole Send"
        let header = disabledItem(name)
        header.image = StatusIcon.image(warning: false, scale: 0.75)
        menu.addItem(header)
        if let coreError {
            let text = tr("核心没有运行：\(coreError)", "Core not running: \(coreError)", "コアが起動していません：\(coreError)")
            menu.addItem(disabledItem(text, symbol: "exclamationmark.octagon"))
        }
        if networkDenied {
            addNetworkWarning(to: menu, tr(
                "没有本地网络权限，其他设备找不到这台 Mac", "No Local Network access: other devices can't find this Mac",
                "ローカルネットワークへのアクセス権がないため、ほかのデバイスからこの Mac が見えません"))
        }
        menu.addItem(.separator())

        if core != nil {
            model.reloadDevices()
            let heading = model.pairedDevices.isEmpty
                ? tr("还没有配对设备", "No Paired Devices Yet", "ペアリングしたデバイスはまだありません")
                : tr("已配对设备", "Paired Devices", "ペアリング済みのデバイス")
            menu.addItem(disabledItem(heading, symbol: "link"))
            let repair = (try? core?.call("devices.list", as: [DeviceInfo].self).get())?
                .filter(\.needsRepair).map(\.fingerprint) ?? []
            for device in model.pairedDevices {
                var title = device.name
                if !device.online { title += tr("（离线）", " (offline)", "（オフライン）") }
                if repair.contains(device.id) { title += tr("（需要重新配对）", " (needs re-pairing)", "（再ペアリングが必要）") }
                let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
                item.state = device.isDefault ? .on : .off
                item.image = icon(device.symbol)
                let actions = NSMenu()
                actions.addItem(actionItem(
                    tr("设为默认设备", "Set as Default Device", "デフォルトのデバイスに設定"), #selector(setDefault(_:)), device.id,
                    symbol: "star", enabled: !device.isDefault))
                actions.addItem(actionItem(
                    tr("解除配对…", "Unpair…", "ペアリングを解除…"), #selector(unpair(_:)), device.id, symbol: "minus.circle"))
                item.submenu = actions
                menu.addItem(item)
            }
            let pairing = NSMenuItem(title: tr("配对新设备", "Pair New Device", "新しいデバイスとペアリング"), action: nil, keyEquivalent: "")
            pairing.image = icon("link.badge.plus")
            let pairingMenu = NSMenu()
            pairingMenu.delegate = self
            pairing.submenu = pairingMenu
            menu.addItem(pairing)
            menu.addItem(.separator())
            let folder = NSMenuItem(
                title: tr("打开接收文件夹", "Open Receive Folder", "受信フォルダを開く"), action: #selector(openSaveFolder), keyEquivalent: "")
            folder.target = self
            folder.image = icon("folder")
            if #available(macOS 14.4, *), let path = settings?.saveDir {
                folder.subtitle = (path as NSString).abbreviatingWithTildeInPath
            }
            menu.addItem(folder)
            let change = NSMenuItem(
                title: tr("更改接收文件夹…", "Change Receive Folder…", "受信フォルダを変更…"), action: #selector(changeSaveFolder),
                keyEquivalent: "")
            change.target = self
            change.image = icon("folder.badge.gearshape")
            menu.addItem(change)
            let sounds = NSMenuItem(title: tr("提示音", "Sounds", "サウンド"), action: #selector(toggleSounds), keyEquivalent: "")
            sounds.target = self
            sounds.image = icon("speaker.wave.2")
            sounds.state = Sound.enabled ? .on : .off
            menu.addItem(sounds)
            menu.addItem(.separator())
        }
        menu.addItem(languageMenu())
        menu.addItem(.separator())
        let quit = NSMenuItem(title: tr("退出", "Quit", "終了"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        quit.image = icon("power")
        menu.addItem(quit)
    }

    /// 跟随系统, then each language in its own name, as on Windows.
    private func languageMenu() -> NSMenuItem {
        let item = NSMenuItem(title: tr("语言", "Language", "言語"), action: nil, keyEquivalent: "")
        item.image = icon("globe")
        let languages = NSMenu()
        let system = Language.system.name
        let choices: [(Language?, String)] = [(nil, tr("跟随系统（\(system)）", "System (\(system))", "システム（\(system)）"))]
            + [Language.en, .zh, .ja].map { ($0, $0.name) }
        for (language, title) in choices {
            let choice = NSMenuItem(title: title, action: #selector(chooseLanguage(_:)), keyEquivalent: "")
            choice.target = self
            choice.representedObject = language?.rawValue
            choice.state = language == Language.choice ? .on : .off
            languages.addItem(choice)
        }
        item.submenu = languages
        return item
    }

    /// Switches at once: the island and the menus from now on, and the
    /// core's messages.
    @objc private func chooseLanguage(_ sender: NSMenuItem) {
        Language.choose((sender.representedObject as? String).flatMap(Language.init(rawValue:)))
        core?.call("language.set", ["language": Language.current.rawValue])
        model.language = Language.current
        log("language \(Language.current.rawValue)")
    }

    /// Online devices that are not paired. Asking also announces this device.
    private func nearbyDevices() -> [DeviceInfo] {
        (try? core?.call("discover.list", as: [DeviceInfo].self).get()) ?? []
    }

    private func buildPairingMenu(_ menu: NSMenu, nearby: [DeviceInfo]) {
        guard core != nil else { return }
        if nearby.isEmpty, networkDenied {
            addNetworkWarning(to: menu, tr(
                "没有本地网络权限，搜索不到附近设备", "No Local Network access: can't find nearby devices",
                "ローカルネットワークへのアクセス権がないため、近くのデバイスを検索できません"))
        } else if nearby.isEmpty {
            let searching = tr("正在搜索附近设备…", "Searching for nearby devices…", "近くのデバイスを検索中…")
            menu.addItem(disabledItem(searching, symbol: "antenna.radiowaves.left.and.right"))
        }
        for device in nearby {
            let symbol = Device.symbol(for: device.deviceType)
            menu.addItem(actionItem(device.alias, #selector(startPairing(_:)), device.fingerprint, symbol: symbol))
        }
    }

    private func icon(_ symbol: String) -> NSImage? {
        NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
    }

    private func disabledItem(_ title: String, symbol: String? = nil) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        item.isEnabled = false
        item.image = symbol.flatMap(icon)
        return item
    }

    private func actionItem(
        _ title: String, _ action: Selector, _ deviceId: String, symbol: String, enabled: Bool = true
    ) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: enabled ? action : nil, keyEquivalent: "")
        item.target = self
        item.representedObject = deviceId
        item.isEnabled = enabled
        item.image = icon(symbol)
        return item
    }

    @objc private func setDefault(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String else { return }
        core?.call("device.setDefault", ["deviceId": id])
        model.reloadDevices()
    }

    @objc private func unpair(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String,
            let device = model.pairedDevices.first(where: { $0.id == id })
        else { return }
        let alert = NSAlert()
        alert.messageText = tr("解除与 \(device.name) 的配对？", "Unpair \(device.name)?", "\(device.name) とのペアリングを解除しますか？")
        alert.informativeText = tr(
            "对方会收到通知。之后互传文件需要重新配对。", "\(device.name) will be notified. To send files again, you'll need to pair again.",
            "相手に通知されます。再びファイルをやり取りするには、もう一度ペアリングが必要です。")
        alert.addButton(withTitle: tr("解除配对", "Unpair", "ペアリング解除"))
        alert.addButton(withTitle: tr("取消", "Cancel", "キャンセル"))
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        core?.call("device.unpair", ["deviceId": id], as: UnpairResult.self)
        model.reloadDevices()
    }

    @objc private func startPairing(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String, let core else { return }
        switch core.call("pair.start", ["deviceId": id], as: String.self) {
        case .success(let pairId): startedPairs.insert(pairId)
        case .failure(let failure): inform(Self.pairingFailed, failure.message)
        }
    }

    /// The core saves it and restarts its services; it refuses while files
    /// are being transferred.
    @objc private func changeSaveFolder() {
        guard let core else { return }
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = tr("选择", "Choose", "選択")
        panel.message = tr("收到的文件会保存到这个文件夹", "Received files will be saved to this folder", "受信したファイルはこのフォルダに保存されます")
        if let path = settings?.saveDir { panel.directoryURL = URL(fileURLWithPath: path) }
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK, let url = panel.url else { return }
        switch core.call("settings.set", ["saveDir": url.path], as: Settings.self) {
        case .success(let saved): settings = saved
        case .failure(let failure):
            inform(tr("无法更改接收文件夹", "Couldn't Change the Receive Folder", "受信フォルダを変更できませんでした"), failure.message)
        }
    }

    @objc private func toggleSounds() {
        Sound.enabled.toggle()
        if Sound.enabled { Sound.sent.play() }
    }

    @objc private func openSaveFolder() {
        guard let path = settings?.saveDir else { return }
        try? FileManager.default.createDirectory(atPath: path, withIntermediateDirectories: true)
        NSWorkspace.shared.open(URL(fileURLWithPath: path))
    }

    private func log(_ text: String) {
        if CommandLine.arguments.contains("--log") {
            FileHandle.standardError.write(Data("\(text)\n".utf8))
        }
    }
}

private struct UnpairResult: Decodable {
    let wasPaired: Bool
}

private extension DeviceInfo {
    /// What the pairing submenu shows of a device.
    var menuKey: String { "\(fingerprint) \(alias)" }
}

enum Arguments {
    static func value(of flag: String) -> String? {
        let arguments = CommandLine.arguments
        guard let index = arguments.firstIndex(of: flag), index + 1 < arguments.count else { return nil }
        return arguments[index + 1]
    }
}
