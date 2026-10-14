# Wormhole Send for macOS: notch panel check

The notch panel on top of the Rust core: SwiftUI + AppKit, macOS 14+. The core is linked as a static library through core-ffi (`libwormhole.a`, header `core-ffi/include/wormhole.h`). Sending, receiving, the queue, interruptions and pairing all run through the real core. Pairing you start from the menu bar confirms the 6-digit code in a dialog; requests from other devices show on the island.

LocalSend devices (1.18+) can't pair, but files go both ways, confirmed by the receiver every time. When one sends here, the island asks: 拒绝 / 接收, or for a text message 忽略 / 复制 (copied to the clipboard). No answer within a minute declines it; the 拒绝 / 忽略 button counts down. LocalSend devices that are online appear in the picker after the paired ones, marked "LocalSend"; sending to one waits ("等待 … 接收…") until its user accepts. They never show under "配对新设备", and the default device is always a paired one.

## Running it

```bash
./scripts/bundle.sh          # builds the core (scripts/build-core.sh) and build/Wormhole Send.app
open "build/Wormhole Send.app"
./scripts/bundle.sh --install  # also copies it to /Applications, restarting the running app
```

The bundle is signed with `$WHS_SIGN_IDENTITY` if set, else the first valid "Apple Development" certificate in the keychain, else ad-hoc. With a certificate macOS knows the app by its bundle ID and team, so Full Disk Access and Local Network stay granted across rebuilds; an ad-hoc signature is new with every build, and both have to be granted again. This is for your own Mac: Gatekeeper still rejects the app elsewhere (that needs Developer ID and notarization).

The app icon is `Resources/AppIcon.icns`, rendered from `assets/app-icon.svg` by `assets/make-icons.sh` (which also writes the Windows `assets/app-icon.ico`; it needs Google Chrome to rasterize the SVG). Building the core needs Rust. `scripts/build-core.sh --universal` produces arm64 + x86_64 (first run `rustup target add aarch64-apple-darwin x86_64-apple-darwin`).

To test against `whs` on the same Mac, give the app its own data directory and port: `"build/Wormhole Send.app/Contents/MacOS/WormholeSend" --home /tmp/app --port 53340 --log`.

You can also open this directory in Xcode (it's a Swift Package) and run it from there.

Menu bar icon (a black hole: a ring with its accretion disk tilted across it, `Sources/WormholeSend/StatusIcon.swift`, source `assets/tray-icon.svg`): paired devices (set as default, unpair), pair a new device (nearby devices), open the receive folder (its path shows below on macOS 14.4+), change the receive folder, sounds on or off, language. Every row has an SF Symbol.

Sounds (design doc 2.5) are the app's own files, the same as on Windows: `assets/sounds/*.wav`, synthesized by `make-sounds.py` and copied into the bundle by `scripts/bundle.sh`. `sent.wav` when files are delivered, `received.wav` when files are received, `failed.wav` when a send fails or a receive breaks off, `pair-request.wav` for a pairing request; played at 60% volume.

The first launch shows a welcome that explains the Local Network prompt macOS 15+ shows next (to see it again: `defaults delete app.wormholesend.mac welcomed`). Without that permission the system silently drops discovery and outgoing transfers, so the app checks it at start and whenever the menu opens. When it is denied, the menu bar icon gets an amber dot, and the menu explains why and links to the setting.

## Languages

English, Chinese and Japanese, the same as the core and the Windows app. By default macOS picks the language from the user's preferred languages among the bundle's `en`, `zh-Hans` and `ja` folders (`Resources/*.lproj`, which also hold the Local Network prompt's text). The menu's 语言 item (跟随系统 / English / 简体中文 / 日本語, as on Windows) switches at once. It saves the choice as the app's `AppleLanguages`, the same setting as System Settings › General › Language & Region › Applications, so the two agree and macOS's own dialogs follow from the next launch. The core is started in the same language (`language` in the `wh_start` config), so its messages match. Every string has all three versions at the call site: `tr("中文", "English", "日本語")` (`L10n.swift`). One-line states widen to fit their text, up to 528 pt.

To check another language, add `-AppleLanguages "(ja)"` (or `en`, `zh-Hans`) to the launch arguments, e.g. with `--demo`.

## Demo mode (pinned to a single state with sample data; the core does not start)

```bash
"build/Wormhole Send.app/Contents/MacOS/WormholeSend" --demo picker [--screen external] [--log]
```

States: `hint` `target` `picker` `picker-offline` `picker-localsend` `single` `unavailable` `sending` `awaiting` `queued` `queued-compact` `done` `failed` `declined` `refused` `no-access` `receiving` `receiving-localsend` `received` `interrupted` `refused-repair` `pair-request` `pair-waiting` `receive-request` `receive-text` `notice` `notice-copied` `notice-failed`. `--log` prints every state change to stderr.

## Manual checklist

- [ ] Start dragging a file in Finder: the island shows the hint "移到这里发送到 书房台式机"
- [ ] Drag into the notch and release right away: sending → delivered (sent.wav plays), then it collapses after 1.5 s
- [ ] The target names what is dragged: "登记表.docx", "文件夹 设计稿", "登记表.docx 等 2 个文件", "设计稿 等 2 个文件夹", "… 等 3 个文件或文件夹"
- [ ] While sending a large file: "945 MB / 2.1 GB · 45 MB/s" under the bar; for a folder, "3 / 12 个文件 · 45 MB/s"; the speed shows after about a second
- [ ] After the island opens, drag down into the "其他设备" strip: the device list opens; slide to another device and release to send to it
- [ ] Coming up from below to the default device: passing through the strip position does not open the device list by mistake
- [ ] Release over an offline device: shows "离线，未发送" and the file flies back
- [ ] Drag out of the island and release: the island collapses and the file flies back
- [ ] Drag a file out of a WeChat chat without Full Disk Access: "读取 微信 的文件需要完全磁盘访问权限" (failed.wav plays), the file flies back and nothing is sent; "打开设置" opens Privacy & Security › Full Disk Access and selects the app in Finder; drag it into the list and switch it on, and the same drag sends. (WeChat drags out a path into its own container, which macOS 14+ keeps from other apps; an ad-hoc signed build has to be switched on again after each rebuild.)
- [ ] Default device offline (the other side quits): dragging in opens the device list right away
- [ ] With 1 paired device: a single large target; with 0 it shows "还没有配对设备"
- [ ] The receiver is busy receiving from another device: the queued state collapses to a small badge beside the camera after 3 s; when its turn comes it sends automatically
- [ ] The receiver is offline / has unpaired this device: "发送失败" shows the reason; retry works
- [ ] Another device sends files over: shows progress; when done the files can be dragged out, "在访达中显示" works, and it stays open while hovered
- [ ] The sender cancels midway: shows "传输中断" and the partial file is removed
- [ ] Menu bar → "配对新设备" → pick a device: both sides show the same 6-digit code; after both confirm, it appears under "已配对设备"
- [ ] Menu bar → a paired device → "解除配对": the other side is notified
- [ ] Menu bar → "更改接收文件夹…" → pick a folder: the next files received land there; during a transfer it is refused with a message
- [ ] Menu bar → "提示音" off: no sounds; on again: a sample plays
- [ ] Menu bar → "语言" → 日本語: the menu and the island switch at once, and a failed send's reason from the core is in Japanese too; after a restart it is still Japanese; "跟随系统" goes back
- [ ] First launch: the welcome comes before the system's Local Network prompt; after "允许" the menu shows no warning
- [ ] Turn Wormhole Send off in System Settings › Privacy & Security › Local Network and restart the app: the icon gets an amber dot, the menu says "没有本地网络权限" and "打开“本地网络”设置…" opens that page; turning it back on clears the warning the next time the menu opens
- [ ] LocalSend on a phone sends photos to this Mac: the island asks "… 想发送 IMG_… 等 N 个文件 · 大小" (pair-request.wav plays); 接收 shows the progress, then the files; 拒绝 tells the phone it was declined
- [ ] LocalSend sends a text: "… 发来一段文字" with the text; 复制 puts it on the clipboard and shows "已复制到剪贴板" with the start of the text; 忽略 closes it
- [ ] 拒绝 / 忽略 count down from 60 s ("拒绝 42s"); at 0 the island closes and the phone is told it was declined. The phone cancels while the island asks: "… 取消了发送"
- [ ] With LocalSend open on the phone, drag a file and go into the device list: the phone appears, marked "LocalSend"; release on it: "等待 … 接收…", then sending once accepted on the phone; declined on the phone: "发送失败：… 拒绝了接收", without 重试; the × cancels the wait
- [ ] "配对新设备" never lists LocalSend devices
- [ ] Drag on the external monitor: a floating capsule appears at the top of that screen
- [ ] While the island is showing, click menu bar items next to the notch: they still work (clicks pass through the transparent area)
- [ ] Switch to a full-screen app and drag: the island still appears
