# Wormhole Send

局域网文件传输：把文件拖到 Mac 刘海（或 Windows 屏幕顶部的岛）上，直接发给已配对的设备，对方不用确认。也能和手机、电脑上的 LocalSend 互传（每次由接收方确认）。设计见[设计文档](https://claude.ai/code/artifact/e854ce7e-9d13-434e-a150-eab89efc3285)。

当前进度：第 1 步（Rust 核心 + 命令行 `whs`）、第 2 步（macOS 刘海面板，见 [apps/macos](apps/macos/README.md)）、第 3 步（Windows 顶部岛，见 [apps/windows](apps/windows/README.md)）和第 4 步（配对与接收排队）已完成；第 5 步（两端界面接入核心）中 Windows 端已接入：顶部岛、托盘菜单、设置、配对和首次引导都走真实核心。

| 目录 | 内容 |
| --- | --- |
| `core/` | Rust 核心 `wormhole-core`：身份、信任库、发现、收发、控制服务（配对、解除配对、接收排队），基于 LocalSend 的 `localsend` crate |
| `core-ffi/` | 核心的 C 接口（`wormhole.dll` / `libwormhole.a` + `include/wormhole.h`），供两端原生界面调用，见 [core-ffi/README.md](core-ffi/README.md) |
| `cli/` | 命令行 `whs`，用来在原生界面之前验证核心 |
| `apps/macos/` | macOS 刘海面板验证原型（假数据） |
| `apps/windows/` | Windows 应用（WPF）：顶部岛、托盘、设置、配对、首次引导，接入核心 |

## 构建

- macOS：安装 [Rust](https://rustup.rs)，然后 `cargo build --release`，程序在 `target/release/whs`。
- Windows：安装 Rust 和 Visual Studio Build Tools（勾选“使用 C++ 的桌面开发”），先运行一次 `git config --global core.longpaths true`（localsend 依赖带的 flutter 子模块里有超过 260 字符的路径，不开会检出失败），然后 `cargo build --release`，程序在 `target\release\whs.exe`。

## 两台电脑互传

1. 接收方运行 `whs receive`。Windows 第一次运行会弹防火墙提示，选“允许专用网络”；macOS 可能询问本地网络权限。
2. 在另一台电脑上运行 `whs pair <对方名称>`（名称用 `whs devices` 查看）。两边都会显示同一个 6 位码，核对一致后各自输入 `y`。
3. 配对后，发送方运行 `whs send 报告.pdf 设计稿/`（发给默认设备）或 `whs send -t 台式机 报告.pdf`，对方不用确认。
4. 解除配对：`whs unpair <对方名称>`。对方在线会立即收到通知；不在线时，下次发现它时再通知。

接收方正在接收别的设备的文件时，新的发送会排队：发送方显示“排队中 · 前面还有 N 个”，轮到后自动发送。

## 和 LocalSend 互传

LocalSend 设备（1.18 及以上）不能配对，但可以直接互传，每次由接收方确认：

- LocalSend 发来文件时，`whs receive` 显示“XX 想发送 a.jpg 等 3 个文件”，输入 `y` 接收、`n` 拒绝，1 分钟不回答视为拒绝。发来的是一段文字时，接收后打印出来（刘海和顶部岛里是复制到剪贴板）。
- 发给 LocalSend 设备：`whs send -t <LocalSend 设备名> 文件…`，显示“等待对方接收”，对方在 LocalSend 里点接收后开始传输。
- `whs devices` 里 LocalSend 设备名后面带“（LocalSend）”。LocalSend 那边显示本机的设备型号为“Wormhole Send”。
- 对方开了 PIN 时暂不支持发送。

组播不通时（公司网络、开着 VPN），可以用 `whs trust add <指纹> --addr 192.168.1.20:53320` 手动添加地址（`trust` 命令只改本机，调试用）。

## 命令

| 命令 | 作用 |
| --- | --- |
| `whs id` | 本机名称、指纹、端口、保存位置 |
| `whs config [--alias 名称] [--save-dir 目录]` | 查看或修改设置 |
| `whs pair <设备>` | 与附近设备配对，双方核对 6 位码 |
| `whs unpair <设备>` | 解除配对并通知对方 |
| `whs trust list / add / remove / default` | 查看已配对设备；手动增删只改本机（调试用） |
| `whs devices [--wait 秒]` | 搜索附近设备 |
| `whs receive [--save-dir 目录]` | 持续接收已配对设备的文件，并处理配对请求和 LocalSend 的发送请求（输入 y / n），Ctrl+C 退出 |
| `whs send [-t 设备] 路径…` | 发送文件或文件夹（设备可以是附近的 LocalSend 设备），Ctrl+C 取消 |

全局参数：`--json` 按行输出 JSON 事件（字段与设计文档的事件表一致）；`--home 目录` 和 `--port 端口` 用于在一台电脑上模拟多台设备。

## 数据位置

- 端口：传输服务 53320，控制服务 53321（配对、排队），组播 53317（与 LocalSend 相同，这样双方能互相发现；和本机上的 LocalSend 共用不冲突）。被占用时临时命令会顺延到下一组端口。
- 数据目录：macOS 为 `~/Library/Application Support/WormholeSend`，Windows 为 `%APPDATA%\WormholeSend`。里面有 `identity.pem`（本机证书和私钥，删除后需要重新配对）、`settings.json`、`trust.json` 和 `pending.json`（还没送达的解除配对通知）。
- 收到的文件默认存到“下载/Wormhole Send”，可用 `whs config --save-dir` 修改。

## 与设计相比还没做的

- macOS 刘海面板接入核心，以及 macOS 的设置、配对界面。
- Windows 端：安装包（MSIX 还是 Inno Setup 待定）、私钥存入 DPAPI、双屏和 150% 缩放的实机验证，见 [apps/windows](apps/windows/README.md)。

## 致谢

传输层使用 [LocalSend](https://github.com/localsend/localsend) 的 Rust 核心（Apache-2.0）；`core/src/identity.rs` 和 `core/src/send.rs` 部分改编自 LocalSend CLI。
