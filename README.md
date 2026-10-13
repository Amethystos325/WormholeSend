# Wormhole Send

局域网文件传输：把文件拖到 Mac 刘海（或 Windows 屏幕顶部的岛）上，直接发给已配对的设备，对方不用确认。设计见[设计文档](https://claude.ai/code/artifact/e854ce7e-9d13-434e-a150-eab89efc3285)。

当前进度：第 1 步验证，Rust 核心 + 命令行 `whs`。

| 目录 | 内容 |
| --- | --- |
| `core/` | Rust 核心 `wormhole-core`：身份、信任库、发现、收发，基于 LocalSend 的 `localsend` crate |
| `cli/` | 命令行 `whs`，用来在原生界面之前验证核心 |

## 构建

- macOS：安装 [Rust](https://rustup.rs)，然后 `cargo build --release`，程序在 `target/release/whs`。
- Windows：安装 Rust 和 Visual Studio Build Tools（勾选“使用 C++ 的桌面开发”），然后 `cargo build --release`，程序在 `target\release\whs.exe`。

## 两台电脑互传

1. 两台都运行 `whs id`，记下各自的指纹。
2. 互相信任。在 Mac 上运行 `whs trust add <Windows 的指纹> --alias 台式机`，在 Windows 上运行 `whs trust add <Mac 的指纹> --alias MacBook`。对方正在运行 `whs receive` 时，也可以直接写名字：`whs trust add 台式机`。
3. 接收方运行 `whs receive`。Windows 第一次运行会弹防火墙提示，选“允许专用网络”；macOS 可能询问本地网络权限。
4. 发送方运行 `whs send 报告.pdf 设计稿/`（发给默认设备），或 `whs send -t 台式机 报告.pdf`。

验证未配对会被拒绝：在接收方运行 `whs trust remove MacBook`，再从 Mac 发一次，发送方会显示“被拒绝”（HTTP 403），接收方显示“已拒绝未配对设备”。

组播不通时（公司网络、开着 VPN），添加时带上地址：`whs trust add <指纹> --addr 192.168.1.20:53320`。

## 命令

| 命令 | 作用 |
| --- | --- |
| `whs id` | 本机名称、指纹、端口、保存位置 |
| `whs config [--alias 名称] [--save-dir 目录]` | 查看或修改设置 |
| `whs trust list / add / remove / default` | 管理已配对设备 |
| `whs devices [--wait 秒]` | 搜索附近设备 |
| `whs receive [--save-dir 目录]` | 持续接收已配对设备的文件，Ctrl+C 退出 |
| `whs send [-t 设备] 路径…` | 发送文件或文件夹，Ctrl+C 取消 |

全局参数：`--json` 按行输出 JSON 事件（字段与设计文档的事件表一致）；`--home 目录` 和 `--port 端口` 用于在一台电脑上模拟多台设备。

## 数据位置

- 数据目录：macOS 为 `~/Library/Application Support/WormholeSend`，Windows 为 `%APPDATA%\WormholeSend`。里面有 `identity.pem`（本机证书和私钥，删除后需要重新配对）、`settings.json` 和 `trust.json`。
- 收到的文件默认存到“下载/Wormhole Send”，可用 `whs config --save-dir` 修改。

## 与设计相比还没做的

- 配对目前是手动、单向的（`whs trust add`）。6 位码双向配对属于第 4 步的控制服务（端口 53321）。
- 对方正在接收别的设备的文件时（HTTP 409），目前直接显示“对方忙”。接收排队也在第 4 步。

## 致谢

传输层使用 [LocalSend](https://github.com/localsend/localsend) 的 Rust 核心（Apache-2.0）；`core/src/identity.rs` 和 `core/src/send.rs` 部分改编自 LocalSend CLI。
