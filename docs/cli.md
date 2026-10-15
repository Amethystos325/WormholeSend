# The `whs` command line

`whs` is a command-line front end to the Wormhole Send core. It's handy for trying the core without a UI, for scripting, and for debugging connections. It shares its data directory with the desktop apps.

## Building

- **macOS**: install [Rust](https://rustup.rs), then run `cargo build --release`. The binary is `target/release/whs`.
- **Windows**: install Rust and Visual Studio Build Tools with "Desktop development with C++". Run `git config --global core.longpaths true` once first: a submodule pulled in by the `localsend` dependency has paths longer than 260 characters, and checkout fails without it. Then run `cargo build --release`. The binary is `target\release\whs.exe`.

## Sending between two computers

1. On the receiving computer, run `whs receive`. On first run, Windows shows a firewall prompt: allow private networks. macOS may ask for Local Network access.
2. On the other computer, run `whs pair <name>`. Use `whs devices` to see the names. Both sides show the same 6-digit code. Check that the codes match, then type `y` on each side.
3. Once paired, send with `whs send report.pdf designs/` (to the default device) or `whs send -t <name> report.pdf`. The receiver doesn't have to confirm.
4. To unpair, run `whs unpair <name>`. If the other device is online, it's told right away. Otherwise it's told the next time it's seen.

If the receiver is busy receiving from another device, a new send waits in a queue. The sender shows "Queued · N ahead" and sends automatically when its turn comes.

## Sending to and from LocalSend

LocalSend devices (1.18 or later) can't pair, but you can exchange files with them. The receiver accepts each transfer:

- When a LocalSend device sends files, `whs receive` asks whether to accept them. Type `y` to accept or `n` to decline. No answer within a minute counts as declining. A text message is printed once accepted. The desktop apps copy it to the clipboard instead.
- To send to a LocalSend device, run `whs send -t <LocalSend device name> files…`. It waits until the other side accepts in LocalSend, then transfers.
- `whs devices` marks LocalSend devices with "(LocalSend)". On the LocalSend side, this device's model shows as "Wormhole Send".
- Sending to a LocalSend device that requires a PIN isn't supported yet.

If multicast doesn't get through (corporate networks, VPNs), add an address by hand: `whs trust add <fingerprint> --addr 192.168.1.20:53320`. The `trust` commands only change this device, and are meant for debugging.

## Commands

| Command | What it does |
| --- | --- |
| `whs id` | Shows this device's name, fingerprint, port and receive folder |
| `whs config [--alias NAME] [--save-dir DIR]` | Shows or changes the settings |
| `whs pair <device>` | Pairs with a nearby device: both sides compare a 6-digit code |
| `whs unpair <device>` | Unpairs a device and tells it |
| `whs trust list / add / remove / default` | Lists paired devices. Adding and removing by hand only changes this device (for debugging) |
| `whs devices [--wait SECONDS]` | Searches for nearby devices |
| `whs receive [--save-dir DIR]` | Keeps receiving from paired devices and answers pairing requests and LocalSend requests (`y` / `n`). Ctrl+C quits |
| `whs send [-t DEVICE] PATH…` | Sends files or folders, to a paired device or a nearby LocalSend device. Ctrl+C cancels |

Global options:

- `--json` prints one JSON event per line. The fields are the same as the core's events, listed in [core-ffi/README.md](../core-ffi/README.md#events).
- `--home DIR` and `--port PORT` let you simulate several devices on one computer.

To debug connections, set `WHS_LOG=debug`. Logs from the core and the `localsend` crate then go to stderr, including every request received, every rejected connection and every TLS error.

## Ports and data

- **Ports**: the transfer service listens on TCP 53320 and the control service (pairing, the queue) on TCP 53321. Discovery uses UDP multicast on 53317, the same as LocalSend, so the two can find each other. Running alongside LocalSend on the same computer is fine. When a port is taken, one-off CLI commands move to the next free pair.
- **Data directory**: `~/Library/Application Support/WormholeSend` on macOS, `%APPDATA%\WormholeSend` on Windows. It holds:
  - `identity.pem`: this device's certificate and private key. Deleting it means pairing again.
  - `settings.json`
  - `trust.json`: paired devices.
  - `pending.json`: unpair notices that haven't been delivered yet.
- **Received files** go to `Downloads/Wormhole Send` by default. Change it with `whs config --save-dir`.
