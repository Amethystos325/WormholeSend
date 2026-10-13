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
            startCore()
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        core?.stop()
    }

    // MARK: - Core

    private func startCore() {
        var config: [String: Any] = ["portFallback": false]
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
                log("core \(Core.version) started")
            } catch {
                self?.coreError = "\(error)"
                self?.log("core failed: \(error)")
            }
        }
    }

    private func handle(_ event: CoreEvent) {
        log("event \(event.event) \(event.id ?? event.pairId ?? "")")
        model.handle(event)
        guard let pairId = event.pairId, startedPairs.contains(pairId) else { return }
        switch event.event {
        case "pair.code":
            if let code = event.code {
                askToConfirm(pairId: pairId, alias: event.alias ?? "对方", code: code)
            }
        case "pair.result":
            startedPairs.remove(pairId)
            if pairId == openPairing {
                // The dialog is still open: the other side declined or it expired.
                NSApp.stopModal(withCode: .abort)
            }
            inform(
                event.ok == true ? "已与 \(event.alias ?? "对方") 配对" : "配对失败",
                event.ok == true ? "之后互传文件不用确认。" : (event.reason ?? "未知原因"))
        default:
            break
        }
    }

    // MARK: - Pairing started from the menu (incoming requests show on the island)

    private func askToConfirm(pairId: String, alias: String, code: String) {
        let alert = NSAlert()
        alert.messageText = "与 \(alias) 配对"
        alert.informativeText = "确认 \(alias) 上显示的是同一个数字：\n\n\(formatCode(code))"
        alert.addButton(withTitle: "一致，配对")
        alert.addButton(withTitle: "拒绝")
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
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = NSImage(systemSymbolName: "dot.radiowaves.up.forward", accessibilityDescription: "Wormhole Send")
        let menu = NSMenu()
        menu.delegate = self
        item.menu = menu
        statusItem = item
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        if menu === statusItem?.menu {
            buildMainMenu(menu)
        } else {
            buildPairingMenu(menu)
        }
    }

    private func buildMainMenu(_ menu: NSMenu) {
        let name = settings.map { "Wormhole Send · \($0.alias)" } ?? "Wormhole Send"
        menu.addItem(disabledItem(name))
        if let coreError {
            menu.addItem(disabledItem("核心没有运行：\(coreError)"))
        }
        menu.addItem(.separator())

        if core != nil {
            model.reloadDevices()
            menu.addItem(disabledItem(model.devices.isEmpty ? "还没有配对设备" : "已配对设备"))
            let repair = (try? core?.call("devices.list", as: [DeviceInfo].self).get())?
                .filter(\.needsRepair).map(\.fingerprint) ?? []
            for device in model.devices {
                var title = device.name
                if !device.online { title += "（离线）" }
                if repair.contains(device.id) { title += "（需要重新配对）" }
                let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
                item.state = device.isDefault ? .on : .off
                let actions = NSMenu()
                actions.addItem(actionItem("设为默认设备", #selector(setDefault(_:)), device.id, enabled: !device.isDefault))
                actions.addItem(actionItem("解除配对…", #selector(unpair(_:)), device.id))
                item.submenu = actions
                menu.addItem(item)
            }
            let pairing = NSMenuItem(title: "配对新设备", action: nil, keyEquivalent: "")
            let pairingMenu = NSMenu()
            pairingMenu.delegate = self
            pairing.submenu = pairingMenu
            menu.addItem(pairing)
            menu.addItem(.separator())
            let folder = NSMenuItem(title: "打开接收文件夹", action: #selector(openSaveFolder), keyEquivalent: "")
            folder.target = self
            menu.addItem(folder)
            menu.addItem(.separator())
        }
        menu.addItem(NSMenuItem(title: "退出", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
    }

    /// Nearby devices that are not paired. Listing them also announces this
    /// device, so reopening the menu finds newcomers.
    private func buildPairingMenu(_ menu: NSMenu) {
        guard let core else { return }
        let nearby = (try? core.call("discover.list", as: [DeviceInfo].self).get()) ?? []
        if nearby.isEmpty {
            menu.addItem(disabledItem("正在搜索附近设备…（稍后再打开此菜单）"))
        }
        for device in nearby {
            menu.addItem(actionItem(device.alias, #selector(startPairing(_:)), device.fingerprint))
        }
    }

    private func disabledItem(_ title: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        item.isEnabled = false
        return item
    }

    private func actionItem(_ title: String, _ action: Selector, _ deviceId: String, enabled: Bool = true) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: enabled ? action : nil, keyEquivalent: "")
        item.target = self
        item.representedObject = deviceId
        item.isEnabled = enabled
        return item
    }

    @objc private func setDefault(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String else { return }
        core?.call("device.setDefault", ["deviceId": id])
        model.reloadDevices()
    }

    @objc private func unpair(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String,
            let device = model.devices.first(where: { $0.id == id })
        else { return }
        let alert = NSAlert()
        alert.messageText = "解除与 \(device.name) 的配对？"
        alert.informativeText = "对方会收到通知。之后互传文件需要重新配对。"
        alert.addButton(withTitle: "解除配对")
        alert.addButton(withTitle: "取消")
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        core?.call("device.unpair", ["deviceId": id], as: UnpairResult.self)
        model.reloadDevices()
    }

    @objc private func startPairing(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String, let core else { return }
        switch core.call("pair.start", ["deviceId": id], as: String.self) {
        case .success(let pairId): startedPairs.insert(pairId)
        case .failure(let failure): inform("配对失败", failure.message)
        }
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

enum Arguments {
    static func value(of flag: String) -> String? {
        let arguments = CommandLine.arguments
        guard let index = arguments.firstIndex(of: flag), index + 1 < arguments.count else { return nil }
        return arguments[index + 1]
    }
}
