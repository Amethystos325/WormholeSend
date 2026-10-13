//! The node's event loop: server and discovery events, and the receive side
//! of transfers. A request is accepted without asking exactly when the
//! sender's verified certificate fingerprint is in the trust store; every
//! other request is declined (HTTP 403).

use crate::event::{CoreEvent, Direction, TransferStatus};
use crate::node::Inner;
use crate::save_path::receive_path;
use crate::trust::normalize_fingerprint;
use localsend::discovery::DiscoveryEvent;
use localsend::http::server::ServerHandle;
use localsend::http::server::common::save::FileUploadTarget;
use localsend::http::server::v2::{PrepareUploadDecisionV2, ServerEventV2, SessionEndReasonV2};
use localsend::model::transfer::FileDto;
use std::collections::{HashMap, HashSet};
use std::path::{Component, PathBuf};
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};
use tokio::sync::{mpsc, oneshot};

const PROGRESS_INTERVAL: Duration = Duration::from_millis(250);

/// What the per-file helper tasks report back to the loop.
enum FileEvent {
    Progress {
        session_id: String,
        file_id: String,
        written: u64,
    },
    Result {
        session_id: String,
        file_id: String,
        result: Result<(), String>,
    },
}

/// The incoming transfer in progress. The server allows one at a time.
struct ReceiveSession {
    transfer_id: String,
    session_id: String,
    fingerprint: String,
    alias: String,
    files: HashMap<String, FileDto>,
    total: u64,
    /// Bytes written so far, per file.
    written: HashMap<String, u64>,
    paths: HashMap<String, PathBuf>,
    in_flight: HashSet<String>,
    finished: HashSet<String>,
    /// Failed files and why, reported with the result unless the sender
    /// cancelled (then the failures are just the cancellation).
    failed: HashMap<String, String>,
    /// Renamed top-level folders, see [`receive_path`].
    roots: HashMap<String, String>,
    ended: Option<SessionEndReasonV2>,
    last_progress: Instant,
}

impl ReceiveSession {
    fn bytes(&self) -> u64 {
        self.written.values().sum()
    }
}

pub(crate) async fn run(
    inner: Arc<Inner>,
    server: Arc<ServerHandle>,
    mut server_rx: mpsc::Receiver<ServerEventV2>,
    mut discovery_rx: mpsc::Receiver<DiscoveryEvent>,
    mut stop_rx: oneshot::Receiver<()>,
) {
    let (files_tx, mut files_rx) = mpsc::unbounded_channel::<FileEvent>();
    let mut receiver = Receiver {
        inner,
        server,
        files_tx,
        session: None,
    };
    loop {
        tokio::select! {
            _ = &mut stop_rx => break,
            Some(event) = server_rx.recv() => receiver.on_server_event(event),
            Some(event) = discovery_rx.recv() => receiver.on_discovery_event(event),
            Some(event) = files_rx.recv() => receiver.on_file_event(event),
            else => break,
        }
    }
    receiver.abort_session();
}

struct Receiver {
    inner: Arc<Inner>,
    server: Arc<ServerHandle>,
    files_tx: mpsc::UnboundedSender<FileEvent>,
    session: Option<ReceiveSession>,
}

impl Receiver {
    fn on_discovery_event(&mut self, event: DiscoveryEvent) {
        match event {
            DiscoveryEvent::Discovered { device } => self.inner.on_device_seen(&device, true),
            DiscoveryEvent::Updated { device } => self.inner.on_device_seen(&device, false),
            DiscoveryEvent::MulticastFailed => self.inner.emit(CoreEvent::Error {
                message: "组播已停止，只能通过已知地址找到设备".to_string(),
            }),
        }
    }

    fn on_server_event(&mut self, event: ServerEventV2) {
        match event {
            // Peers answer this device's announcement by registering here.
            ServerEventV2::Register { ip, info } => self.inner.confirm_device(ip.to_string(), info),
            ServerEventV2::PrepareUpload {
                session_id,
                ip,
                info,
                cert_fingerprint,
                files,
                decision_tx,
            } => {
                self.inner.confirm_device(ip.to_string(), info.clone());
                // Client certificates are mandatory, so a request without a
                // verified fingerprint should not exist; decline it anyway.
                let fingerprint = cert_fingerprint.as_deref().map(normalize_fingerprint);
                let trusted = fingerprint
                    .as_deref()
                    .is_some_and(|fingerprint| self.inner.trust().contains(fingerprint));
                let alias = single_line(&info.alias);
                if !trusted {
                    let _ = decision_tx.send(PrepareUploadDecisionV2::Decline);
                    self.inner.emit(CoreEvent::ReceiveRejected {
                        device_id: fingerprint.unwrap_or_default(),
                        alias,
                        ip: ip.to_string(),
                    });
                    return;
                }
                let fingerprint = fingerprint.unwrap_or_default();
                // One session at a time, in queue order (see `queue`).
                if !self.inner.queue.admit(&fingerprint) {
                    let _ = decision_tx.send(PrepareUploadDecisionV2::Decline);
                    self.inner.emit(CoreEvent::Error {
                        message: format!("{alias} 没有排队就发送，已拒绝"),
                    });
                    return;
                }
                if let Err(err) = std::fs::create_dir_all(&self.inner.settings.save_dir) {
                    self.inner.queue.session_ended(&fingerprint);
                    let _ = decision_tx.send(PrepareUploadDecisionV2::Decline);
                    self.inner.emit(CoreEvent::Error {
                        message: format!(
                            "无法创建保存目录 {}：{err}",
                            self.inner.settings.save_dir.display()
                        ),
                    });
                    return;
                }
                let ids: HashSet<String> = files.keys().cloned().collect();
                if decision_tx.send(PrepareUploadDecisionV2::Accept(ids)).is_err() {
                    // The sender went away while this was being decided.
                    self.inner.queue.session_ended(&fingerprint);
                    return;
                }
                let session = ReceiveSession {
                    transfer_id: uuid::Uuid::new_v4().to_string(),
                    session_id,
                    fingerprint,
                    alias,
                    total: files.values().map(|file| file.size).sum(),
                    files,
                    written: HashMap::new(),
                    paths: HashMap::new(),
                    in_flight: HashSet::new(),
                    finished: HashSet::new(),
                    failed: HashMap::new(),
                    roots: HashMap::new(),
                    ended: None,
                    last_progress: Instant::now(),
                };
                self.inner.emit(CoreEvent::TransferStarted {
                    id: session.transfer_id.clone(),
                    direction: Direction::Receive,
                    device_id: session.fingerprint.clone(),
                    alias: session.alias.clone(),
                    files: session.files.len(),
                    total: session.total,
                });
                self.session = Some(session);
            }
            ServerEventV2::FileUpload {
                session_id,
                file_id,
                file,
                target_tx,
            } => self.on_file_upload(session_id, file_id, file, target_tx),
            ServerEventV2::SessionEnd { session_id, reason } => {
                if let Some(session) = self.session.as_mut().filter(|s| s.session_id == session_id) {
                    // The per-file results race with this event; finish once
                    // the last file in flight has reported.
                    session.ended = Some(reason);
                    self.finish_if_done();
                }
            }
            ServerEventV2::PrepareUploadAborted { .. } => {}
            // The receiver cancels a transfer this device is sending.
            ServerEventV2::CancelReceived { ip, session_id } => {
                let host = ip.to_string();
                for send in self.inner.sends.lock().unwrap().values() {
                    if send.session_id.as_deref() == Some(session_id.as_str()) && send.host == host {
                        send.cancel.by_peer.store(true, Ordering::Relaxed);
                        send.cancel.token.cancel();
                    }
                }
            }
            ServerEventV2::ListenerFailed { error } => self.inner.emit(CoreEvent::Error {
                message: format!("传输服务已停止：{error}"),
            }),
        }
    }

    fn on_file_upload(
        &mut self,
        session_id: String,
        file_id: String,
        file: FileDto,
        target_tx: oneshot::Sender<FileUploadTarget>,
    ) {
        // Dropping `target_tx` fails the upload request.
        let Some(session) = self.session.as_mut().filter(|s| s.session_id == session_id) else {
            return;
        };
        if !session.files.contains_key(&file_id) {
            return;
        }
        let path = receive_path(&self.inner.settings.save_dir, &file.file_name, &mut session.roots);
        if let Some(parent) = path.parent()
            && let Err(err) = std::fs::create_dir_all(parent)
        {
            self.inner.emit(CoreEvent::Error {
                message: format!("无法创建目录 {}：{err}", parent.display()),
            });
            return;
        }
        session.paths.insert(file_id.clone(), path.clone());
        session.in_flight.insert(file_id.clone());

        let (progress_tx, mut progress_rx) = mpsc::channel::<u64>(16);
        tokio::spawn({
            let files_tx = self.files_tx.clone();
            let (session_id, file_id) = (session_id.clone(), file_id.clone());
            async move {
                while let Some(written) = progress_rx.recv().await {
                    let _ = files_tx.send(FileEvent::Progress {
                        session_id: session_id.clone(),
                        file_id: file_id.clone(),
                        written,
                    });
                }
            }
        });

        let (result_tx, result_rx) = oneshot::channel::<Result<(), String>>();
        tokio::spawn({
            let files_tx = self.files_tx.clone();
            async move {
                let result = result_rx
                    .await
                    .unwrap_or_else(|_| Err("upload aborted".to_string()));
                let _ = files_tx.send(FileEvent::Result {
                    session_id,
                    file_id,
                    result,
                });
            }
        });

        let _ = target_tx.send(FileUploadTarget::Path {
            path,
            result_tx,
            progress_tx: Some(progress_tx),
        });
    }

    fn on_file_event(&mut self, event: FileEvent) {
        match event {
            FileEvent::Progress {
                session_id,
                file_id,
                written,
            } => {
                let Some(session) = self.session.as_mut().filter(|s| s.session_id == session_id)
                else {
                    return;
                };
                session.written.insert(file_id, written);
                if session.last_progress.elapsed() >= PROGRESS_INTERVAL {
                    session.last_progress = Instant::now();
                    self.inner.emit(CoreEvent::TransferProgress {
                        id: session.transfer_id.clone(),
                        direction: Direction::Receive,
                        device_id: session.fingerprint.clone(),
                        bytes: session.bytes(),
                        total: session.total,
                    });
                }
            }
            FileEvent::Result {
                session_id,
                file_id,
                result,
            } => {
                let Some(session) = self.session.as_mut().filter(|s| s.session_id == session_id)
                else {
                    return;
                };
                session.in_flight.remove(&file_id);
                match result {
                    Ok(()) => {
                        let size = session.files.get(&file_id).map_or(0, |file| file.size);
                        session.written.insert(file_id.clone(), size);
                        session.finished.insert(file_id);
                    }
                    Err(err) => {
                        if let Some(path) = session.paths.get(&file_id) {
                            let _ = std::fs::remove_file(path);
                        }
                        let name = session
                            .files
                            .get(&file_id)
                            .map_or(file_id.clone(), |file| file.file_name.clone());
                        session.failed.insert(file_id, format!("{name}：{err}"));
                    }
                }
                self.finish_if_done();
            }
        }
    }

    /// Reports the result and clears the session once the server reported its
    /// end and no file is in flight anymore.
    fn finish_if_done(&mut self) {
        let done = self
            .session
            .as_ref()
            .is_some_and(|s| s.ended.is_some() && s.in_flight.is_empty());
        if !done {
            return;
        }
        let session = self.session.take().unwrap();
        self.inner.queue.session_ended(&session.fingerprint);
        let (status, error) = match session.ended.unwrap() {
            SessionEndReasonV2::Cancelled => {
                self.delete_unfinished(&session);
                (TransferStatus::Cancelled, Some("发送方取消了传输".to_string()))
            }
            SessionEndReasonV2::Finished
                if session.failed.is_empty() && session.finished.len() == session.files.len() =>
            {
                (TransferStatus::Done, None)
            }
            SessionEndReasonV2::Finished => {
                self.delete_unfinished(&session);
                let failed = session.files.len() - session.finished.len();
                let mut error = format!("{failed} 个文件没有收到");
                if let Some(reason) = session.failed.values().next() {
                    error.push_str(&format!("（{reason}）"));
                }
                (TransferStatus::Failed, Some(error))
            }
        };
        self.inner.emit(CoreEvent::TransferFinished {
            id: session.transfer_id.clone(),
            direction: Direction::Receive,
            device_id: session.fingerprint.clone(),
            alias: session.alias.clone(),
            status,
            files_done: session.finished.len(),
            bytes: session
                .finished
                .iter()
                .filter_map(|id| session.files.get(id))
                .map(|file| file.size)
                .sum(),
            saved_paths: self.top_level_paths(&session),
            error,
        });
    }

    /// The node stops while a transfer is running: discard the partial files.
    fn abort_session(&mut self) {
        if let Some(session) = self.session.take() {
            self.inner.queue.session_ended(&session.fingerprint);
            self.delete_unfinished(&session);
            let server = self.server.clone();
            let session_id = session.session_id.clone();
            tokio::spawn(async move {
                server.cancel_v2_session(&session_id).await;
            });
        }
    }

    fn delete_unfinished(&self, session: &ReceiveSession) {
        for (file_id, path) in &session.paths {
            if !session.finished.contains(file_id) {
                let _ = std::fs::remove_file(path);
            }
        }
    }

    /// The saved files and folders right under the save directory, in the
    /// order they were first written.
    fn top_level_paths(&self, session: &ReceiveSession) -> Vec<PathBuf> {
        let save_dir = &self.inner.settings.save_dir;
        let mut ids: Vec<&String> = session.finished.iter().collect();
        ids.sort_by_key(|id| session.files.get(*id).map(|file| &file.file_name));
        let mut paths: Vec<PathBuf> = Vec::new();
        for id in ids {
            let Some(path) = session.paths.get(id) else {
                continue;
            };
            let top = match path.strip_prefix(save_dir).ok().and_then(|rel| rel.components().next()) {
                Some(Component::Normal(name)) => save_dir.join(name),
                _ => path.clone(),
            };
            if !paths.contains(&top) {
                paths.push(top);
            }
        }
        paths
    }
}

/// A peer-supplied name, safe to print on one line.
fn single_line(text: &str) -> String {
    text.chars()
        .map(|c| if c.is_control() { ' ' } else { c })
        .collect::<String>()
        .trim()
        .to_string()
}
