//! `whs`: the Wormhole Send command line, for trying the core without the
//! native apps and for debugging connections.

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
use wormhole_core::{DeviceInfo, Kind, Node, NodeOptions};

#[derive(Parser)]
#[command(name = "whs", version, about = "Wormhole Send command line: send files straight to paired devices on the local network, and to and from LocalSend")]
struct Cli {
    /// Data folder (identity, settings, trust store); by default WormholeSend in the system's app data folder
    #[arg(long, global = true, env = "WHS_HOME")]
    home: Option<PathBuf>,

    /// Transfer port for this run (not saved)
    #[arg(long, global = true)]
    port: Option<u16>,

    /// Print JSON lines (for programs to read)
    #[arg(long, global = true)]
    json: bool,

    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// Show this device's name, fingerprint and settings
    Id,

    /// Show or change the settings
    Config {
        /// Device name
        #[arg(long)]
        alias: Option<String>,

        /// Where received files are saved
        #[arg(long)]
        save_dir: Option<PathBuf>,
    },

    /// Pair with a nearby device: both sides check the same 6-digit code
    Pair {
        /// Device name or fingerprint prefix (see whs devices)
        target: String,
    },

    /// Unpair a device and tell it
    Unpair {
        /// Device name or fingerprint prefix
        target: String,
    },

    /// Manage the trust list by hand (for debugging; normally use pair / unpair)
    #[command(subcommand)]
    Trust(TrustCommand),

    /// Search for devices on the local network
    Devices {
        /// How many seconds to search
        #[arg(long, default_value_t = 3)]
        wait: u64,
    },

    /// Keep running: receive files from paired devices automatically, ask when a LocalSend device sends (Ctrl+C to quit)
    Receive {
        /// Save folder for this run (not saved to the settings)
        #[arg(long)]
        save_dir: Option<PathBuf>,
    },

    /// Send files or folders to a paired device, or to a nearby LocalSend device (received once its user accepts)
    Send {
        /// Target device name or fingerprint prefix; the default device if left out
        #[arg(short, long)]
        to: Option<String>,

        /// Files or folders to send
        #[arg(required = true)]
        paths: Vec<PathBuf>,
    },
}

#[derive(Subcommand)]
enum TrustCommand {
    /// List paired devices
    List,

    /// Trust a device: a full fingerprint, or a nearby device's name / fingerprint prefix
    Add {
        target: String,

        /// Name for the device (by default its own name)
        #[arg(long)]
        alias: Option<String>,

        /// Device address host:port, to connect directly when multicast doesn't work
        #[arg(long)]
        addr: Option<String>,
    },

    /// Stop trusting a device
    Remove { target: String },

    /// Make it the default device
    Default { target: String },
}

#[tokio::main]
async fn main() {
    // For debugging: WHS_LOG=debug (or a filter such as
    // "localsend=debug,wormhole_core=debug") prints the core's and the
    // localsend crate's logs to stderr.
    if let Ok(filter) = std::env::var("WHS_LOG") {
        tracing_subscriber::fmt()
            .with_env_filter(tracing_subscriber::EnvFilter::new(filter))
            .with_writer(std::io::stderr)
            .init();
    }
    let cli = Cli::parse();
    match run(cli).await {
        Ok(code) => std::process::exit(code),
        Err(err) => {
            eprintln!("Error: {err:#}");
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
        println!("Device name    {}", identity.alias);
        println!("Fingerprint    {}", identity.fingerprint);
        println!("Transfer port  {}", settings.port);
        println!("Save folder    {}", settings.save_dir.display());
        println!("Data folder    {}", home.display());
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
            bail!("The device name can't be empty");
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
            println!("Settings saved");
        }
        println!("Device name  {}", settings.alias);
        println!("Save folder  {}", settings.save_dir.display());
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
                println!("No paired devices yet. Use whs pair to pair with a nearby device.");
            } else {
                for device in store.devices() {
                    let address = match (&device.last_host, device.last_port) {
                        (Some(host), Some(port)) => format!("{host}:{port}"),
                        _ => "address unknown".to_string(),
                    };
                    println!(
                        "{} {}  {}  {}{}",
                        if device.is_default { "★" } else { " " },
                        device.alias,
                        trust::short_fingerprint(&device.fingerprint),
                        address,
                        if device.needs_repair { "  (needs pairing again)" } else { "" }
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
                TrustedDevice::new(&target, alias.clone().unwrap_or_else(|| "Unnamed device".to_string()))
            } else {
                let found = discover(home, settings, 3).await?;
                let info = trust::resolve_device(&found, &target, |d| (&d.alias, &d.fingerprint))
                    .map_err(|err| anyhow::anyhow!("{err} (see nearby devices with whs devices)"))?;
                let mut device = TrustedDevice::new(&info.fingerprint, info.alias.clone());
                device.last_host = info.host.clone();
                device.last_port = info.port;
                device
            };
            if own.as_deref() == Some(device.fingerprint.as_str()) {
                bail!("That is this device's own fingerprint");
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
                    "Trusted {alias} ({}){}",
                    trust::short_fingerprint(&fingerprint),
                    if is_default { ", and made it the default device" } else { "" }
                );
                println!("Note: the other device has to trust this one too before it can send files here.");
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
                out.say(&format!("No longer trusting {}", device.alias));
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
            out.say(&format!("Default device: {alias}"));
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
        eprintln!("Searching for nearby devices ({wait} s)…");
    }
    let devices = discover(&home, settings, wait).await?;
    if out.json {
        out.json_line(&devices);
        return Ok(0);
    }
    if devices.is_empty() {
        println!("No devices found. Make sure the other device is running whs receive and is on the same network.");
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
            "{mark} {}{}  {}  {}  {address}{}",
            device.alias,
            if device.kind == Kind::LocalSend { " (LocalSend)" } else { "" },
            if device.online { "online" } else { "offline" },
            trust::short_fingerprint(&device.fingerprint),
            if device.needs_repair { "  (needs pairing again)" } else { "" }
        );
    }
    // Explain only the marks that appear.
    let mut legend = Vec::new();
    if devices.iter().any(|device| device.is_default) {
        legend.push("★ default device");
    }
    if devices.iter().any(|device| device.paired && !device.is_default) {
        legend.push("✓ paired");
    }
    if !legend.is_empty() {
        println!("{}", legend.join("  "));
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
            "{} ({}) is receiving on port {}, saving to {}",
            identity.alias,
            trust::short_fingerprint(&identity.fingerprint),
            identity.port,
            save_dir.display()
        );
        println!("Files from paired devices are received automatically; you're asked when a LocalSend device sends. Press Ctrl+C to quit.");
    }
    // A pairing code or a LocalSend request waiting for y/n on stdin.
    let mut awaiting: Option<Awaiting> = None;
    let mut lines = stdin_lines();
    let mut stdin_open = true;
    loop {
        tokio::select! {
            event = events.recv() => {
                let Some(event) = event else { break };
                out.event(&event, true);
                match &event {
                    CoreEvent::PairCode { pair_id, .. } => {
                        awaiting = Some(Awaiting::Pair(pair_id.clone()));
                        if !out.json {
                            println!("Type y to confirm the pairing, n to decline:");
                        }
                    }
                    CoreEvent::ReceiveRequest { request_id, text, .. } => {
                        awaiting = Some(Awaiting::Receive(request_id.clone(), text.clone()));
                        if !out.json {
                            println!("Type y to receive, n to decline (no answer within 1 minute declines):");
                        }
                    }
                    CoreEvent::PairResult { pair_id: id, .. } | CoreEvent::ReceiveClosed { request_id: id, .. }
                        if awaiting.as_ref().is_some_and(|a| a.id() == id) =>
                    {
                        awaiting = None
                    }
                    _ => {}
                }
            }
            line = lines.recv(), if stdin_open => match line {
                Some(line) => match awaiting.take() {
                    Some(Awaiting::Pair(pair_id)) => {
                        if let Err(err) = node.pair_confirm(&pair_id, is_yes(&line)) {
                            out.say(&format!("{err:#}"));
                        }
                    }
                    Some(Awaiting::Receive(request_id, text)) => {
                        let accept = is_yes(&line);
                        if !node.respond_receive(&request_id, accept) {
                            out.say("This request has already ended");
                        } else if accept && let Some(text) = text {
                            out.say(&format!("Received text:\n{text}"));
                        }
                    }
                    None => {}
                },
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
            bail!("Can't find {}", path.display());
        }
    }
    let store = TrustStore::load(&home)?;
    let paired = match &to {
        Some(query) => store.resolve(query).ok(),
        None => Some(
            store
                .default_device()
                .context("No paired devices yet. Pair with whs pair first, or name a nearby LocalSend device with --to")?,
        ),
    };
    let paired = paired.map(|device| device.fingerprint.clone());

    let (node, mut events) = Node::start(NodeOptions {
        home,
        settings,
        port_fallback: true,
    })
    .await?;
    let fingerprint = match (paired, &to) {
        (Some(fingerprint), _) => fingerprint,
        // Not paired: a LocalSend device nearby.
        (None, Some(query)) => {
            if !out.json {
                eprintln!("Searching for nearby LocalSend devices…");
            }
            let deadline = Instant::now() + Duration::from_secs(3);
            node.refresh().await;
            tokio::time::sleep(deadline.saturating_duration_since(Instant::now())).await;
            let found: Vec<DeviceInfo> = node
                .devices()
                .into_iter()
                .filter(|device| device.kind == Kind::LocalSend && device.online)
                .collect();
            match trust::resolve_device(&found, query, |d| (&d.alias, &d.fingerprint)) {
                Ok(device) => device.fingerprint.clone(),
                Err(err) => {
                    node.stop().await;
                    bail!("{err} (not a paired device, and no such LocalSend device nearby)");
                }
            }
        }
        (None, None) => unreachable!(),
    };
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

/// What a line typed on stdin answers.
enum Awaiting {
    Pair(String),
    /// A LocalSend request, with the text of a text message.
    Receive(String, Option<String>),
}

impl Awaiting {
    fn id(&self) -> &String {
        match self {
            Awaiting::Pair(id) | Awaiting::Receive(id, _) => id,
        }
    }
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
        eprintln!("Searching for nearby devices…");
    }
    let deadline = Instant::now() + Duration::from_secs(3);
    node.refresh().await;
    tokio::time::sleep(deadline.saturating_duration_since(Instant::now())).await;
    let devices = node.devices();
    let started = match trust::resolve_device(&devices, &target, |d| (&d.alias, &d.fingerprint)) {
        Ok(device) => node.pair(&device.fingerprint),
        Err(err) => Err(anyhow::anyhow!("{err} (see nearby devices with whs devices)")),
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
                            println!("Do the numbers match on both devices? Type y to confirm, n to decline:");
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
                    println!("Waiting for the other device to confirm…");
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
        Some(true) => format!("Unpaired {} and told it", device.alias),
        Some(false) => format!("Unpaired {}; it's offline now and will be told the next time it's found", device.alias),
        None => format!("{} isn't paired", device.alias),
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

/// "y" or "yes", in any case.
fn is_yes(line: &str) -> bool {
    matches!(line.trim().to_lowercase().as_str(), "y" | "yes")
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
        .context("The address should be host:port, e.g. 192.168.1.20:53320")?;
    let port = port.parse().context("The port must be a number")?;
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
                kind,
                online,
                paired,
                ..
            } => {
                if !devices {
                    return;
                }
                let fp = trust::short_fingerprint(id);
                let text = match (paired, kind, online) {
                    (true, _, true) => format!("Paired device online: {alias} ({fp})"),
                    (true, _, false) => format!("Paired device offline: {alias} ({fp})"),
                    (false, Kind::LocalSend, true) => format!("LocalSend device online: {alias} ({fp})"),
                    (false, Kind::LocalSend, false) => format!("LocalSend device offline: {alias} ({fp})"),
                    (false, Kind::Wormhole, _) => format!("Unpaired device found: {alias} ({fp})"),
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
                        format!("→ Sending {} ({}) to {alias}", count_files(*files), format_bytes(*total))
                    }
                    Direction::Receive => {
                        format!("← Receiving {} ({}) from {alias}", count_files(*files), format_bytes(*total))
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
                    .map(|started| format!(", in {:.1} s", started.elapsed().as_secs_f64()))
                    .unwrap_or_default();
                let text = match (status, direction) {
                    (TransferStatus::Done, Direction::Send) => format!(
                        "✓ Sent {} to {alias} ({}{took})",
                        count_files(*files_done),
                        format_bytes(*bytes)
                    ),
                    (TransferStatus::Done, Direction::Receive) => {
                        let saved = saved_paths
                            .iter()
                            .map(|path| path.display().to_string())
                            .collect::<Vec<_>>()
                            .join(", ");
                        format!(
                            "✓ Received {} from {alias} ({}{took}), saved as {saved}",
                            count_files(*files_done),
                            format_bytes(*bytes)
                        )
                    }
                    (status, _) => {
                        let label = match status {
                            TransferStatus::Cancelled => "Cancelled",
                            TransferStatus::Rejected => "Rejected",
                            TransferStatus::Declined => "Declined",
                            TransferStatus::Busy => "Busy",
                            TransferStatus::Offline => "Offline",
                            _ => "Failed",
                        };
                        match error {
                            Some(error) => format!("✗ {label}: {error}"),
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
                self.line(&format!("✗ Declined a send request from unpaired device {alias} ({fp}, {ip})"));
            }
            CoreEvent::TransferWaiting { alias, .. } => {
                self.line(&format!("⏳ Waiting for {alias} to accept…"));
            }
            CoreEvent::ReceiveRequest {
                device_id,
                alias,
                label,
                total,
                text,
                ..
            } => {
                let fp = trust::short_fingerprint(device_id);
                self.line(&match text {
                    Some(text) => format!("{alias} (LocalSend, {fp}) sent a text message:\n{text}"),
                    None => format!("{alias} (LocalSend, {fp}) wants to send {label} ({})", format_bytes(*total)),
                });
            }
            CoreEvent::ReceiveClosed { reason, .. } => {
                self.line(match reason.as_str() {
                    "timeout" => "✗ No answer within 1 minute; declined",
                    _ => "✗ The sender cancelled",
                });
            }
            CoreEvent::TransferQueued { position, .. } => {
                self.line(&format!("⏳ The other device is receiving from another device; queued · {position} ahead"));
            }
            CoreEvent::ReceiveWaiting { count } => match count {
                0 => self.line("The queue is empty"),
                1 => self.line("1 device is waiting in the queue"),
                count => self.line(&format!("{count} devices are waiting in the queue")),
            },
            CoreEvent::PairIncoming { device_id, alias, .. } => {
                let fp = trust::short_fingerprint(device_id);
                self.line(&format!("{alias} ({fp}) wants to pair"));
            }
            CoreEvent::PairCode { alias, code, .. } => {
                self.line(&format!("Pairing code: {}  check it against the number on {alias}'s screen", format_code(code)));
            }
            CoreEvent::PairResult { alias, ok, reason, .. } => match ok {
                true => self.line(&format!("✓ Paired with {alias}; files between you no longer need confirming")),
                false => self.line(&format!("✗ Pairing failed: {}", reason.as_deref().unwrap_or("unknown reason"))),
            },
            CoreEvent::DeviceUnpaired { alias, .. } => {
                self.line(&format!("{alias} unpaired from this device"));
            }
            CoreEvent::Error { message } => self.line(&format!("! {message}")),
        }
    }
}

/// "1 file", "3 files".
fn count_files(count: usize) -> String {
    match count {
        1 => "1 file".to_string(),
        count => format!("{count} files"),
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
