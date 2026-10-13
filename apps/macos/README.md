# Wormhole Send for macOS: notch panel check

The notch panel on top of the Rust core: SwiftUI + AppKit, macOS 14+. The core is linked as a static library through core-ffi (`libwormhole.a`, header `core-ffi/include/wormhole.h`). Sending, receiving, the queue, interruptions and pairing all run through the real core. Pairing you start from the menu bar confirms the 6-digit code in a dialog; requests from other devices show on the island.

## Running it

```bash
./scripts/bundle.sh          # builds the core (scripts/build-core.sh) and build/Wormhole Send.app (ad-hoc signed)
open "build/Wormhole Send.app"
```

Building the core needs Rust. `scripts/build-core.sh --universal` produces arm64 + x86_64 (first run `rustup target add aarch64-apple-darwin x86_64-apple-darwin`).

To test against `whs` on the same Mac, give the app its own data directory and port: `"build/Wormhole Send.app/Contents/MacOS/WormholeSend" --home /tmp/app --port 53340 --log`.

You can also open this directory in Xcode (it's a Swift Package) and run it from there.

Menu bar icon (a black hole: a ring with its accretion disk tilted across it, `Sources/WormholeSend/StatusIcon.swift`, source `assets/tray-icon.svg`): paired devices (set as default, unpair), pair a new device (nearby devices), open the receive folder.

The first launch shows a welcome that explains the Local Network prompt macOS 15+ shows next (to see it again: `defaults delete app.wormholesend.mac welcomed`). Without that permission the system silently drops discovery and outgoing transfers, so the app checks it at start and whenever the menu opens. When it is denied, the menu bar icon gets an amber dot, and the menu explains why and links to the setting.

## Demo mode (pinned to a single state with sample data; the core does not start)

```bash
"build/Wormhole Send.app/Contents/MacOS/WormholeSend" --demo picker [--screen external] [--log]
```

States: `hint` `target` `picker` `picker-offline` `single` `unavailable` `sending` `queued` `queued-compact` `done` `failed` `refused` `receiving` `received` `interrupted`. `--log` prints every state change to stderr.

## Manual checklist

- [ ] Start dragging a file in Finder: the island shows the hint "移到这里发送到 书房台式机"
- [ ] Drag into the notch and release right away: sending → delivered, then it collapses after 1.5 s
- [ ] After the island opens, drag down into the "其他设备" strip: the device list opens; slide to another device and release to send to it
- [ ] Coming up from below to the default device: passing through the strip position does not open the device list by mistake
- [ ] Release over an offline device: shows "离线，未发送" and the file flies back
- [ ] Drag out of the island and release: the island collapses and the file flies back
- [ ] Default device offline (the other side quits): dragging in opens the device list right away
- [ ] With 1 paired device: a single large target; with 0 it shows "还没有配对设备"
- [ ] The receiver is busy receiving from another device: the queued state collapses to a small badge beside the camera after 3 s; when its turn comes it sends automatically
- [ ] The receiver is offline / has unpaired this device: "发送失败" shows the reason; retry works
- [ ] Another device sends files over: shows progress; when done the files can be dragged out, "在访达中显示" works, and it stays open while hovered
- [ ] The sender cancels midway: shows "传输中断" and the partial file is removed
- [ ] Menu bar → "配对新设备" → pick a device: both sides show the same 6-digit code; after both confirm, it appears under "已配对设备"
- [ ] Menu bar → a paired device → "解除配对": the other side is notified
- [ ] First launch: the welcome comes before the system's Local Network prompt; after "允许" the menu shows no warning
- [ ] Turn Wormhole Send off in System Settings › Privacy & Security › Local Network and restart the app: the icon gets an amber dot, the menu says "没有本地网络权限" and "打开“本地网络”设置…" opens that page; turning it back on clears the warning the next time the menu opens
- [ ] Drag on the external monitor: a floating capsule appears at the top of that screen
- [ ] While the island is showing, click menu bar items next to the notch: they still work (clicks pass through the transparent area)
- [ ] Switch to a full-screen app and drag: the island still appears
