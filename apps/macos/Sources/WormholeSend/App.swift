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

/// Owns the panel, the drag monitor and the menu bar item. The menu holds
/// switches for the fake data while the core is not connected yet.
@MainActor
final class AppController: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let model = IslandModel()
    private lazy var panel = NotchPanel(model: model)
    private let dragMonitor = DragMonitor()
    private var statusItem: NSStatusItem?
    private var returnHome: Task<Void, Never>?

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
        Demo.apply(arguments: CommandLine.arguments, to: model) { [weak self] screen in
            self?.place(on: screen)
        }
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
        if CommandLine.arguments.contains("--log") {
            FileHandle.standardError.write(Data("phase \(phase)\n".utf8))
        }
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
        let title = NSMenuItem(title: "Wormhole Send · 刘海验证（假数据）", action: nil, keyEquivalent: "")
        title.isEnabled = false
        menu.addItem(title)
        menu.addItem(.separator())

        let devices = NSMenu()
        for count in [0, 1, 3] {
            let item = NSMenuItem(title: "\(count) 台", action: #selector(setDeviceCount(_:)), keyEquivalent: "")
            item.target = self
            item.tag = count
            item.state = model.devices.count == count ? .on : .off
            devices.addItem(item)
        }
        let devicesItem = NSMenuItem(title: "配对设备", action: nil, keyEquivalent: "")
        devicesItem.submenu = devices
        menu.addItem(devicesItem)

        let offline = NSMenuItem(title: "默认设备离线", action: #selector(toggleDefaultOffline), keyEquivalent: "")
        offline.target = self
        offline.state = model.defaultDevice?.online == false ? .on : .off
        offline.isEnabled = model.defaultDevice != nil
        menu.addItem(offline)

        let outcomes = NSMenu()
        for (index, outcome) in IslandModel.SendOutcome.allCases.enumerated() {
            let item = NSMenuItem(title: outcome.rawValue, action: #selector(setOutcome(_:)), keyEquivalent: "")
            item.target = self
            item.tag = index
            item.state = model.nextOutcome == outcome ? .on : .off
            outcomes.addItem(item)
        }
        let outcomesItem = NSMenuItem(title: "发送结果", action: nil, keyEquivalent: "")
        outcomesItem.submenu = outcomes
        menu.addItem(outcomesItem)

        menu.addItem(.separator())
        let receive = NSMenuItem(title: "模拟接收文件", action: #selector(simulateReceive), keyEquivalent: "r")
        receive.target = self
        receive.isEnabled = model.phase == .idle
        menu.addItem(receive)

        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "退出", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
    }

    @objc private func setDeviceCount(_ sender: NSMenuItem) {
        model.devices = Array(Device.samples.prefix(sender.tag))
    }

    @objc private func toggleDefaultOffline() {
        guard let index = model.devices.firstIndex(where: \.isDefault) else { return }
        model.devices[index].online.toggle()
    }

    @objc private func setOutcome(_ sender: NSMenuItem) {
        model.nextOutcome = IslandModel.SendOutcome.allCases[sender.tag]
    }

    @objc private func simulateReceive() {
        place(on: homeScreen)
        model.simulateReceive()
    }
}
