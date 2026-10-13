# Wormhole Send for Windows: top island check

The third check from the design doc: the sensor along the top of each screen, drops, the device list, multiple monitors and DPI, and hiding under full-screen apps, with fake data and no Rust core connected. C# / WPF on .NET 10, Windows 10 and 11.

It follows the macOS check (`apps/macos`) state for state: the phases, timings and sizes in `Phase.cs`, `IslandModel.cs` and `Spec.cs` are ports of `IslandModel.swift` and `Spec.swift`. Change both sides together.

## Running it

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download), then:

```powershell
dotnet run --project WormholeSend
```

Or open `WormholeSend.slnx` in Visual Studio. The tray icon (an arrow to the top) switches the fake data: number of paired devices, default device offline, the result of the next send (success / receiver busy / failure), and a simulated receive.

## How it differs from macOS

- Windows has no global drag events without hooks, so the island doesn't show when a drag starts. Each screen has a 240 × 6 sensor at the top center; dragging a file into it opens the island on that screen.
- The sensor and the drop halo are painted at alpha 1/255: layered windows only take drags and clicks on pixels that are not fully transparent. The sensor therefore also takes clicks in its 240 × 6 area.
- After a drag leaves the island the hint stays until the mouse button is released, which the app learns by polling the buttons (no drag events reach it then).
- Full-screen apps (games, videos, slideshows, F11 browsers) hide the windows on their screen.
- Received files can be dragged out to Explorer or the desktop; they are copied, not moved.

## Checks

```powershell
dotnet test                                   # the state machine and the spring
WormholeSend.exe --selftest --log             # drags through OLE, without the mouse
WormholeSend.exe --demo picker [--screen external|N] [--devices 1] [--log] [--snapshot out.png]
```

`--selftest` calls the window's registered drop target the way `DoDragDrop` does and checks with `WindowFromPoint` which pixels take the pointer; it prints one line per check and exits with 0 when all pass. `--demo` freezes one state, as on macOS: `hint` `target` `picker` `picker-offline` `single` `unavailable` `sending` `queued` `queued-compact` `done` `failed` `refused` `receiving` `received`. `--snapshot` saves the window as it shows on screen and quits. `--log` prints every state change to stderr.

Checked so far on one 4K screen at 225 %: every state with `--demo`, `--selftest`, real mouse drags from another process (drop via the device list, out and back, drop on an offline device), and hiding under borderless full-screen and borderless maximized windows but not under ordinary maximized ones. Not yet checked: a second monitor, 150 % scaling, monitors with different scaling.

## Manual checklist

- [ ] Drag a file from Explorer to the top center of the screen: the island opens with "松手发送到 书房台式机"
- [ ] Release right away: sending → delivered, then it collapses after 1.5 s
- [ ] After the island opens, drag down into the "其他设备" strip: the device list opens; slide to another device and release to send to it
- [ ] Release over an offline device: shows "离线，未发送"
- [ ] Drag out of the island and keep holding: the hint "移到这里发送到 …" stays; release elsewhere and it collapses
- [ ] Turn on "默认设备离线" in the tray menu: dragging in opens the device list right away
- [ ] Set devices to 1, then to 0: one device is a single large target; with 0 it shows "还没有配对设备"
- [ ] Set the send result to "对方忙（排队）": the queued state collapses to a small capsule after 3 s and the tray icon gets an orange dot, then it starts sending
- [ ] Set the send result to "失败": "发送失败" with a retry button; retry works
- [ ] "模拟接收文件": shows progress; when done the files can be dragged out, "在资源管理器中显示" works, and it stays open while hovered
- [ ] Second monitor: dragging into its sensor opens the island there; a simulated receive shows on the primary screen
- [ ] 150 % scaling, and monitors with different scaling: the island is crisp and at the top center of each
- [ ] While the island shows, click and drag windows next to it: the transparent area lets everything through
- [ ] A full-screen video or game: no sensor and no island on that screen
