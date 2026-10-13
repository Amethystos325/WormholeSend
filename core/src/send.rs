//! The send side: locate the device (paired, or a LocalSend device whose
//! user accepts), prepare-upload, then upload the files one by one.
//!
//! The upload loop is adapted from the LocalSend CLI (`cli/src/send_task.rs`,
//! Apache-2.0).

use crate::control::client::{ControlClient, ControlError, control_addr};
use crate::event::{CoreEvent, Direction, TransferStatus};
use crate::node::{ActiveSend, Inner, Kind};
use crate::trust::{TrustStore, normalize_fingerprint, short_fingerprint};
use bytes::Bytes;
use futures_util::StreamExt;
use localsend::discovery::HttpChannel;
use localsend::http::client::ClientError;
use localsend::http::client::v2::LsHttpClientV2;
use localsend::http::dto_v2::PrepareUploadRequestDtoV2;
use localsend::model::discovery::ProtocolType;
use localsend::model::transfer::{FileContent, FileDto, FileMetadata};
use localsend::reqwest;
use std::collections::HashMap;
use std::net::SocketAddr;
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::time::Duration;
use tokio_util::sync::CancellationToken;
use walkdir::WalkDir;

/// How long to probe the last known address of the device.
const PROBE_TIMEOUT: Duration = Duration::from_secs(1);

/// How long to wait for the device to answer an announcement.
const ANNOUNCE_WAIT: Duration = Duration::from_secs(2);

const PROGRESS_INTERVAL: Duration = Duration::from_millis(250);

/// A busy receiver (HTTP 409) right after this device's turn came is retried.
const BUSY_RETRIES: u32 = 3;
const BUSY_RETRY_DELAY: Duration = Duration::from_millis(500);

/// Cancellation state of a send, shared between the node and the send task.
#[derive(Clone)]
pub(crate) struct SendCancel {
    pub(crate) token: CancellationToken,
    /// Set (before triggering `token`) when the receiver cancelled; only a
    /// local cancellation still has to notify the receiver.
    pub(crate) by_peer: Arc<AtomicBool>,
}

impl SendCancel {
    fn new() -> Self {
        Self {
            token: CancellationToken::new(),
            by_peer: Arc::new(AtomicBool::new(false)),
        }
    }
}

struct Outcome {
    status: TransferStatus,
    files_done: usize,
    bytes: u64,
    error: Option<String>,
}

impl Outcome {
    fn fail(status: TransferStatus, error: impl Into<String>) -> Self {
        Self {
            status,
            files_done: 0,
            bytes: 0,
            error: Some(error.into()),
        }
    }

    /// Cancelled on this device; the status says it all.
    fn cancelled() -> Self {
        Self {
            status: TransferStatus::Cancelled,
            files_done: 0,
            bytes: 0,
            error: None,
        }
    }
}

/// Where a send goes: a paired device, or a LocalSend device discovered in
/// this run.
struct Target {
    fingerprint: String,
    alias: String,
    /// Where it was last seen.
    host: Option<String>,
    port: Option<u16>,
    protocol: ProtocolType,
    /// Paired devices accept without asking and keep a receive queue.
    paired: bool,
}

impl Target {
    fn find(inner: &Inner, fingerprint: &str) -> Option<Self> {
        if let Some(device) = inner.trust().get(fingerprint) {
            return Some(Self {
                fingerprint: device.fingerprint.clone(),
                alias: device.alias.clone(),
                host: device.last_host.clone(),
                port: device.last_port,
                protocol: ProtocolType::Https,
                paired: true,
            });
        }
        let found = inner.discovery.device_by_fingerprint(fingerprint)?;
        if Kind::of(found.device.device_model.as_deref()) != Kind::LocalSend {
            return None;
        }
        let http = found.get_best_channel().and_then(|channel| channel.http());
        Some(Self {
            fingerprint: fingerprint.to_string(),
            alias: found.device.alias.clone(),
            host: http.map(|http| http.host.clone()),
            port: http.map(|http| http.port),
            protocol: http.map_or(ProtocolType::Https, |http| http.protocol),
            paired: false,
        })
    }
}

pub(crate) fn spawn(inner: Arc<Inner>, transfer_id: String, fingerprint: String, paths: Vec<PathBuf>) {
    let cancel = SendCancel::new();
    inner.sends.lock().unwrap().insert(
        transfer_id.clone(),
        ActiveSend {
            session_id: None,
            host: String::new(),
            cancel: cancel.clone(),
        },
    );
    tokio::spawn(async move {
        let target = Target::find(&inner, &fingerprint);
        let alias = target
            .as_ref()
            .map_or_else(|| short_fingerprint(&fingerprint).to_string(), |t| t.alias.clone());
        let outcome = match target {
            Some(target) => run(&inner, &transfer_id, &target, paths, &cancel).await,
            None => Outcome::fail(TransferStatus::Rejected, tr!("目标设备没有配对", "This device isn't paired", "このデバイスはペアリングされていません")),
        };
        inner.sends.lock().unwrap().remove(&transfer_id);
        inner.emit(CoreEvent::TransferFinished {
            id: transfer_id,
            direction: Direction::Send,
            device_id: fingerprint,
            alias,
            status: outcome.status,
            files_done: outcome.files_done,
            bytes: outcome.bytes,
            saved_paths: Vec::new(),
            error: outcome.error,
        });
    });
}

async fn run(
    inner: &Arc<Inner>,
    transfer_id: &str,
    device: &Target,
    paths: Vec<PathBuf>,
    cancel: &SendCancel,
) -> Outcome {
    let fingerprint = &device.fingerprint;
    let (files, sources, total) = collect_files(inner, &paths);
    if files.is_empty() {
        return Outcome::fail(TransferStatus::Failed, tr!("没有可发送的文件", "Nothing to send", "送信できるファイルがありません"));
    }

    let http = tokio::select! {
        http = locate(inner, device) => http,
        _ = cancel.token.cancelled() => return Outcome::cancelled(),
    };
    let Some(http) = http else {
        return Outcome::fail(TransferStatus::Offline, tr!("{} 不在线", "{} is offline", "{} はオフラインです", device.alias));
    };
    if let Some(send) = inner.sends.lock().unwrap().get_mut(transfer_id) {
        send.host = http.host.clone();
    }

    // Wait for this device's turn in the receiver's queue. LocalSend has
    // none: a busy receiver answers 409.
    let control = control_addr(&http.host, http.port).filter(|_| device.paired);
    let ticket = match wait_turn(inner, transfer_id, device, control, cancel).await {
        Ok(ticket) => ticket,
        Err(outcome) => return outcome,
    };
    // Gives the turn back when the transfer stops before its session starts.
    let release = || {
        if let (Some(addr), Some(ticket)) = (control, ticket.clone()) {
            let (inner, fingerprint) = (inner.clone(), fingerprint.clone());
            tokio::spawn(async move {
                let client = ControlClient { identity: &inner.identity, addr, expected: Some(&fingerprint) };
                let _ = client.queue_leave(&ticket).await;
            });
        }
    };

    let started = |files: usize, total: u64| {
        inner.emit(CoreEvent::TransferStarted {
            id: transfer_id.to_string(),
            direction: Direction::Send,
            device_id: fingerprint.clone(),
            alias: device.alias.clone(),
            files,
            total,
        });
    };
    // A LocalSend device's user accepts first, possibly only some files.
    match device.paired {
        true => started(files.len(), total),
        false => inner.emit(CoreEvent::TransferWaiting {
            id: transfer_id.to_string(),
            device_id: fingerprint.clone(),
            alias: device.alias.clone(),
        }),
    }

    // Pinned to the fingerprint: a device impersonating it fails the TLS
    // handshake.
    let client = match LsHttpClientV2::try_new(
        &inner.identity.key_pem,
        &inner.identity.cert_pem,
        Some(fingerprint.clone()),
        None,
    ) {
        Ok(client) => Arc::new(client),
        Err(err) => return Outcome::fail(TransferStatus::Failed, tr!("无法创建连接：{err}", "Can't connect: {err}", "接続できません：{err}")),
    };
    let protocol = device.protocol;

    let mut attempt = 0;
    let prepared = loop {
        let prepared = client
            .prepare_upload(
                protocol,
                &http.host,
                http.port,
                None,
                PrepareUploadRequestDtoV2 {
                    info: inner.identity.register_dto(),
                    files: files.clone(),
                },
                None,
                cancel.token.clone(),
            )
            .await;
        match prepared {
            // The previous session may still be closing on the receiver,
            // although it is this device's turn already.
            Err(ClientError::StatusCode(err)) if err.status == 409 && attempt < BUSY_RETRIES => {
                attempt += 1;
                tokio::time::sleep(BUSY_RETRY_DELAY).await;
            }
            result => break result,
        }
    };
    let prepared = match prepared {
        Ok(prepared) => prepared,
        Err(ClientError::Cancelled) => {
            release();
            return Outcome::cancelled();
        }
        Err(ClientError::StatusCode(err)) => {
            release();
            return match err.status {
                403 if !device.paired => {
                    Outcome::fail(TransferStatus::Declined, tr!("{} 拒绝了接收", "{} declined", "{} が受信を拒否しました", device.alias))
                }
                403 => {
                    mark_needs_repair(inner, fingerprint);
                    Outcome::fail(
                        TransferStatus::Rejected,
                        tr!("{} 没有信任本机（未配对或已解除配对）", "{} doesn't trust this device (not paired, or unpaired)", "{} はこのデバイスを信頼していません（未ペアリングまたはペアリング解除済み）", device.alias),
                    )
                }
                409 => Outcome::fail(
                    TransferStatus::Busy,
                    tr!("{} 正在接收其他文件", "{} is receiving other files", "{} は別のファイルを受信中です", device.alias),
                ),
                401 => Outcome::fail(
                    TransferStatus::Failed,
                    tr!("{} 开启了 PIN，暂不支持", "{} asks for a PIN, which isn't supported yet", "{} は PIN を必要としていますが、まだ対応していません", device.alias),
                ),
                status => Outcome::fail(TransferStatus::Failed, tr!("请求失败（HTTP {status}）", "The request failed (HTTP {status})", "リクエストに失敗しました（HTTP {status}）")),
            };
        }
        Err(err) => {
            release();
            return Outcome::fail(TransferStatus::Failed, err.to_string());
        }
    };
    let Some(response) = prepared.response else {
        return Outcome::fail(TransferStatus::Failed, tr!("对方没有接收任何文件", "The other device accepted no files", "相手はファイルを受け取りませんでした"));
    };
    if response.session_id.is_empty()
        || response.files.is_empty()
        || response
            .files
            .iter()
            .any(|(id, token)| !files.contains_key(id) || token.is_empty())
    {
        return Outcome::fail(TransferStatus::Failed, tr!("对方的响应无效", "The other device's response is invalid", "相手からの応答が無効です"));
    }
    if let Some(send) = inner.sends.lock().unwrap().get_mut(transfer_id) {
        send.session_id = Some(response.session_id.clone());
    }

    // On a local cancellation, tell the receiver before aborting the upload:
    // aborted first, the receiver would see a failed file and end the session
    // as finished-with-errors before the cancel request arrives.
    let upload_token = CancellationToken::new();
    let watcher = tokio::spawn({
        let (client, cancel, upload_token) = (client.clone(), cancel.clone(), upload_token.clone());
        let (host, port, session_id) = (http.host.clone(), http.port, response.session_id.clone());
        async move {
            cancel.token.cancelled().await;
            if !cancel.by_peer.load(Ordering::Relaxed) {
                let notify = client.cancel(protocol, &host, port, &session_id);
                let _ = tokio::time::timeout(PROBE_TIMEOUT, notify).await;
            }
            upload_token.cancel();
        }
    });

    let progress = Arc::new(AtomicU64::new(0));
    let files_done = Arc::new(AtomicUsize::new(0));
    let accepted_total: u64 = response
        .files
        .keys()
        .filter_map(|id| files.get(id))
        .map(|file| file.size)
        .sum();
    if !device.paired {
        started(response.files.len(), accepted_total);
    }
    let ticker = tokio::spawn({
        let inner = inner.clone();
        let (progress, files_done) = (progress.clone(), files_done.clone());
        let (id, device_id) = (transfer_id.to_string(), fingerprint.clone());
        async move {
            let mut interval = tokio::time::interval(PROGRESS_INTERVAL);
            loop {
                interval.tick().await;
                inner.emit(CoreEvent::TransferProgress {
                    id: id.clone(),
                    direction: Direction::Send,
                    device_id: device_id.clone(),
                    bytes: progress.load(Ordering::Relaxed),
                    total: accepted_total,
                    files_done: files_done.load(Ordering::Relaxed),
                });
            }
        }
    });

    // Upload sequentially, in name order.
    let mut file_ids: Vec<&String> = response.files.keys().collect();
    file_ids.sort_by_key(|id| &files[*id].file_name);
    let mut sources = sources;
    let mut outcome = Outcome {
        status: TransferStatus::Done,
        files_done: 0,
        bytes: 0,
        error: None,
    };
    for file_id in file_ids {
        let file = &files[file_id];
        let Some(path) = sources.remove(file_id.as_str()) else {
            continue;
        };
        let body = {
            let progress = progress.clone();
            let base = outcome.bytes;
            upload_body(FileContent::Path(path), move |sent| {
                progress.store(base + sent, Ordering::Relaxed);
            })
        };
        let result = client
            .upload(
                protocol,
                &http.host,
                http.port,
                None,
                &response.session_id,
                file_id,
                &response.files[file_id],
                body,
                upload_token.clone(),
            )
            .await;
        match result {
            Ok(()) => {
                outcome.files_done += 1;
                outcome.bytes += file.size;
                progress.store(outcome.bytes, Ordering::Relaxed);
                files_done.store(outcome.files_done, Ordering::Relaxed);
            }
            // A failure caused by the receiver closing the session after the
            // cancel counts as the cancellation too.
            Err(_) if cancel.token.is_cancelled() => {
                if cancel.by_peer.load(Ordering::Relaxed) {
                    outcome.error = Some(tr!("{} 取消了接收", "{} cancelled", "{} が受信をキャンセルしました", device.alias));
                }
                outcome.status = TransferStatus::Cancelled;
                break;
            }
            Err(err) => {
                let _ = client
                    .cancel(protocol, &http.host, http.port, &response.session_id)
                    .await;
                outcome.status = TransferStatus::Failed;
                outcome.error = Some(tr!("{} 发送失败：{err}", "Couldn't send {}: {err}", "{} の送信に失敗しました：{err}", file.file_name));
                break;
            }
        }
    }
    ticker.abort();
    watcher.abort();
    inner.emit(CoreEvent::TransferProgress {
        id: transfer_id.to_string(),
        direction: Direction::Send,
        device_id: fingerprint.clone(),
        bytes: outcome.bytes,
        total: accepted_total,
        files_done: outcome.files_done,
    });
    if outcome.status == TransferStatus::Done && device.paired {
        let _ = TrustStore::update(&inner.home, |store| (store.record_sent(fingerprint), ()));
    }
    outcome
}

/// Takes a ticket in the receiver's queue and waits for the turn, reporting
/// the position. Returns the ticket, or `None` for a receiver without a
/// control service (it is sent to directly); `Err` ends the transfer.
async fn wait_turn(
    inner: &Arc<Inner>,
    transfer_id: &str,
    device: &Target,
    control: Option<SocketAddr>,
    cancel: &SendCancel,
) -> Result<Option<String>, Outcome> {
    let Some(addr) = control else {
        return Ok(None);
    };
    let client = ControlClient {
        identity: &inner.identity,
        addr,
        expected: Some(&device.fingerprint),
    };
    let mut status = match client.queue_join().await {
        Ok(status) => status,
        Err(ControlError::Status(403, _)) => {
            mark_needs_repair(inner, &device.fingerprint);
            return Err(Outcome::fail(
                TransferStatus::Rejected,
                tr!("{} 没有信任本机（未配对或已解除配对）", "{} doesn't trust this device (not paired, or unpaired)", "{} はこのデバイスを信頼していません（未ペアリングまたはペアリング解除済み）", device.alias),
            ));
        }
        Err(ControlError::Status(503, _)) => {
            return Err(Outcome::fail(
                TransferStatus::Busy,
                tr!("{} 的排队人数已满", "{}'s queue is full", "{} の待ち行列がいっぱいです", device.alias),
            ));
        }
        Err(_) => return Ok(None),
    };
    let mut reported = None;
    while !status.ready {
        if reported != Some(status.position) {
            reported = Some(status.position);
            inner.emit(CoreEvent::TransferQueued {
                id: transfer_id.to_string(),
                device_id: device.fingerprint.clone(),
                position: status.position,
            });
        }
        let next = tokio::select! {
            next = client.queue_wait(&status.ticket) => next,
            _ = cancel.token.cancelled() => {
                let _ = client.queue_leave(&status.ticket).await;
                return Err(Outcome::cancelled());
            }
        };
        status = next.map_err(|err| Outcome::fail(TransferStatus::Failed, tr!("排队时失去连接：{err}", "Lost the connection while queued: {err}", "順番待ち中に接続が切れました：{err}")))?;
    }
    Ok(Some(status.ticket))
}

fn mark_needs_repair(inner: &Inner, fingerprint: &str) {
    let _ = TrustStore::update(&inner.home, |store| (store.mark_needs_repair(fingerprint), ()));
}

/// Finds a dialable address of the device: the one discovery saw recently,
/// else its last known address, else whatever answers an announcement.
async fn locate(inner: &Arc<Inner>, device: &Target) -> Option<HttpChannel> {
    let fingerprint = &device.fingerprint;
    if inner.is_online(fingerprint)
        && let Some(channel) = known_channel(inner, fingerprint)
    {
        return Some(channel);
    }

    if let (Some(host), Some(port)) = (&device.host, device.port) {
        let probe = inner.discovery.discover(host, port, device.protocol);
        if let Ok(Ok(Some(found))) = tokio::time::timeout(PROBE_TIMEOUT, probe).await
            && normalize_fingerprint(&found.device.fingerprint) == *fingerprint
            && let Some(http) = found.get_best_channel().and_then(|channel| channel.http())
        {
            return Some(http.clone());
        }
    }

    tokio::spawn({
        let inner = inner.clone();
        async move { inner.announce().await }
    });
    let deadline = tokio::time::Instant::now() + ANNOUNCE_WAIT;
    while tokio::time::Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(100)).await;
        if inner.is_online(fingerprint)
            && let Some(channel) = known_channel(inner, fingerprint)
        {
            return Some(channel);
        }
    }
    None
}

fn known_channel(inner: &Inner, fingerprint: &str) -> Option<HttpChannel> {
    inner
        .discovery
        .device_by_fingerprint(fingerprint)?
        .get_best_channel()?
        .http()
        .cloned()
}

/// Expands files and folders into protocol v2 file metadata keyed by a fresh
/// file ID, the local paths under the same IDs, and the total size. Folder
/// entries are named `<folder>/<relative path>` so the receiver can rebuild
/// the folder. Unreadable entries are skipped with an error event.
fn collect_files(
    inner: &Inner,
    picked: &[PathBuf],
) -> (HashMap<String, FileDto>, HashMap<String, PathBuf>, u64) {
    let mut files = HashMap::new();
    let mut paths = HashMap::new();
    let mut total = 0u64;
    for (path, file_name) in picked.iter().flat_map(|path| expand(inner, path)) {
        let metadata = match std::fs::metadata(&path) {
            Ok(metadata) if metadata.is_file() => metadata,
            _ => {
                inner.emit(CoreEvent::Error {
                    message: tr!("跳过无法读取的文件：{}", "Skipped a file that can't be read: {}", "読み取れないファイルをスキップしました：{}", path.display()),
                });
                continue;
            }
        };
        let id = uuid::Uuid::new_v4().to_string();
        total += metadata.len();
        files.insert(
            id.clone(),
            FileDto {
                id: id.clone(),
                file_name,
                size: metadata.len(),
                file_type: mime_guess::from_path(&path)
                    .first_or_octet_stream()
                    .to_string(),
                sha256: None,
                preview: None,
                metadata: FileMetadata::from_fs_metadata(&metadata),
            },
        );
        paths.insert(id, path);
    }
    (files, paths, total)
}

fn expand(inner: &Inner, path: &Path) -> Vec<(PathBuf, String)> {
    if path.is_file() {
        let name = path
            .file_name()
            .map_or_else(|| "unnamed".to_string(), |name| name.to_string_lossy().into_owned());
        return vec![(path.to_path_buf(), name)];
    }
    if !path.is_dir() {
        inner.emit(CoreEvent::Error {
            message: tr!("跳过不存在的路径：{}", "Skipped a path that doesn't exist: {}", "存在しないパスをスキップしました：{}", path.display()),
        });
        return Vec::new();
    }
    let root = path
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .or_else(|| {
            path.canonicalize()
                .ok()?
                .file_name()
                .map(|name| name.to_string_lossy().into_owned())
        })
        .unwrap_or_else(|| "folder".to_string());
    WalkDir::new(path)
        .min_depth(1)
        .sort_by_file_name()
        .into_iter()
        .filter_map(|entry| {
            let entry = match entry {
                Ok(entry) => entry,
                Err(err) => {
                    inner.emit(CoreEvent::Error {
                        message: tr!("跳过无法读取的路径：{err}", "Skipped a path that can't be read: {err}", "読み取れないパスをスキップしました：{err}"),
                    });
                    return None;
                }
            };
            if !entry.file_type().is_file() {
                return None;
            }
            let relative = entry.path().strip_prefix(path).ok()?;
            let name = std::iter::once(root.clone())
                .chain(relative.components().filter_map(|component| match component {
                    Component::Normal(name) => Some(name.to_string_lossy().into_owned()),
                    _ => None,
                }))
                .collect::<Vec<_>>()
                .join("/");
            Some((entry.path().to_path_buf(), name))
        })
        .collect()
}

/// A streaming request body from the file content, reporting the bytes of
/// this file sent so far.
fn upload_body(content: FileContent, progress: impl Fn(u64) + Send + 'static) -> reqwest::Body {
    let mut sent = 0u64;
    let stream = content.into_stream().map(move |chunk| {
        let chunk = chunk?;
        sent += chunk.len() as u64;
        progress(sent);
        Ok::<Bytes, anyhow::Error>(chunk)
    });
    reqwest::Body::wrap_stream(stream)
}
