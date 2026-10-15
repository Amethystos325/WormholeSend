# Wormhole Send for Windows

The Windows app: the island at the top of the screen, the tray icon, settings, pairing and the first-run guide, on the Rust core through `core-ffi` (`wormhole.dll`). C# / WPF on .NET 10 with [WPF-UI](https://github.com/lepoco/wpfui) for the Fluent windows; Windows 10 and 11.

The island follows the macOS app state for state: the phases, timings and sizes in `Phase.cs`, `IslandModel.cs` and `Spec.cs` are ports of `IslandModel.swift` and `Spec.swift`. Change both sides together.

## Building and running

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download), [Rust](https://rustup.rs) and Visual Studio Build Tools with "Desktop development with C++", and once `git config --global core.longpaths true` (see [the CLI guide](../../docs/cli.md#building)). Then, from this directory:

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
| `Sounds.cs` | The sounds of delivered, received, failed and a pairing request, at 60 %: `assets/sounds` (made by `make-sounds.py`), the same files as on macOS; Sounds in the tray menu |
| `AppController.cs`, `TrayIcon.cs`, `TrayGlyph.cs` | Wiring: screens, core, windows; the tray icon (the black hole of `assets/tray-icon.svg`) |
| `TrayMenu.cs` | The tray menu in Windows 11's style: acrylic, icons, submenus, keyboard |
| `DeviceGlyph.cs` | The app's own device icons (`assets/device-icons.svg`, as `DeviceGlyph.swift` on macOS): cream on a deep-space plate in the island, ringed in amber on the device a drop goes to; also in the tray menu, the pairing and settings windows |
| `AppIcon.cs` | The app icon (`assets/app-icon.ico`, made on the Mac from `app-icon.svg`) in the title bars; also the exe's icon |
| `StatusLines.cs` | What the one-line states say, and their width: as wide as the text, up to 528; past that the text shrinks to 85 % (as on macOS) |
| `Lang.cs`, `Strings.cs` | The interface language (English, Chinese, Japanese) and the windows' texts |
| `SettingsWindow`, `PairWindow`, `WelcomeWindow` | Settings, pairing with the 6-digit code, the first-run guide |

## How it differs from macOS

- Windows has no global drag events without hooks. While a mouse button is held the app looks for ole32's drag window, which has the mouse captured during a drag (`DragMonitor.cs`), and shows the hint on the screen under the cursor, following it between screens. What a drag carries is only known once it reaches the island, so text and links show the hint too; over the island they get "Only files can be sent".
- Apps built on Chromium (VS Code's explorer, for one) don't give a file list (CF_HDROP) but the paths as text, one per line. Text counts as files when every line is a full path or a file: URL of something that exists. `--log` prints what a drag that isn't files carries.
- Drags that don't go through DoDragDrop aren't noticed until they reach the 240 × 6 sensor at the top center of each screen, which opens the island there.
- The sensor and the drop halo are painted at alpha 1/255: layered windows only take drags and clicks on pixels that are not fully transparent. The sensor therefore also takes clicks in its 240 × 6 area.
- After a drag leaves the island the hint stays until the mouse button is released, which the app learns by polling the buttons (no drag events reach it then).
- Full-screen apps hide the windows on their screen: what the shell reports as full screen (QUNS_BUSY, D3D, presentation mode) or a window without a title bar covering the monitor. Ordinary maximized windows don't.
- Received files can be dragged out to Explorer or the desktop; they are copied, not moved.
- Phases the macOS app doesn't have yet: `pairRequest` (another device asks to pair; 380 × 78 content, the code on the right, Decline / Pair), `pairWaiting` and `notice` (340 × 28 status lines). `interrupted(reason)` matches the macOS app.
- LocalSend devices (the LocalSend app, which can't pair) come after the paired ones in the device list, marked "LocalSend"; the default device is always a paired one, and without paired devices a drag goes straight to the list. Sending to one waits ("Waiting for … to accept…", `awaitingAcceptance`) until its user accepts; declining ends as a failure. A LocalSend device that sends here asks first (`receiveRequest`: Decline / Accept with the size, or Ignore / Copy with the start of a text message, which is copied to the clipboard), as a pairing request does: after a drag or a send, with the pairing request's sound.
- The interface is in English, Chinese or Japanese: Windows' display language, unless one is chosen in the settings (kept in `HKCU\Software\WormholeSend`, `Language`). Each text is written out in the three where it is used, `L.T(zh, en, ja)`, as `tr!` in the core, which is told the same language (`language` at start, `language.set` on a change). Changing it builds the islands and the settings window again. Japanese text is set in Yu Gothic UI throughout, so a line doesn't mix two fonts' weights; Chinese in Segoe UI with Microsoft YaHei UI.
- While receiving, a new drag takes over the island; the receive shows again afterwards. A send shows before a receive.

## Command line

```powershell
WormholeSend.exe [--home DIR] [--port N] [--log]     # a second device on one PC, as with whs
WormholeSend.exe --fake [--log]                       # simulated devices and transfers, switched from the tray menu
WormholeSend.exe --demo picker [--screen external|N] [--devices 1] [--snapshot out.png]
WormholeSend.exe --selftest --log                     # drags through OLE, without the mouse
WormholeSend.exe --fake --menu [--snapshot out.png]   # the tray menu with a submenu open, at the screen center
WormholeSend.exe … --lang en|zh|ja                    # any of the above in a language, for this run
```

`--demo` freezes one state: `hint` `target` `picker` `picker-offline` `single` `unavailable` `sending` `queued` `queued-compact` `done` `failed` `failed-long` `refused` `receiving` `received` `pair-request` `notice`, and for LocalSend `receive-request` `receive-text` `copied` `awaiting` `picker-localsend`. `--snapshot` saves the window as it shows on screen and quits. `--log` prints state changes and core events to stderr. Starting the app again opens the settings of the one running.

## Checks

```powershell
dotnet test       # the state machine, core events, and two real cores pairing and sending through P/Invoke
```

`--selftest` calls the window's registered drop target the way `DoDragDrop` does and checks with `WindowFromPoint` which pixels take the pointer; it exits with 0 when all checks pass.

Checked so far on one 4K screen at 225 %: every state with `--demo`; `--selftest`; the hint on drags from Explorer and the desktop; real mouse drags from another process (drop via the device list, out and back, drop on an offline device); hiding under borderless full-screen and borderless maximized windows but not under ordinary maximized ones; and, against `whs` through UI Automation, an incoming pairing answered on the island, receiving a folder, the unpair notice, and pairing from the pairing window. Not yet checked: a second monitor, 150 % scaling, monitors with different scaling, and a Mac on the other end. What's still missing on Windows is in the [roadmap](../../docs/roadmap.md).

## Manual checklist

- [ ] First run: the guide asks for the name, explains the firewall prompt, then offers pairing; "Start Wormhole Send with Windows" sets the Run key
- [ ] Pair with another computer from Settings › Pair a new device: both screens show the same code
- [ ] Another computer pairs with this one: the island shows the request with the code; Pair / Decline work
- [ ] Start dragging a file in Explorer or on the desktop: the hint shows at the top center at once; Esc, or releasing elsewhere, hides it
- [ ] Drag selected text from a browser: the hint shows; over the island "Only files can be sent"
- [ ] Drag a file to the top center of the screen: the island opens with "Release to send to …"; release: sending → delivered
- [ ] After the island opens, drag down into the "Other devices" strip: the device list opens; release over another device
- [ ] Release over an offline device: shows "… is offline. Not sent"
- [ ] Drag out of the island and keep holding: the hint stays; release elsewhere and it collapses
- [ ] The receiver is busy with another sender: "Queued · N ahead", a small capsule after 3 s, an orange dot on the tray icon
- [ ] The other computer quits mid-send: "Couldn't send" with Retry
- [ ] Receive files: progress, then the files can be dragged out, "Show in File Explorer" works, and it stays open while hovered
- [ ] The sender cancels: "Transfer interrupted"
- [ ] Sending shows "945 MB / 2.1 GB · 45 MB/s" under the bar, or "3 / 12 files · …" for several files or a folder; the speed after a second
- [ ] Long names shorten in the middle and keep "and N more files": the target, the device list, "Delivered to …"
- [ ] Sounds: delivered, received, a failed send, a receive that breaks off (not when the sender cancels), a pairing request
- [ ] A LocalSend app on the network: it shows in the device list as "LocalSend"; sending waits for its user, then runs, or fails with "… declined"
- [ ] The LocalSend app sends files: the island asks (Decline / Accept), then receives; it sends a text: Copy copies it and says so
- [ ] Language: follows Windows' display language; choosing English, Simplified Chinese or Japanese in the settings switches the island, the menu, the settings and the core's messages at once
- [ ] Tray menu: default device (offline grey), Pair a new device, Open the receive folder, Sounds (plays one when turned on), Settings…, Exit
- [ ] Second monitor: dragging into its sensor opens the island there; incoming transfers show on the primary screen
- [ ] 150 % scaling, and monitors with different scaling: crisp, at the top center of each
- [ ] A full-screen video or game: no sensor and no island on that screen
