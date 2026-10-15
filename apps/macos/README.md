# Wormhole Send for macOS

The notch panel on top of the Rust core: SwiftUI + AppKit, macOS 14+. The core is linked as a static library through core-ffi (`libwormhole.a`, header `core-ffi/include/wormhole.h`). Sending, receiving, the queue, interruptions and pairing all run through the real core. Pairing you start from the menu bar confirms the 6-digit code in a dialog; requests from other devices show on the island.

LocalSend devices (1.18+) can't pair, but files go both ways, confirmed by the receiver every time. When one sends here, the island asks: Decline / Accept, or for a text message Ignore / Copy (copied to the clipboard). No answer within a minute declines it; the Decline / Ignore button counts down. LocalSend devices that are online appear in the picker after the paired ones, marked "LocalSend"; sending to one waits ("Waiting for … to accept…") until its user accepts. They never show under "Pair New Device", and the default device is always a paired one.

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

Menu bar icon (a black hole: a ring with its accretion disk tilted across it, `Sources/WormholeSend/StatusIcon.swift`, source `assets/tray-icon.svg`): paired devices (set as default, unpair), pair a new device (nearby devices), open the receive folder (its path shows below on macOS 14.4+), change the receive folder, sounds on or off, language. Every row has an SF Symbol, except devices: they get the app's own device glyphs (`Sources/WormholeSend/DeviceGlyph.swift`, source `assets/device-icons.svg`), the same ones the island draws in cream on a deep-space plate, ringed in amber on the device a drop goes to.

Sounds are the app's own files, the same as on Windows: `assets/sounds/*.wav`, synthesized by `make-sounds.py` and copied into the bundle by `scripts/bundle.sh`. `sent.wav` when files are delivered, `received.wav` when files are received, `failed.wav` when a send fails or a receive breaks off, `pair-request.wav` for a pairing request; played at 60% volume.

The first launch shows a welcome that explains the Local Network prompt macOS 15+ shows next (to see it again: `defaults delete app.wormholesend.mac welcomed`). Without that permission the system silently drops discovery and outgoing transfers, so the app checks it at start and whenever the menu opens. When it is denied, the menu bar icon gets an amber dot, and the menu explains why and links to the setting.

## Languages

English, Chinese and Japanese, the same as the core and the Windows app. By default macOS picks the language from the user's preferred languages among the bundle's `en`, `zh-Hans` and `ja` folders (`Resources/*.lproj`, which also hold the Local Network prompt's text). The menu's Language item (System, English, Simplified Chinese, Japanese, as on Windows) switches at once. It saves the choice as the app's `AppleLanguages`, the same setting as System Settings › General › Language & Region › Applications, so the two agree and macOS's own dialogs follow from the next launch. The core is started in the same language (`language` in the `wh_start` config), so its messages match. Every string has all three versions at the call site: `tr(zh, en, ja)` (`L10n.swift`). One-line states widen to fit their text, up to 528 pt.

To check another language, add `-AppleLanguages "(ja)"` (or `en`, `zh-Hans`) to the launch arguments, e.g. with `--demo`.

## Demo mode (pinned to a single state with sample data; the core does not start)

```bash
"build/Wormhole Send.app/Contents/MacOS/WormholeSend" --demo picker [--screen external] [--log]
```

States: `hint` `target` `picker` `picker-offline` `picker-localsend` `single` `unavailable` `sending` `awaiting` `queued` `queued-compact` `done` `failed` `declined` `refused` `no-access` `receiving` `receiving-localsend` `received` `interrupted` `refused-repair` `pair-request` `pair-waiting` `receive-request` `receive-text` `notice` `notice-copied` `notice-failed`. `--log` prints every state change to stderr.

## Manual checklist

The strings below are the English interface.

- [ ] Start dragging a file in Finder: the island shows the hint "Move here to send to Study Desktop"
- [ ] Drag into the notch and release right away: sending → delivered (sent.wav plays), then it collapses after 1.5 s
- [ ] The target names what is dragged: "Form.docx", "Designs (folder)", "Form.docx and 1 more file", "Designs and 1 more folder", "… and 2 more items"
- [ ] Drag two files and a folder from VS Code's explorer: the hint shows and the island names all three; releasing sends them. (Apps built on Chromium give the paths as text, one per line, and at most one file URL; `--log` prints what a drag that isn't files carries.)
- [ ] While sending a large file: "945 MB / 2.1 GB · 45 MB/s" under the bar; for a folder, "3 / 12 files · 45 MB/s"; the speed shows after about a second
- [ ] After the island opens, drag down into the "Other devices" strip: the device list opens; slide to another device and release to send to it
- [ ] Coming up from below to the default device: passing through the strip position does not open the device list by mistake
- [ ] Release over an offline device: shows "… is offline, not sent" and the file flies back
- [ ] Drag out of the island and release: the island collapses and the file flies back
- [ ] Drag a file out of a WeChat chat without Full Disk Access: "Sending files from WeChat needs Full Disk Access" (failed.wav plays), the file flies back and nothing is sent; "Open Settings" opens Privacy & Security › Full Disk Access and selects the app in Finder; drag it into the list and switch it on, and the same drag sends. (WeChat drags out a path into its own container, which macOS 14+ keeps from other apps; an ad-hoc signed build has to be switched on again after each rebuild.)
- [ ] Default device offline (the other side quits): dragging in opens the device list right away
- [ ] With 1 paired device: a single large target; with 0 it shows "No paired devices yet"
- [ ] The receiver is busy receiving from another device: the queued state collapses to a small badge beside the camera after 3 s; when its turn comes it sends automatically
- [ ] The receiver is offline / has unpaired this device: "Couldn't send" shows the reason; retry works
- [ ] Another device sends files over: shows progress; when done the files can be dragged out, "Show in Finder" works, and it stays open while hovered
- [ ] The sender cancels midway: shows "Transfer interrupted" and the partial file is removed
- [ ] Menu bar → "Pair New Device" → pick a device: both sides show the same 6-digit code; after both confirm, it appears under "Paired Devices"
- [ ] Menu bar → a paired device → "Unpair…": the other side is notified
- [ ] Menu bar → "Change Receive Folder…" → pick a folder: the next files received land there; during a transfer it is refused with a message
- [ ] Menu bar → "Sounds" off: no sounds; on again: a sample plays
- [ ] Menu bar → "Language" → Japanese: the menu and the island switch at once, and a failed send's reason from the core is in Japanese too; after a restart it is still Japanese; "System" goes back
- [ ] First launch: the welcome comes before the system's Local Network prompt; after "Allow" the menu shows no warning
- [ ] Turn Wormhole Send off in System Settings › Privacy & Security › Local Network and restart the app: the icon gets an amber dot, the menu says "No Local Network access…" and "Open Local Network Settings…" opens that page; turning it back on clears the warning the next time the menu opens
- [ ] LocalSend on a phone sends photos to this Mac: the island asks "… wants to send IMG_… and N more files · size" (pair-request.wav plays); Accept shows the progress, then the files; Decline tells the phone it was declined
- [ ] LocalSend sends a text: "… sent a text message" with the text; Copy puts it on the clipboard and shows "Copied" with the start of the text; Ignore closes it
- [ ] Decline / Ignore count down from 60 s ("Decline 42s"); at 0 the island closes and the phone is told it was declined. The phone cancels while the island asks: "… cancelled sending"
- [ ] With LocalSend open on the phone, drag a file and go into the device list: the phone appears, marked "LocalSend"; release on it: "Waiting for … to accept…", then sending once accepted on the phone; declined on the phone: "Couldn't send: … declined", without Retry; the × cancels the wait
- [ ] "Pair New Device" never lists LocalSend devices
- [ ] Drag on the external monitor: a floating capsule appears at the top of that screen
- [ ] While the island is showing, click menu bar items next to the notch: they still work (clicks pass through the transparent area)
- [ ] Switch to a full-screen app and drag: the island still appears
