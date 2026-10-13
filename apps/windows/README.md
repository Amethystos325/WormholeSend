# Wormhole Send for Windows

The Windows app: the island at the top of the screen, the tray icon, settings, pairing and the first-run guide, on the Rust core through `core-ffi` (`wormhole.dll`). C# / WPF on .NET 10 with [WPF-UI](https://github.com/lepoco/wpfui) for the Fluent windows; Windows 10 and 11.

The island follows the macOS app state for state: the phases, timings and sizes in `Phase.cs`, `IslandModel.cs` and `Spec.cs` are ports of `IslandModel.swift` and `Spec.swift`. Change both sides together.

## Building and running

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download), [Rust](https://rustup.rs) and Visual Studio Build Tools with "Desktop development with C++", and once `git config --global core.longpaths true` (see the top-level README). Then, from this directory:

```powershell
dotnet run --project WormholeSend
```

`dotnet build` builds the core with cargo first and copies `wormhole.dll` next to the app (`-p:SkipCoreBuild=true` skips cargo). Or open `WormholeSend.slnx` in Visual Studio.

The first run shows a three-step guide (name, the firewall prompt, the first pairing) before the services start. When Windows asks about the firewall, allow private networks. Data lives in `%APPDATA%\WormholeSend`, shared with `whs`.

## What's where

| File | |
| --- | --- |
| `IslandModel.cs`, `Phase.cs` | The state machine: drags from the windows, core events, pairing requests |
| `IslandView.cs`, `IslandContent.cs`, `Parts.cs` | The island and each phase's content, with the spring morphing |
| `IslandWindow.cs`, `Screens.cs` | One transparent topmost window per screen, the sensor, DPI, full-screen hiding |
| `DragMonitor.cs`, `DragWatch.cs` | Drags anywhere on screen, for the hint; the end of a drag that left the island |
| `Core.cs`, `CoreEvents.cs`, `Transfers.cs` | P/Invoke to `wormhole.dll`, typed events, real and simulated transfers |
| `Sounds.cs` | The sounds of delivered, received, failed and a pairing request, at 60 %: `assets/sounds` (made by `make-sounds.py`), the same files as on macOS; 提示音 in the tray menu |
| `AppController.cs`, `TrayIcon.cs`, `TrayGlyph.cs` | Wiring: screens, core, windows; the tray icon (the black hole of `assets/tray-icon.svg`) |
| `TrayMenu.cs` | The tray menu (design doc 7.2) in Windows 11's style: acrylic, icons, submenus, keyboard |
| `AppIcon.cs` | The app icon (`assets/app-icon.ico`, made on the Mac from `app-icon.svg`) in the title bars; also the exe's icon |
| `SettingsWindow`, `PairWindow`, `WelcomeWindow` | Settings (7.1), pairing with the 6-digit code (3.1), first-run guide (7.3) |

## How it differs from macOS

- Windows has no global drag events without hooks. While a mouse button is held the app looks for ole32's drag window, which has the mouse captured during a drag (`DragMonitor.cs`), and shows the hint on the screen under the cursor, following it between screens. What a drag carries is only known once it reaches the island, so text and links show the hint too; over the island they get "只能发送文件".
- Drags that don't go through DoDragDrop aren't noticed until they reach the 240 × 6 sensor at the top center of each screen, which opens the island there.
- The sensor and the drop halo are painted at alpha 1/255: layered windows only take drags and clicks on pixels that are not fully transparent. The sensor therefore also takes clicks in its 240 × 6 area.
- After a drag leaves the island the hint stays until the mouse button is released, which the app learns by polling the buttons (no drag events reach it then).
- Full-screen apps hide the windows on their screen: what the shell reports as full screen (QUNS_BUSY, D3D, presentation mode) or a window without a title bar covering the monitor. Ordinary maximized windows don't.
- Received files can be dragged out to Explorer or the desktop; they are copied, not moved.
- Phases the macOS spike doesn't have yet: `pairRequest` (another device asks to pair; 380 × 78 content, the code on the right, 拒绝 / 配对), `pairWaiting` and `notice` (340 × 28 status lines). `interrupted(reason)` matches the macOS app.
- While receiving, a new drag takes over the island; the receive shows again afterwards. A send shows before a receive (design doc 2.2).

## Command line

```powershell
WormholeSend.exe [--home DIR] [--port N] [--log]     # a second device on one PC, as with whs
WormholeSend.exe --fake [--log]                       # simulated devices and transfers, switched from the tray menu
WormholeSend.exe --demo picker [--screen external|N] [--devices 1] [--snapshot out.png]
WormholeSend.exe --selftest --log                     # drags through OLE, without the mouse
WormholeSend.exe --fake --menu [--snapshot out.png]   # the tray menu with a submenu open, at the screen center
```

`--demo` freezes one state: `hint` `target` `picker` `picker-offline` `single` `unavailable` `sending` `queued` `queued-compact` `done` `failed` `refused` `receiving` `received` `pair-request` `notice`. `--snapshot` saves the window as it shows on screen and quits. `--log` prints state changes and core events to stderr. Starting the app again opens the settings of the one running.

## Checks

```powershell
dotnet test       # the state machine, core events, and two real cores pairing and sending through P/Invoke
```

`--selftest` calls the window's registered drop target the way `DoDragDrop` does and checks with `WindowFromPoint` which pixels take the pointer; it exits with 0 when all checks pass.

Checked so far on one 4K screen at 225 %: every state with `--demo`; `--selftest`; the hint on drags from Explorer and the desktop; real mouse drags from another process (drop via the device list, out and back, drop on an offline device); hiding under borderless full-screen and borderless maximized windows but not under ordinary maximized ones; and, against `whs` through UI Automation, an incoming pairing answered on the island, receiving a folder, the unpair notice, and pairing from the pairing window. Not yet checked: a second monitor, 150 % scaling, monitors with different scaling, and a Mac on the other end.

## Not done yet

- Installer, with Inno Setup (design doc 9). It should add firewall rules for TCP 53320, TCP 53321 and UDP 53320, use `assets/app-icon.ico`, and be code-signed against SmartScreen.
- The private key is in `identity.pem`, not yet protected with DPAPI (design doc 3.3).
- A hint "正在发送" when dragging in during a send (2.1); for now such a drag is ignored.

## Manual checklist

- [ ] First run: the guide asks for the name, explains the firewall prompt, then offers pairing; "开机时启动" sets the Run key
- [ ] Pair with another computer from 设置 › 配对新设备: both screens show the same code
- [ ] Another computer pairs with this one: the island shows the request with the code; 配对 / 拒绝 work
- [ ] Start dragging a file in Explorer or on the desktop: the hint shows at the top center at once; Esc, or releasing elsewhere, hides it
- [ ] Drag selected text from a browser: the hint shows; over the island "只能发送文件"
- [ ] Drag a file to the top center of the screen: the island opens with "松手发送到 …"; release: sending → delivered
- [ ] After the island opens, drag down into the "其他设备" strip: the device list opens; release over another device
- [ ] Release over an offline device: shows "离线，未发送"
- [ ] Drag out of the island and keep holding: the hint stays; release elsewhere and it collapses
- [ ] The receiver is busy with another sender: "排队中 · 前面还有 N 个", a small capsule after 3 s, an orange dot on the tray icon
- [ ] The other computer quits mid-send: "发送失败" with 重试
- [ ] Receive files: progress, then the files can be dragged out, "在资源管理器中显示" works, and it stays open while hovered
- [ ] The sender cancels: "传输中断"
- [ ] Sending shows "945 MB / 2.1 GB · 45 MB/s" under the bar, or "3 / 12 个文件 · …" for several files or a folder; the speed after a second
- [ ] Long names shorten in the middle and keep "等 N 个文件": the target, the device list, "已送达"
- [ ] Sounds: delivered, received, a failed send, a receive that breaks off (not when the sender cancels), a pairing request
- [ ] Tray menu: default device (offline grey), 配对新设备, 打开接收文件夹, 提示音 (plays one when turned on), 设置, 退出
- [ ] Second monitor: dragging into its sensor opens the island there; incoming transfers show on the primary screen
- [ ] 150 % scaling, and monitors with different scaling: crisp, at the top center of each
- [ ] A full-screen video or game: no sensor and no island on that screen
