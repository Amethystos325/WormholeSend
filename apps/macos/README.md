# Wormhole Send for macOS: notch panel check

The second check from the design doc: the notch panel, global drag detection, the device list and the animations, with fake data and no Rust core connected. SwiftUI + AppKit, macOS 14+.

## Running it

```bash
./scripts/bundle.sh          # builds build/Wormhole Send.app (ad-hoc signed)
open "build/Wormhole Send.app"
```

You can also open this directory in Xcode (it's a Swift Package) and run it from there.

The menu bar icon (a radio-wave arrow) switches the fake data: number of paired devices, default device offline, the result of the next send (success / receiver busy / failure), and a simulated receive.

## Demo mode (pinned to a single state, for screenshots)

```bash
"build/Wormhole Send.app/Contents/MacOS/WormholeSend" --demo picker [--screen external] [--log]
```

States: `hint` `target` `picker` `picker-offline` `single` `unavailable` `sending` `queued` `queued-compact` `done` `failed` `refused` `receiving` `received`. `--log` prints every state change to stderr.

## Manual checklist

- [ ] Start dragging a file in Finder: the island shows the hint "移到这里发送到 书房台式机"
- [ ] Drag into the notch and release right away: sending → delivered, then it collapses after 1.5 s
- [ ] After the island opens, drag down into the "其他设备" strip: the device list opens; slide to another device and release to send to it
- [ ] Coming up from below to the default device: passing through the strip position does not open the device list by mistake
- [ ] Release over an offline device: shows "离线，未发送" and the file flies back
- [ ] Drag out of the island and release: the island collapses and the file flies back
- [ ] Turn on "默认设备离线" in the menu: dragging in opens the device list right away
- [ ] Set devices to 1, then to 0: one device is a single large target; with 0 it shows "还没有配对设备"
- [ ] Set the send result to "对方忙": the queued state collapses to a small badge beside the camera after 3 s, then starts sending
- [ ] Set the send result to "失败": "发送失败" with a retry button; retry works
- [ ] "模拟接收文件": shows progress; when done the files can be dragged out, "在访达中显示" works, and it stays open while hovered
- [ ] Drag on the external monitor: a floating capsule appears at the top of that screen
- [ ] While the island is showing, click menu bar items next to the notch: they still work (clicks pass through the transparent area)
- [ ] Switch to a full-screen app and drag: the island still appears
