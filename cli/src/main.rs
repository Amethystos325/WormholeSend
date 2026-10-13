//! `whs`: the Wormhole Send command line, for checking the core before the
//! native apps exist. Pairing is manual for now (`whs trust add`); the 6-digit
//! pairing comes with the control service.

use anyhow::{Context, bail};
use clap::{Parser, Subcommand};
use std::collections::HashMap;
use std::io::{IsTerminal, Write};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};
use wormhole_core::config::{self, Settings};
use wormhole_core::event::{CoreEvent, Direction, TransferStatus};
use wormhole_core::identity::Identity;
use wormhole_core::trust::{self, TrustStore, TrustedDevice};
use wormhole_core::{DeviceInfo, Node, NodeOptions};

#[derive(Parser)]
#[command(name = "whs", version, about = "Wormhole Send 命令行：在局域网内直接给已配对设备发文件")]
struct Cli {
    /// 数据目录（身份、设置、信任库），默认在系统应用数据目录下的 WormholeSend
    #[arg(long, global = true, env = "WHS_HOME")]
    home: Option<PathBuf>,

    /// 本次运行使用的传输端口（不保存）
    #[arg(long, global = true)]
    port: Option<u16>,

    /// 以 JSON 行输出（给程序读取）
    #[arg(long, global = true)]
    json: bool,

    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// 显示本机名称、指纹和设置
    Id,

    /// 查看或修改设置
    Config {
        /// 设备名称
        #[arg(long)]
        alias: Option<String>,

        /// 收到的文件保存到哪里
        #[arg(long)]
        save_dir: Option<PathBuf>,
    },

    /// 与附近的设备配对：双方核对同一个 6 位码
    Pair {
        /// 设备名称或指纹前缀（用 whs devices 查看）
        target: String,
    },

    /// 解除与一台设备的配对，并通知对方
    Unpair {
        /// 设备名称或指纹前缀
        target: String,
    },

    /// 手动管理信任列表（调试用；正常请用 pair / unpair）
    #[command(subcommand)]
    Trust(TrustCommand),

    /// 搜索局域网内的设备
    Devices {
        /// 搜索多少秒
        #[arg(long, default_value_t = 3)]
        wait: u64,
    },

    /// 持续运行，自动接收已配对设备发来的文件（Ctrl+C 退出）
    Receive {
        /// 本次运行的保存位置（不保存到设置）
        #[arg(long)]
        save_dir: Option<PathBuf>,
    },

    /// 把文件或文件夹发给已配对设备
    Send {
        /// 目标设备名称或指纹前缀；不填则发给默认设备
        #[arg(short, long)]
        to: Option<String>,

        /// 要发送的文件或文件夹
        #[arg(required = true)]
        paths: Vec<PathBuf>,
    },
}

#[derive(Subcommand)]
enum TrustCommand {
    /// 列出已配对设备
    List,

    /// 信任一台设备：完整指纹，或附近设备的名称 / 指纹前缀
    Add {
        target: String,

        /// 给设备起的名字（默认用对方自己的名字）
        #[arg(long)]
        alias: Option<String>,

        /// 设备地址 host:port，组播不通时用来直连
        #[arg(long)]
        addr: Option<String>,
    },

    /// 取消信任一台设备
    Remove { target: String },

    /// 设为默认设备
    Default { target: String },
}

#[tokio::main]
async fn main() {
    let cli = Cli::parse();
    match run(cli).await {
        Ok(code) => std::process::exit(code),
        Err(err) => {
            eprintln!("错误：{err:#}");
            std::process::exit(1);
        }
    }
}

async fn run(cli: Cli) -> anyhow::Result<i32> {
    let home = cli.home.clone().unwrap_or_else(config::default_home);
    let mut settings = Settings::load(&home)?;
    if let Some(port) = cli.port {
        settings.port = port;
    }
    let out = Printer::new(cli.json);
    match cli.command {
        Command::Id => show_id(&out, &home, &settings),
        Command::Config { alias, save_dir } => configure(&out, &home, settings, alias, save_dir),
        Command::Pair { target } => pair(out, home, settings, target).await,
        Command::Unpair { target } => unpair(&out, home, settings, target).await,
        Command::Trust(command) => trust_command(&out, &home, settings, command).await,
        Command::Devices { wait } => devices(&out, home, settings, wait).await,
        Command::Receive { save_dir } => {
            if let Some(dir) = save_dir {
                settings.save_dir = absolute(&dir)?;
            }
            receive(out, home, settings).await
        }
        Command::Send { to, paths } => send(out, home, settings, to, paths).await,
    }
}

fn show_id(out: &Printer, home: &Path, settings: &Settings) -> anyhow::Result<i32> {
    let identity = Identity::load_or_generate(home, settings.alias.clone(), settings.port)?;
    if out.json {
        out.json_line(&serde_json::json!({
            "alias": identity.alias,
            "fingerprint": identity.fingerprint,
            "port": settings.port,
            "saveDir": settings.save_dir,
            "home": home,
        }));
    } else {
        println!("设备名称  {}", identity.alias);
        println!("指纹      {}", identity.fingerprint);
        println!("传输端口  {}", settings.port);
        println!("保存位置  {}", settings.save_dir.display());
        println!("数据目录  {}", home.display());
    }
    Ok(0)
}

fn configure(
    out: &Printer,
    home: &Path,
    mut settings: Settings,
    alias: Option<String>,
    save_dir: Option<PathBuf>,
) -> anyhow::Result<i32> {
    let changed = alias.is_some() || save_dir.is_some();
    if let Some(alias) = alias {
        let alias = alias.trim().to_string();
        if alias.is_empty() {
            bail!("设备名称不能为空");
        }
        settings.alias = alias;
    }
    if let Some(dir) = save_dir {
        settings.save_dir = absolute(&dir)?;
    }
    if changed {
        // The port given with --port applies to this run only.
        let mut saved = settings.clone();
        saved.port = Settings::load(home)?.port;
        saved.save(home)?;
    }
    if out.json {
        out.json_line(&settings);
    } else {
        if changed {
            println!("设置已保存");
        }
        println!("设备名称  {}", settings.alias);
        println!("保存位置  {}", settings.save_dir.display());
    }
    Ok(0)
}

async fn trust_command(
    out: &Printer,
    home: &Path,
    settings: Settings,
    command: TrustCommand,
) -> anyhow::Result<i32> {
    match command {
        TrustCommand::List => {
            let store = TrustStore::load(home)?;
            if out.json {
                out.json_line(&store.devices());
            } else if store.devices().is_empty() {
                println!("还没有配对设备。用 whs pair 和附近的设备配对。");
            } else {
                for device in store.devices() {
                    let address = match (&device.last_host, device.last_port) {
                        (Some(host), Some(port)) => format!("{host}:{port}"),
                        _ => "地址未知".to_string(),
                    };
                    println!(
                        "{} {}  {}  {}{}",
                        if device.is_default { "★" } else { " " },
                        device.alias,
                        trust::short_fingerprint(&device.fingerprint),
                        address,
                        if device.needs_repair { "  （需要重新配对）" } else { "" }
                    );
                }
            }
            Ok(0)
        }
        TrustCommand::Add {
            target,
            alias,
            addr,
        } => {
            let address = addr.as_deref().map(parse_addr).transpose()?;
            let own = Identity::read_fingerprint(home)?;
            let mut device = if trust::is_full_fingerprint(&target) {
                TrustedDevice::new(&target, alias.clone().unwrap_or_else(|| "未命名设备".to_string()))
            } else {
                let found = discover(home, settings, 3).await?;
                let info = trust::resolve_device(&found, &target, |d| (&d.alias, &d.fingerprint))
                    .map_err(|err| anyhow::anyhow!("{err}（用 whs devices 查看附近设备）"))?;
                let mut device = TrustedDevice::new(&info.fingerprint, info.alias.clone());
                device.last_host = info.host.clone();
                device.last_port = info.port;
                device
            };
            if own.as_deref() == Some(device.fingerprint.as_str()) {
                bail!("这是本机自己的指纹");
            }
            if let Some(alias) = alias {
                device.alias = alias;
            }
            if let Some((host, port)) = address {
                device.last_host = Some(host);
                device.last_port = Some(port);
            }
            let fingerprint = device.fingerprint.clone();
            let alias = device.alias.clone();
            let is_default = TrustStore::update(home, |store| {
                store.add(device);
                (true, store.default_device().is_some_and(|d| d.fingerprint == fingerprint))
            })?;
            if out.json {
                out.json_line(&serde_json::json!({ "fingerprint": fingerprint, "alias": alias }));
            } else {
                println!(
                    "已信任 {alias}（{}）{}",
                    trust::short_fingerprint(&fingerprint),
                    if is_default { "，并设为默认设备" } else { "" }
                );
                println!("提示：对方也要信任本机，才能把文件发过来。");
            }
            Ok(0)
        }
        TrustCommand::Remove { target } => {
            let removed = TrustStore::update(home, |store| match store.resolve(&target) {
                Ok(device) => {
                    let fingerprint = device.fingerprint.clone();
                    (true, Ok(store.remove(&fingerprint)))
                }
                Err(err) => (false, Err(err)),
            })?
            .map_err(anyhow::Error::msg)?;
            if let Some(device) = removed {
                out.say(&format!("已取消信任 {}", device.alias));
            }
            Ok(0)
        }
        TrustCommand::Default { target } => {
            let alias = TrustStore::update(home, |store| match store.resolve(&target) {
                Ok(device) => {
                    let (fingerprint, alias) = (device.fingerprint.clone(), device.alias.clone());
                    (store.set_default(&fingerprint), Ok(alias))
                }
                Err(err) => (false, Err(err)),
            })?
            .map_err(anyhow::Error::msg)?;
            out.say(&format!("默认设备：{alias}"));
            Ok(0)
        }
    }
}

/// Runs a short-lived node for `wait` seconds and returns what it found.
async fn discover(home: &Path, settings: Settings, wait: u64) -> anyhow::Result<Vec<DeviceInfo>> {
    let (node, _events) = Node::start(NodeOptions {
        home: home.to_path_buf(),
        settings,
        port_fallback: true,
    })
    .await?;
    let deadline = Instant::now() + Duration::from_secs(wait);
    node.refresh().await;
    tokio::time::sleep(deadline.saturating_duration_since(Instant::now())).await;
    let devices = node.devices();
    node.stop().await;
    Ok(devices)
}

async fn devices(out: &Printer, home: PathBuf, settings: Settings, wait: u64) -> anyhow::Result<i32> {
    if !out.json {
        eprintln!("正在搜索附近设备（{wait} 秒）…");
    }
    let devices = discover(&home, settings, wait).await?;
    if out.json {
        out.json_line(&devices);
        return Ok(0);
    }
    if devices.is_empty() {
        println!("没有找到设备。确认对方正在运行 whs receive，并且在同一个网络。");
    }
    for device in &devices {
        let mark = match (device.is_default, device.paired) {
            (true, _) => "★",
            (false, true) => "✓",
            (false, false) => " ",
        };
        let address = match (&device.host, device.port) {
            (Some(host), Some(port)) => format!("{host}:{port}"),
            _ => String::new(),
        };
        println!(
            "{mark} {}  {}  {}  {address}{}",
            device.alias,
            if device.online { "在线" } else { "离线" },
            trust::short_fingerprint(&device.fingerprint),
            if device.needs_repair { "  （需要重新配对）" } else { "" }
        );
    }
    if !devices.is_empty() {
        println!("★ 默认设备  ✓ 已配对");
    }
    Ok(0)
}

async fn receive(mut out: Printer, home: PathBuf, settings: Settings) -> anyhow::Result<i32> {
    let mut interrupt = Interrupt::new()?;
    let save_dir = settings.save_dir.clone();
    let (node, mut events) = Node::start(NodeOptions {
        home,
        settings,
        port_fallback: false,
    })
    .await?;
    if !out.json {
        let identity = node.identity();
        println!(
            "{}（{}）正在接收，端口 {}，保存到 {}",
            identity.alias,
            trust::short_fingerprint(&identity.fingerprint),
            identity.port,
            save_dir.display()
        );
        println!("只接收已配对设备的文件。按 Ctrl+C 退出。");
    }
    // A pairing whose code waits for y/n on stdin.
    let mut awaiting: Option<String> = None;
    let mut lines = stdin_lines();
    let mut stdin_open = true;
    loop {
        tokio::select! {
            event = events.recv() => {
                let Some(event) = event else { break };
                out.event(&event, true);
                match &event {
                    CoreEvent::PairCode { pair_id, .. } => {
                        awaiting = Some(pair_id.clone());
                        if !out.json {
                            println!("输入 y 确认配对，n 拒绝：");
                        }
                    }
                    CoreEvent::PairResult { pair_id, .. } if awaiting.as_ref() == Some(pair_id) => awaiting = None,
                    _ => {}
                }
            }
            line = lines.recv(), if stdin_open => match line {
                Some(line) => {
                    if let Some(pair_id) = awaiting.take()
                        && let Err(err) = node.pair_confirm(&pair_id, is_yes(&line))
                    {
                        out.say(&format!("{err:#}"));
                    }
                }
                None => stdin_open = false,
            },
            _ = interrupt.recv() => break,
        }
    }
    node.stop().await;
    Ok(0)
}

async fn send(
    mut out: Printer,
    home: PathBuf,
    settings: Settings,
    to: Option<String>,
    paths: Vec<PathBuf>,
) -> anyhow::Result<i32> {
    let mut interrupt = Interrupt::new()?;
    for path in &paths {
        if !path.exists() {
            bail!("找不到 {}", path.display());
        }
    }
    let store = TrustStore::load(&home)?;
    let target = match &to {
        Some(query) => store.resolve(query).map_err(anyhow::Error::msg)?,
        None => store
            .default_device()
            .context("还没有配对设备。先用 whs trust add 添加")?,
    };
    let fingerprint = target.fingerprint.clone();

    let (node, mut events) = Node::start(NodeOptions {
        home,
        settings,
        port_fallback: true,
    })
    .await?;
    let transfer_id = node.send(&fingerprint, paths);
    let mut cancelled = false;
    let code = loop {
        tokio::select! {
            event = events.recv() => {
                let Some(event) = event else { break 1 };
                out.event(&event, false);
                if let CoreEvent::TransferFinished { id, status, .. } = &event
                    && *id == transfer_id
                {
                    break if *status == TransferStatus::Done { 0 } else { 1 };
                }
            }
            _ = interrupt.recv(), if !cancelled => {
                cancelled = true;
                node.cancel(&transfer_id);
            }
        }
    };
    node.stop().await;
    Ok(code)
}

/// Ctrl+C, listened to from creation on. `tokio::signal::ctrl_c()` only
/// installs its handler when first polled, so a Ctrl+C during startup would
/// be lost (or, in a background job that starts with SIGINT ignored, ignored).
struct Interrupt {
    #[cfg(unix)]
    signal: tokio::signal::unix::Signal,
    #[cfg(windows)]
    signal: tokio::signal::windows::CtrlC,
}

impl Interrupt {
    fn new() -> anyhow::Result<Self> {
        #[cfg(unix)]
        let signal = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::interrupt())?;
        #[cfg(windows)]
        let signal = tokio::signal::windows::ctrl_c()?;
        Ok(Self { signal })
    }

    async fn recv(&mut self) {
        self.signal.recv().await;
    }
}

async fn pair(mut out: Printer, home: PathBuf, settings: Settings, target: String) -> anyhow::Result<i32> {
    let mut interrupt = Interrupt::new()?;
    let (node, mut events) = Node::start(NodeOptions {
        home,
        settings,
        port_fallback: true,
    })
    .await?;
    if !out.json {
        eprintln!("正在搜索附近设备…");
    }
    let deadline = Instant::now() + Duration::from_secs(3);
    node.refresh().await;
    tokio::time::sleep(deadline.saturating_duration_since(Instant::now())).await;
    let devices = node.devices();
    let started = match trust::resolve_device(&devices, &target, |d| (&d.alias, &d.fingerprint)) {
        Ok(device) => node.pair(&device.fingerprint),
        Err(err) => Err(anyhow::anyhow!("{err}（用 whs devices 查看附近设备）")),
    };
    let pair_id = match started {
        Ok(pair_id) => pair_id,
        Err(err) => {
            node.stop().await;
            return Err(err);
        }
    };
    let mut lines = stdin_lines();
    let mut asking = false;
    let code = loop {
        tokio::select! {
            event = events.recv() => {
                let Some(event) = event else { break 1 };
                match &event {
                    CoreEvent::PairCode { pair_id: id, .. } if *id == pair_id => {
                        out.event(&event, false);
                        asking = true;
                        if !out.json {
                            println!("两边的数字一致吗？输入 y 确认，n 拒绝：");
                        }
                    }
                    CoreEvent::PairResult { pair_id: id, ok, .. } if *id == pair_id => {
                        out.event(&event, false);
                        break if *ok { 0 } else { 1 };
                    }
                    CoreEvent::Error { .. } => out.event(&event, false),
                    _ => {}
                }
            }
            line = lines.recv(), if asking => {
                asking = false;
                let accept = line.as_deref().is_some_and(is_yes);
                node.pair_confirm(&pair_id, accept)?;
                if accept && !out.json {
                    println!("等待对方确认…");
                }
            }
            _ = interrupt.recv() => {
                let _ = node.pair_confirm(&pair_id, false);
                break 1;
            }
        }
    };
    node.stop().await;
    Ok(code)
}

async fn unpair(out: &Printer, home: PathBuf, settings: Settings, target: String) -> anyhow::Result<i32> {
    let device = TrustStore::load(&home)?
        .resolve(&target)
        .map_err(anyhow::Error::msg)?
        .clone();
    let (node, _events) = Node::start(NodeOptions {
        home,
        settings,
        port_fallback: true,
    })
    .await?;
    // Learn the device's current address before telling it.
    node.refresh().await;
    let result = node.unpair(&device.fingerprint).await;
    node.stop().await;
    let message = match result? {
        Some(true) => format!("已解除与 {} 的配对，并已通知对方", device.alias),
        Some(false) => format!("已解除与 {} 的配对；对方现在不在线，下次发现它时会通知", device.alias),
        None => format!("{} 没有配对", device.alias),
    };
    out.say(&message);
    Ok(0)
}

/// Lines typed on stdin, read on a separate thread.
fn stdin_lines() -> tokio::sync::mpsc::UnboundedReceiver<String> {
    let (tx, rx) = tokio::sync::mpsc::unbounded_channel();
    std::thread::spawn(move || {
        for line in std::io::stdin().lines() {
            let Ok(line) = line else { break };
            if tx.send(line).is_err() {
                break;
            }
        }
    });
    rx
}

fn is_yes(line: &str) -> bool {
    matches!(line.trim().to_lowercase().as_str(), "y" | "yes" | "是")
}

fn format_code(code: &str) -> String {
    match code.len() {
        6 => format!("{} {}", &code[..3], &code[3..]),
        _ => code.to_string(),
    }
}

fn parse_addr(addr: &str) -> anyhow::Result<(String, u16)> {
    let (host, port) = addr
        .rsplit_once(':')
        .context("地址格式应为 host:port，例如 192.168.1.20:53320")?;
    let port = port.parse().context("端口必须是数字")?;
    Ok((host.trim_matches(['[', ']']).to_string(), port))
}

fn absolute(path: &Path) -> anyhow::Result<PathBuf> {
    Ok(match path.is_absolute() {
        true => path.to_path_buf(),
        false => std::env::current_dir()?.join(path),
    })
}

/// Prints events as text lines with a live progress line, or as JSON lines.
struct Printer {
    json: bool,
    progress_line: bool,
    started: HashMap<String, Instant>,
}

impl Printer {
    fn new(json: bool) -> Self {
        Self {
            json,
            progress_line: false,
            started: HashMap::new(),
        }
    }

    fn json_line(&self, value: &impl serde::Serialize) {
        println!("{}", serde_json::to_string(value).unwrap_or_default());
    }

    fn say(&self, text: &str) {
        if self.json {
            self.json_line(&serde_json::json!({ "message": text }));
        } else {
            println!("{text}");
        }
    }

    fn line(&mut self, text: &str) {
        if self.progress_line {
            eprintln!();
            self.progress_line = false;
        }
        println!("{text}");
    }

    fn progress(&mut self, text: &str) {
        if std::io::stderr().is_terminal() {
            eprint!("\r\x1b[2K{text}");
            let _ = std::io::stderr().flush();
            self.progress_line = true;
        }
    }

    /// `devices`: whether device events are worth a line.
    fn event(&mut self, event: &CoreEvent, devices: bool) {
        if self.json {
            self.json_line(event);
            return;
        }
        match event {
            CoreEvent::DeviceUpdated {
                id,
                alias,
                online,
                paired,
                ..
            } => {
                if !devices {
                    return;
                }
                let fp = trust::short_fingerprint(id);
                let text = match (paired, online) {
                    (true, true) => format!("已配对设备在线：{alias}（{fp}）"),
                    (true, false) => format!("已配对设备离线：{alias}（{fp}）"),
                    (false, _) => format!("发现未配对设备：{alias}（{fp}）"),
                };
                self.line(&text);
            }
            CoreEvent::TransferStarted {
                id,
                direction,
                alias,
                files,
                total,
                ..
            } => {
                self.started.insert(id.clone(), Instant::now());
                let text = match direction {
                    Direction::Send => {
                        format!("→ 正在发送 {files} 个文件（{}）到 {alias}", format_bytes(*total))
                    }
                    Direction::Receive => {
                        format!("← 正在接收 {alias} 发来的 {files} 个文件（{}）", format_bytes(*total))
                    }
                };
                self.line(&text);
            }
            CoreEvent::TransferProgress {
                id, bytes, total, ..
            } => {
                let percent = match *total {
                    0 => 100,
                    total => bytes * 100 / total,
                };
                let speed = self
                    .started
                    .get(id)
                    .map(|started| started.elapsed().as_secs_f64())
                    .filter(|secs| *secs > 0.2)
                    .map(|secs| format!("  {}/s", format_bytes((*bytes as f64 / secs) as u64)))
                    .unwrap_or_default();
                self.progress(&format!(
                    "  {percent:>3}%  {} / {}{speed}",
                    format_bytes(*bytes),
                    format_bytes(*total)
                ));
            }
            CoreEvent::TransferFinished {
                id,
                direction,
                alias,
                status,
                files_done,
                bytes,
                saved_paths,
                error,
                ..
            } => {
                let took = self
                    .started
                    .remove(id)
                    .map(|started| format!("，用时 {:.1} 秒", started.elapsed().as_secs_f64()))
                    .unwrap_or_default();
                let text = match (status, direction) {
                    (TransferStatus::Done, Direction::Send) => format!(
                        "✓ 已发送 {files_done} 个文件到 {alias}（{}{took}）",
                        format_bytes(*bytes)
                    ),
                    (TransferStatus::Done, Direction::Receive) => {
                        let saved = saved_paths
                            .iter()
                            .map(|path| path.display().to_string())
                            .collect::<Vec<_>>()
                            .join("、");
                        format!(
                            "✓ 已收到 {alias} 的 {files_done} 个文件（{}{took}），保存为 {saved}",
                            format_bytes(*bytes)
                        )
                    }
                    (status, _) => {
                        let label = match status {
                            TransferStatus::Cancelled => "已取消",
                            TransferStatus::Rejected => "被拒绝",
                            TransferStatus::Busy => "对方忙",
                            TransferStatus::Offline => "设备离线",
                            _ => "失败",
                        };
                        match error {
                            Some(error) => format!("✗ {label}：{error}"),
                            None => format!("✗ {label}"),
                        }
                    }
                };
                self.line(&text);
            }
            CoreEvent::ReceiveRejected {
                device_id,
                alias,
                ip,
            } => {
                let fp = trust::short_fingerprint(device_id);
                self.line(&format!("✗ 已拒绝未配对设备 {alias}（{fp}，{ip}）的发送请求"));
            }
            CoreEvent::TransferQueued { position, .. } => {
                self.line(&format!("⏳ 对方正在接收其他设备的文件，排队中 · 前面还有 {position} 个"));
            }
            CoreEvent::ReceiveWaiting { count } => match count {
                0 => self.line("排队已清空"),
                count => self.line(&format!("还有 {count} 个设备在排队")),
            },
            CoreEvent::PairIncoming { device_id, alias, .. } => {
                let fp = trust::short_fingerprint(device_id);
                self.line(&format!("{alias}（{fp}）请求配对"));
            }
            CoreEvent::PairCode { alias, code, .. } => {
                self.line(&format!("配对码：{}　请和 {alias} 屏幕上的数字核对", format_code(code)));
            }
            CoreEvent::PairResult { alias, ok, reason, .. } => match ok {
                true => self.line(&format!("✓ 已与 {alias} 配对，之后互传文件不用确认")),
                false => self.line(&format!("✗ 配对失败：{}", reason.as_deref().unwrap_or("未知原因"))),
            },
            CoreEvent::DeviceUnpaired { alias, .. } => {
                self.line(&format!("{alias} 解除了与本机的配对"));
            }
            CoreEvent::Error { message } => self.line(&format!("! {message}")),
        }
    }
}

fn format_bytes(bytes: u64) -> String {
    const UNITS: [&str; 4] = ["B", "KB", "MB", "GB"];
    let mut value = bytes as f64;
    let mut unit = 0;
    while value >= 1024.0 && unit < UNITS.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    match unit {
        0 => format!("{bytes} B"),
        _ => format!("{value:.1} {}", UNITS[unit]),
    }
}
