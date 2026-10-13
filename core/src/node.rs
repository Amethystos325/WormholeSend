//! The running node: the transfer service, discovery, and the online state of
//! paired devices. The receive side lives in [`crate::receive`], the send
//! side in [`crate::send`].

use crate::config::{MULTICAST_PORT, Settings};
use crate::event::CoreEvent;
use crate::identity::{DEVICE_MODEL, Identity};
use crate::send::SendCancel;
use crate::trust::{TrustStore, normalize_fingerprint};
use crate::pairing::{self, Pairings};
use crate::queue::ReceiveQueue;
use crate::{control, receive, send};
use anyhow::Context;
use localsend::discovery::{
    DEFAULT_DISCOVERY_TIMEOUT, DeviceChannel, DiscoveredDevice, DiscoveryConfig, DiscoveryEvent,
    DiscoveryHandle, HttpChannel,
};
use localsend::http::dto_v2::RegisterDtoV2;
use localsend::http::server::v2::ServerEventV2;
use localsend::http::server::web::WebConfig;
use localsend::http::server::{ServerConfigV2, ServerHandle, start_with_port};
use localsend::model::discovery::{DeviceType, ProtocolType};
use localsend::multicast::{DEFAULT_MULTICAST_GROUP, DEFAULT_MULTICAST_GROUP_V6};
use localsend::util::interface::InterfaceFilter;
use serde::Serialize;
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use tokio::sync::{mpsc, oneshot};
use tokio::task::JoinHandle;

/// How long a device counts as online after it was last seen.
pub(crate) const ONLINE_WINDOW: Duration = Duration::from_secs(60);

/// How often paired devices are probed at their last known address.
const PROBE_INTERVAL: Duration = Duration::from_secs(20);

/// LocalSend devices are probed while they were seen this recently: they
/// announce themselves only when their app starts or refreshes.
const LOCALSEND_PROBE_WINDOW: Duration = Duration::from_secs(10 * 60);

/// Which app a device runs, told by the model it announces.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Kind {
    /// Wormhole Send: pairs, then sends and receives without asking.
    Wormhole,
    /// LocalSend: cannot pair; its user, or this one, accepts every transfer.
    #[serde(rename = "localsend")]
    LocalSend,
}

impl Kind {
    pub(crate) fn of(device_model: Option<&str>) -> Self {
        match device_model {
            Some(DEVICE_MODEL) => Kind::Wormhole,
            _ => Kind::LocalSend,
        }
    }
}

pub struct NodeOptions {
    /// Directory of the identity, the settings and the trust store.
    pub home: PathBuf,
    pub settings: Settings,
    /// Listen on a free port when the configured one is taken, e.g. by a
    /// receiving node of the same home. For short-lived commands only.
    pub port_fallback: bool,
}

/// A device as the shell lists it: paired devices, plus the other devices
/// discovered in this run.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceInfo {
    pub fingerprint: String,
    pub alias: String,
    pub device_type: Option<String>,
    /// Paired devices are always Wormhole Send devices.
    pub kind: Kind,
    pub host: Option<String>,
    pub port: Option<u16>,
    pub online: bool,
    pub paired: bool,
    pub is_default: bool,
    /// Paired here, but the device refused a transfer: pair again.
    pub needs_repair: bool,
}

pub struct Node {
    inner: Arc<Inner>,
    server: Arc<ServerHandle>,
    server_stop_tx: Option<oneshot::Sender<()>>,
    discovery_stop_tx: Option<oneshot::Sender<()>>,
    loop_stop_tx: Option<oneshot::Sender<()>>,
    tasks: Vec<JoinHandle<()>>,
}

/// State shared by the node's tasks.
pub(crate) struct Inner {
    pub(crate) home: PathBuf,
    pub(crate) settings: Settings,
    pub(crate) identity: Identity,
    pub(crate) discovery: Arc<DiscoveryHandle>,
    events: mpsc::UnboundedSender<CoreEvent>,
    /// Outgoing transfers by transfer ID.
    pub(crate) sends: Mutex<HashMap<String, ActiveSend>>,
    /// When each device was last seen, by fingerprint.
    presence: Mutex<HashMap<String, Presence>>,
    pub(crate) pairings: Pairings,
    pub(crate) queue: ReceiveQueue,
    /// The user's answers to `receive.request`, for the event loop.
    pub(crate) answers: mpsc::UnboundedSender<receive::Answer>,
    /// The `receive.request` waiting for an answer, if any.
    pub(crate) asking: Mutex<Option<String>>,
}

pub(crate) struct ActiveSend {
    /// The session ID the receiver issued; set once prepare-upload succeeded.
    pub(crate) session_id: Option<String>,
    pub(crate) host: String,
    pub(crate) cancel: SendCancel,
}

struct Presence {
    last_seen: Instant,
    /// Whether the shell was last told the device is online.
    reported_online: bool,
}

impl Node {
    pub async fn start(
        options: NodeOptions,
    ) -> anyhow::Result<(Node, mpsc::UnboundedReceiver<CoreEvent>)> {
        let NodeOptions {
            home,
            settings,
            port_fallback,
        } = options;
        let mut identity =
            Identity::load_or_generate(&home, settings.alias.clone(), settings.port)?;

        let (server, server_rx, server_stop_tx, control_listeners) =
            bind_ports(&identity, settings.port, port_fallback).await?;
        // Announce the port actually bound.
        identity.port = server.port();

        let (events_tx, events_rx) = mpsc::unbounded_channel();
        let (answers_tx, answers_rx) = mpsc::unbounded_channel();
        let (discovery_tx, discovery_rx) = mpsc::channel::<DiscoveryEvent>(64);
        let (discovery_stop_tx, discovery_stop_rx) = oneshot::channel();
        let discovery = Arc::new(
            localsend::discovery::start(
                DiscoveryConfig {
                    group: DEFAULT_MULTICAST_GROUP,
                    group_v6: Some(DEFAULT_MULTICAST_GROUP_V6),
                    port: MULTICAST_PORT,
                    interface_filter: InterfaceFilter::default(),
                    device: identity.multicast_device(),
                    identity: identity.device_identity(),
                    timeout: DEFAULT_DISCOVERY_TIMEOUT,
                    event_tx: Some(discovery_tx),
                },
                discovery_stop_rx,
            )
            .await,
        );
        if let Some(err) = discovery.multicast_error() {
            let _ = events_tx.send(CoreEvent::Error {
                message: tr!("组播不可用，只能通过已知地址找到设备：{err:#}", "Multicast is unavailable; devices can only be found at known addresses: {err:#}", "マルチキャストを利用できません。既知のアドレスでのみデバイスを検出できます：{err:#}"),
            });
        }

        let inner = Arc::new(Inner {
            home,
            settings,
            identity,
            discovery,
            queue: ReceiveQueue::new(events_tx.clone()),
            events: events_tx,
            sends: Mutex::new(HashMap::new()),
            presence: Mutex::new(HashMap::new()),
            pairings: Pairings::default(),
            answers: answers_tx,
            asking: Mutex::new(None),
        });
        let server = Arc::new(server);

        let (loop_stop_tx, loop_stop_rx) = oneshot::channel();
        let mut tasks = vec![tokio::spawn(receive::run(
            inner.clone(),
            server.clone(),
            server_rx,
            discovery_rx,
            answers_rx,
            loop_stop_rx,
        ))];
        tasks.push(tokio::spawn({
            let inner = inner.clone();
            async move {
                let mut interval = tokio::time::interval(PROBE_INTERVAL);
                loop {
                    interval.tick().await;
                    inner.probe_known().await;
                    inner.check_presence();
                }
            }
        }));
        tasks.push(tokio::spawn({
            let inner = inner.clone();
            async move { inner.discovery.announce().await }
        }));
        tasks.extend(control::server::spawn(inner.clone(), control_listeners)?);
        tasks.push(tokio::spawn({
            let inner = inner.clone();
            async move {
                let mut interval = tokio::time::interval(Duration::from_secs(1));
                loop {
                    interval.tick().await;
                    inner.queue.sweep();
                }
            }
        }));

        Ok((
            Node {
                inner,
                server,
                server_stop_tx: Some(server_stop_tx),
                discovery_stop_tx: Some(discovery_stop_tx),
                loop_stop_tx: Some(loop_stop_tx),
                tasks,
            },
            events_rx,
        ))
    }

    pub fn identity(&self) -> &Identity {
        &self.inner.identity
    }

    pub fn settings(&self) -> &Settings {
        &self.inner.settings
    }

    /// Announces this device and probes the paired (and recently seen
    /// LocalSend) devices at their last known addresses; returns once the
    /// probes answered or timed out.
    pub async fn refresh(&self) {
        tokio::join!(self.inner.discovery.announce(), self.inner.probe_known());
    }

    /// Paired devices first (the default one on top), then the other devices
    /// discovered in this run.
    pub fn devices(&self) -> Vec<DeviceInfo> {
        let inner = &self.inner;
        let trust = inner.trust();
        let mut list: Vec<DeviceInfo> = inner
            .discovery
            .devices()
            .into_iter()
            .filter_map(|stateful| {
                let device = &stateful.device;
                let fingerprint = normalize_fingerprint(&device.fingerprint);
                if fingerprint == inner.identity.fingerprint {
                    return None;
                }
                let http = stateful.get_best_channel().and_then(|channel| channel.http());
                let trusted = trust.get(&fingerprint);
                Some(DeviceInfo {
                    online: inner.is_online(&fingerprint),
                    kind: match trusted {
                        Some(_) => Kind::Wormhole,
                        None => Kind::of(device.device_model.as_deref()),
                    },
                    paired: trusted.is_some(),
                    is_default: trusted.is_some_and(|t| t.is_default),
                    needs_repair: trusted.is_some_and(|t| t.needs_repair),
                    fingerprint,
                    alias: device.alias.clone(),
                    device_type: device.device_type.as_ref().map(type_name),
                    host: http.map(|http| http.host.clone()),
                    port: http.map(|http| http.port),
                })
            })
            .collect();
        for trusted in trust.devices() {
            if !list.iter().any(|d| d.fingerprint == trusted.fingerprint) {
                list.push(DeviceInfo {
                    fingerprint: trusted.fingerprint.clone(),
                    alias: trusted.alias.clone(),
                    device_type: trusted.device_type.as_ref().map(type_name),
                    host: trusted.last_host.clone(),
                    port: trusted.last_port,
                    kind: Kind::Wormhole,
                    online: false,
                    paired: true,
                    is_default: trusted.is_default,
                    needs_repair: trusted.needs_repair,
                });
            }
        }
        list.sort_by(|a, b| {
            (!a.paired, !a.is_default, a.alias.to_lowercase())
                .cmp(&(!b.paired, !b.is_default, b.alias.to_lowercase()))
        });
        list
    }

    /// Starts sending `paths` (files and folders) to a paired device, or to
    /// a LocalSend device that is online (its user accepts), and returns the
    /// transfer ID. Progress and the result arrive as events.
    pub fn send(&self, fingerprint: &str, paths: Vec<PathBuf>) -> String {
        let transfer_id = uuid::Uuid::new_v4().to_string();
        send::spawn(
            self.inner.clone(),
            transfer_id.clone(),
            normalize_fingerprint(fingerprint),
            paths,
        );
        transfer_id
    }

    /// Like [`Node::refresh`], without waiting: answers arrive as
    /// `device.updated` events.
    pub fn refresh_in_background(&self) {
        let inner = self.inner.clone();
        tokio::spawn(async move {
            tokio::join!(inner.discovery.announce(), inner.probe_known());
        });
    }

    /// Starts pairing with a discovered device and returns the pairing ID
    /// right away. The code follows as `pair.code`, the outcome as
    /// `pair.result` (also when the exchange fails before there is a code).
    pub fn pair(&self, fingerprint: &str) -> anyhow::Result<String> {
        pairing::start(&self.inner, fingerprint)
    }

    /// This side's user compared the codes and accepts or declines.
    pub fn pair_confirm(&self, pair_id: &str, accept: bool) -> anyhow::Result<()> {
        pairing::confirm(&self.inner, pair_id, accept)
    }

    /// The user accepts or declines a `receive.request`. Returns whether it
    /// was still waiting for the answer.
    pub fn respond_receive(&self, request_id: &str, accept: bool) -> bool {
        if self.inner.asking.lock().unwrap().as_deref() != Some(request_id) {
            return false;
        }
        let answer = receive::Answer::User { request_id: request_id.to_string(), accept };
        self.inner.answers.send(answer).is_ok()
    }

    /// Unpairs a device and tells it. `None`: it was not paired;
    /// `Some(false)`: it is told when it is seen again.
    pub async fn unpair(&self, fingerprint: &str) -> anyhow::Result<Option<bool>> {
        match pairing::remove(&self.inner, fingerprint)? {
            Some(device) => Ok(Some(pairing::notify_removed(&self.inner, device).await?)),
            None => Ok(None),
        }
    }

    /// Unpairs a device here and tells it in the background (or when it is
    /// seen again). Returns whether it was paired.
    pub fn unpair_in_background(&self, fingerprint: &str) -> anyhow::Result<bool> {
        let Some(device) = pairing::remove(&self.inner, fingerprint)? else {
            return Ok(false);
        };
        let inner = self.inner.clone();
        tokio::spawn(async move {
            if let Err(err) = pairing::notify_removed(&inner, device).await {
                inner.emit(CoreEvent::Error { message: format!("{err:#}") });
            }
        });
        Ok(true)
    }

    /// Makes a paired device the default one. Returns whether it is paired.
    pub fn set_default(&self, fingerprint: &str) -> anyhow::Result<bool> {
        TrustStore::update(&self.inner.home, |store| {
            let found = store.set_default(fingerprint);
            (found, found)
        })
    }

    /// Cancels an outgoing transfer. Returns whether it was still running.
    pub fn cancel(&self, transfer_id: &str) -> bool {
        match self.inner.sends.lock().unwrap().get(transfer_id) {
            Some(send) => {
                send.cancel.token.cancel();
                true
            }
            None => false,
        }
    }

    /// Stops the service and the discovery, waiting briefly so the ports are
    /// released cleanly.
    pub async fn stop(mut self) {
        for send in self.inner.sends.lock().unwrap().values() {
            send.cancel.token.cancel();
        }
        if let Some(tx) = self.loop_stop_tx.take() {
            let _ = tx.send(());
        }
        if let Some(tx) = self.server_stop_tx.take() {
            let _ = tx.send(());
        }
        if let Some(tx) = self.discovery_stop_tx.take() {
            let _ = tx.send(());
        }
        let _ = tokio::time::timeout(Duration::from_secs(1), self.server.wait_stopped()).await;
        let _ = tokio::time::timeout(
            Duration::from_secs(1),
            self.inner.discovery.wait_stopped(),
        )
        .await;
        for task in self.tasks.drain(..) {
            task.abort();
        }
    }
}

impl Inner {
    pub(crate) fn emit(&self, event: CoreEvent) {
        let _ = self.events.send(event);
    }

    /// The trust store as it is on disk. An unreadable store trusts nobody.
    pub(crate) fn trust(&self) -> TrustStore {
        TrustStore::load(&self.home).unwrap_or_else(|err| {
            self.emit(CoreEvent::Error {
                message: format!("{err:#}"),
            });
            TrustStore::default()
        })
    }

    pub(crate) fn is_online(&self, fingerprint: &str) -> bool {
        self.presence
            .lock()
            .unwrap()
            .get(fingerprint)
            .is_some_and(|p| p.last_seen.elapsed() < ONLINE_WINDOW)
    }

    /// Feeds a device that contacted this one (a register or a transfer
    /// request) into the discovery store; the store reports it back as a
    /// discovery event.
    pub(crate) fn confirm_device(&self, host: String, info: RegisterDtoV2) {
        if normalize_fingerprint(&info.fingerprint) == self.identity.fingerprint {
            return;
        }
        let device = DiscoveredDevice {
            alias: info.alias,
            version: info.version,
            device_model: info.device_model,
            device_type: info.device_type,
            fingerprint: info.fingerprint,
            channel: DeviceChannel::Http(HttpChannel {
                host,
                port: info.port,
                protocol: info.protocol,
            }),
            download: info.download,
        };
        let discovery = self.discovery.clone();
        tokio::spawn(async move {
            discovery.add_device(device).await;
        });
    }

    /// A device was confirmed by discovery (`first` on its first confirmation
    /// in this run).
    pub(crate) fn on_device_seen(self: &Arc<Self>, device: &DiscoveredDevice, first: bool) {
        let fingerprint = normalize_fingerprint(&device.fingerprint);
        if fingerprint == self.identity.fingerprint {
            return;
        }
        if let Some(http) = device.http() {
            pairing::deliver_pending(self, &fingerprint, &http.host, http.port);
        }
        let paired = match device.http() {
            Some(http) => TrustStore::update(&self.home, |store| {
                let changed = store.record_seen(&fingerprint, &device.alias, &http.host, http.port);
                (changed, store.contains(&fingerprint))
            })
            .unwrap_or(false),
            None => self.trust().contains(&fingerprint),
        };
        let kind = match paired {
            true => Kind::Wormhole,
            false => Kind::of(device.device_model.as_deref()),
        };
        let newly_online = {
            let mut presence = self.presence.lock().unwrap();
            let entry = presence.entry(fingerprint.clone()).or_insert(Presence {
                last_seen: Instant::now(),
                reported_online: false,
            });
            entry.last_seen = Instant::now();
            !std::mem::replace(&mut entry.reported_online, true)
        };
        // LocalSend devices can be sent to, so they come and go like paired ones.
        if first || ((paired || kind == Kind::LocalSend) && newly_online) {
            self.emit(CoreEvent::DeviceUpdated {
                id: fingerprint,
                alias: device.alias.clone(),
                device_type: device.device_type.as_ref().map(type_name),
                kind,
                online: true,
                paired,
            });
        }
    }

    /// Reports paired and LocalSend devices that have not been seen for a
    /// while as offline.
    fn check_presence(&self) {
        let gone: Vec<String> = {
            let mut presence = self.presence.lock().unwrap();
            presence
                .iter_mut()
                .filter(|(_, p)| p.reported_online && p.last_seen.elapsed() >= ONLINE_WINDOW)
                .map(|(fingerprint, p)| {
                    p.reported_online = false;
                    fingerprint.clone()
                })
                .collect()
        };
        if gone.is_empty() {
            return;
        }
        let trust = self.trust();
        for fingerprint in gone {
            if let Some(device) = trust.get(&fingerprint) {
                self.emit(CoreEvent::DeviceUpdated {
                    id: fingerprint,
                    alias: device.alias.clone(),
                    device_type: device.device_type.as_ref().map(type_name),
                    kind: Kind::Wormhole,
                    online: false,
                    paired: true,
                });
            } else if let Some(found) = self.discovery.device_by_fingerprint(&fingerprint)
                && Kind::of(found.device.device_model.as_deref()) == Kind::LocalSend
            {
                self.emit(CoreEvent::DeviceUpdated {
                    id: fingerprint,
                    alias: found.device.alias.clone(),
                    device_type: found.device.device_type.as_ref().map(type_name),
                    kind: Kind::LocalSend,
                    online: false,
                    paired: false,
                });
            }
        }
    }

    /// Sends a register request to every paired device with a known address,
    /// and to the LocalSend devices seen recently; the ones that answer are
    /// confirmed through discovery events.
    pub(crate) async fn probe_known(&self) {
        let mut channels: Vec<HttpChannel> = self
            .trust()
            .devices()
            .iter()
            .filter_map(|device| {
                Some(HttpChannel {
                    host: device.last_host.clone()?,
                    port: device.last_port?,
                    protocol: ProtocolType::Https,
                })
            })
            .collect();
        let recent = |fingerprint: &str| {
            self.presence
                .lock()
                .unwrap()
                .get(fingerprint)
                .is_some_and(|p| p.last_seen.elapsed() < LOCALSEND_PROBE_WINDOW)
        };
        for found in self.discovery.devices() {
            let device = &found.device;
            if Kind::of(device.device_model.as_deref()) == Kind::LocalSend
                && recent(&normalize_fingerprint(&device.fingerprint))
                && let Some(http) = found.get_best_channel().and_then(|channel| channel.http())
            {
                channels.push(http.clone());
            }
        }
        if !channels.is_empty() {
            let _ = self.discovery.discover_known_http_channels(channels).await;
        }
    }
}

async fn start_server(
    identity: &Identity,
    port: u16,
) -> anyhow::Result<(
    ServerHandle,
    mpsc::Receiver<ServerEventV2>,
    oneshot::Sender<()>,
)> {
    let (server_tx, server_rx) = mpsc::channel::<ServerEventV2>(64);
    let (stop_tx, stop_rx) = oneshot::channel();
    let server = start_with_port(
        port,
        Some(identity.tls_config()),
        identity.client_info(),
        None,
        Some(ServerConfigV2 {
            pin: None,
            verify_checksums: true,
            event_tx: server_tx,
        }),
        // No web share: client certificates are mandatory, so every request
        // carries a verified sender fingerprint.
        WebConfig::default(),
        stop_rx,
    )
    .await?;
    Ok((server, server_rx, stop_tx))
}

/// Binds the transfer port and the control port (+1) as a pair. With
/// `fallback`, a taken pair moves on to the next one.
async fn bind_ports(
    identity: &Identity,
    base: u16,
    fallback: bool,
) -> anyhow::Result<(
    ServerHandle,
    mpsc::Receiver<ServerEventV2>,
    oneshot::Sender<()>,
    Vec<std::net::TcpListener>,
)> {
    let attempts = if fallback { 10 } else { 1 };
    let mut last_error = None;
    for attempt in 0..attempts {
        let Some(port) = base.checked_add(2 * attempt) else { break };
        let control = match control::server::bind(port + 1) {
            Ok(listeners) => listeners,
            Err(err) if err.kind() == std::io::ErrorKind::AddrInUse => {
                last_error = Some(anyhow::Error::from(err));
                continue;
            }
            Err(err) => return Err(err).with_context(|| tr!("无法监听控制端口 {}", "Can't listen on control port {}", "制御ポート {} で待ち受けできません", port + 1)),
        };
        match start_server(identity, port).await {
            Ok((server, rx, stop)) => return Ok((server, rx, stop, control)),
            Err(err) if is_addr_in_use(&err) => last_error = Some(err),
            Err(err) => return Err(err).with_context(|| tr!("无法启动传输服务", "Can't start the transfer service", "転送サービスを開始できません")),
        }
    }
    let err = last_error.unwrap_or_else(|| anyhow::anyhow!("no free port"));
    Err(err).context(match fallback {
        true => tr!("端口 {base} 起都被占用", "Ports from {base} on are all in use", "ポート {base} 以降はすべて使用中です"),
        false => tr!("端口 {base} 或 {} 已被占用（可能已有一个 whs receive 在运行）", "Port {base} or {} is in use (maybe a whs receive is running)", "ポート {base} または {} は使用中です（whs receive が動いている可能性があります）", base + 1),
    })
}

fn is_addr_in_use(err: &anyhow::Error) -> bool {
    err.chain().any(|cause| {
        cause
            .downcast_ref::<std::io::Error>()
            .is_some_and(|io| io.kind() == std::io::ErrorKind::AddrInUse)
    })
}

pub(crate) fn parse_type(name: &str) -> Option<DeviceType> {
    match name {
        "mobile" => Some(DeviceType::Mobile),
        "desktop" => Some(DeviceType::Desktop),
        "web" => Some(DeviceType::Web),
        "headless" => Some(DeviceType::Headless),
        "server" => Some(DeviceType::Server),
        _ => None,
    }
}

pub(crate) fn type_name(device_type: &DeviceType) -> String {
    match device_type {
        DeviceType::Mobile => "mobile",
        DeviceType::Desktop => "desktop",
        DeviceType::Web => "web",
        DeviceType::Headless => "headless",
        DeviceType::Server => "server",
    }
    .to_string()
}
