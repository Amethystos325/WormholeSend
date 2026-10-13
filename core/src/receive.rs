//! The node's event loop: server and discovery events, and the receive side
//! of transfers. A request is accepted without asking exactly when the
//! sender's verified certificate fingerprint is in the trust store. A
//! LocalSend device, which cannot pair, waits while this device's user is
//! asked; any other request is declined (HTTP 403).

use crate::i18n::Lang;
use crate::event::{CoreEvent, Direction, TransferStatus};
use crate::node::{Inner, Kind, type_name};
use crate::save_path::receive_path;
use crate::trust::normalize_fingerprint;
use localsend::discovery::DiscoveryEvent;
use localsend::http::server::ServerHandle;
use localsend::http::server::common::save::FileUploadTarget;
use localsend::http::server::v2::{PrepareUploadDecisionV2, ServerEventV2, SessionEndReasonV2};
use localsend::model::discovery::DeviceType;
use localsend::model::transfer::FileDto;
use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::{Component, PathBuf};
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};
use tokio::sync::{mpsc, oneshot};

const PROGRESS_INTERVAL: Duration = Duration::from_millis(250);

/// A `receive.request` nobody answered is declined after this long.
const ASK_TIMEOUT: Duration = Duration::from_secs(60);

/// The user's answer to a `receive.request`, or the timeout standing in.
pub(crate) enum Answer {
    User { request_id: String, accept: bool },
    Timeout { request_id: String },
}

/// A request from a LocalSend device, waiting for the user's answer. The
/// server holds its single session slot meanwhile.
struct Asking {
    request_id: String,
    session_id: String,
    fingerprint: String,
    alias: String,
    files: HashMap<String, FileDto>,
    /// A text message: accepting it transfers nothing.
    text: bool,
    decision_tx: oneshot::Sender<PrepareUploadDecisionV2>,
}

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
    mut answers_rx: mpsc::UnboundedReceiver<Answer>,
    mut stop_rx: oneshot::Receiver<()>,
) {
    let (files_tx, mut files_rx) = mpsc::unbounded_channel::<FileEvent>();
    let mut receiver = Receiver {
        inner,
        server,
        files_tx,
        session: None,
        asking: None,
    };
    loop {
        tokio::select! {
            _ = &mut stop_rx => break,
            Some(event) = server_rx.recv() => receiver.on_server_event(event),
            Some(event) = discovery_rx.recv() => receiver.on_discovery_event(event),
            Some(event) = files_rx.recv() => receiver.on_file_event(event),
            Some(answer) = answers_rx.recv() => receiver.on_answer(answer),
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
    asking: Option<Asking>,
}

impl Receiver {
    fn on_discovery_event(&mut self, event: DiscoveryEvent) {
        match event {
            DiscoveryEvent::Discovered { device } => self.inner.on_device_seen(&device, true),
            DiscoveryEvent::Updated { device } => self.inner.on_device_seen(&device, false),
            DiscoveryEvent::MulticastFailed => self.inner.emit(CoreEvent::Error {
                message: tr!("组播已停止，只能通过已知地址找到设备", "Multicast stopped; devices can only be found at known addresses", "マルチキャストが停止しました。既知のアドレスでのみデバイスを検出できます"),
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
                let alias = single_line(&info.alias);
                // Client certificates are mandatory, so a request without a
                // verified fingerprint should not exist; decline it anyway.
                let Some(fingerprint) = cert_fingerprint.as_deref().map(normalize_fingerprint) else {
                    let _ = decision_tx.send(PrepareUploadDecisionV2::Decline);
                    self.inner.emit(CoreEvent::ReceiveRejected {
                        device_id: String::new(),
                        alias,
                        ip: ip.to_string(),
                    });
                    return;
                };
                let trusted = self.inner.trust().contains(&fingerprint);
                // A Wormhole Send device that is not paired thinks it still
                // is: the 403 tells it to pair again.
                let ask = !trusted && Kind::of(info.device_model.as_deref()) == Kind::LocalSend;
                // One session at a time, in queue order (see `queue`).
                if !(trusted || ask) || !self.inner.queue.admit(&fingerprint) {
                    let _ = decision_tx.send(PrepareUploadDecisionV2::Decline);
                    self.inner.emit(match trusted {
                        true => CoreEvent::Error {
                            message: tr!("{alias} 没有排队就发送，已拒绝", "{alias} sent without queueing; declined", "{alias} が順番待ちをせずに送信したため拒否しました"),
                        },
                        false => CoreEvent::ReceiveRejected {
                            device_id: fingerprint,
                            alias,
                            ip: ip.to_string(),
                        },
                    });
                    return;
                }
                let text = message_text(&files).is_some();
                match ask {
                    true => self.ask(session_id, fingerprint, alias, info.device_type, files, decision_tx),
                    false => self.accept(session_id, fingerprint, alias, files, text, decision_tx),
                }
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
            // The sender gave up while this device's user was being asked.
            ServerEventV2::PrepareUploadAborted { session_id } => {
                if let Some(asking) = self.asking.take_if(|a| a.session_id == session_id) {
                    self.close_asking(&asking);
                    self.inner.emit(CoreEvent::ReceiveClosed {
                        request_id: asking.request_id,
                        reason: "cancelled".to_string(),
                    });
                }
            }
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
                message: tr!("传输服务已停止：{error}", "The transfer service stopped: {error}", "転送サービスが停止しました：{error}"),
            }),
        }
    }

    /// Accepts every file (or, for a text message, nothing: the text came
    /// with the request) and starts the session.
    fn accept(
        &mut self,
        session_id: String,
        fingerprint: String,
        alias: String,
        files: HashMap<String, FileDto>,
        text: bool,
        decision_tx: oneshot::Sender<PrepareUploadDecisionV2>,
    ) {
        if text {
            let _ = decision_tx.send(PrepareUploadDecisionV2::Accept(HashSet::new()));
            self.inner.queue.session_ended(&fingerprint);
            return;
        }
        if let Err(err) = std::fs::create_dir_all(&self.inner.settings.save_dir) {
            self.inner.queue.session_ended(&fingerprint);
            let _ = decision_tx.send(PrepareUploadDecisionV2::Decline);
            self.inner.emit(CoreEvent::Error {
                message: tr!(
                    "无法创建保存目录 {}：{err}",
                    "Can't create the save folder {}: {err}",
                    "保存先フォルダ {} を作成できません：{err}",
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

    /// Asks this device's user about a LocalSend device's request; declined
    /// when nobody answers in time.
    fn ask(
        &mut self,
        session_id: String,
        fingerprint: String,
        alias: String,
        device_type: Option<DeviceType>,
        files: HashMap<String, FileDto>,
        decision_tx: oneshot::Sender<PrepareUploadDecisionV2>,
    ) {
        if let Some(previous) = self.asking.take() {
            // The server holds one request at a time, so this is stale.
            self.close_asking(&previous);
        }
        let request_id = uuid::Uuid::new_v4().to_string();
        let text = message_text(&files);
        self.inner.emit(CoreEvent::ReceiveRequest {
            request_id: request_id.clone(),
            device_id: fingerprint.clone(),
            alias: alias.clone(),
            device_type: device_type.as_ref().map(type_name),
            label: match text {
                Some(_) => tr!("一段文字", "Text message", "テキストメッセージ"),
                None => label(&files),
            },
            files: files.len(),
            total: files.values().map(|file| file.size).sum(),
            text: text.clone(),
        });
        *self.inner.asking.lock().unwrap() = Some(request_id.clone());
        tokio::spawn({
            let (answers, request_id) = (self.inner.answers.clone(), request_id.clone());
            async move {
                tokio::time::sleep(ASK_TIMEOUT).await;
                let _ = answers.send(Answer::Timeout { request_id });
            }
        });
        self.asking = Some(Asking {
            request_id,
            session_id,
            fingerprint,
            alias,
            files,
            text: text.is_some(),
            decision_tx,
        });
    }

    fn on_answer(&mut self, answer: Answer) {
        let (request_id, accept) = match &answer {
            Answer::User { request_id, accept } => (request_id, *accept),
            Answer::Timeout { request_id } => (request_id, false),
        };
        let Some(asking) = self.asking.take_if(|a| a.request_id == *request_id) else {
            return;
        };
        *self.inner.asking.lock().unwrap() = None;
        if let Answer::Timeout { .. } = answer {
            self.inner.emit(CoreEvent::ReceiveClosed {
                request_id: asking.request_id.clone(),
                reason: "timeout".to_string(),
            });
        }
        match accept {
            true => self.accept(
                asking.session_id,
                asking.fingerprint,
                asking.alias,
                asking.files,
                asking.text,
                asking.decision_tx,
            ),
            false => {
                let _ = asking.decision_tx.send(PrepareUploadDecisionV2::Decline);
                self.inner.queue.session_ended(&asking.fingerprint);
            }
        }
    }

    /// Ends a request that is no longer asked about; its decision, if the
    /// sender still waits, is a decline (by dropping it).
    fn close_asking(&self, asking: &Asking) {
        let mut open = self.inner.asking.lock().unwrap();
        if open.as_deref() == Some(asking.request_id.as_str()) {
            *open = None;
        }
        drop(open);
        self.inner.queue.session_ended(&asking.fingerprint);
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
                message: tr!("无法创建目录 {}：{err}", "Can't create the folder {}: {err}", "フォルダ {} を作成できません：{err}", parent.display()),
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
                        files_done: session.finished.len(),
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
                (TransferStatus::Cancelled, Some(tr!("发送方取消了传输", "The sender cancelled the transfer", "送信者が転送をキャンセルしました")))
            }
            SessionEndReasonV2::Finished
                if session.failed.is_empty() && session.finished.len() == session.files.len() =>
            {
                (TransferStatus::Done, None)
            }
            SessionEndReasonV2::Finished => {
                self.delete_unfinished(&session);
                let failed = session.files.len() - session.finished.len();
                let mut error = match failed {
                    1 => tr!("1 个文件没有收到", "1 file wasn't received", "1 個のファイルを受信できませんでした"),
                    _ => tr!("{failed} 个文件没有收到", "{failed} files weren't received", "{failed} 個のファイルを受信できませんでした"),
                };
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
        if let Some(asking) = self.asking.take() {
            self.close_asking(&asking);
            let _ = asking.decision_tx.send(PrepareUploadDecisionV2::Decline);
        }
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

/// The text of a LocalSend text message: a single text "file" whose content
/// is the preview.
fn message_text(files: &HashMap<String, FileDto>) -> Option<String> {
    let mut iter = files.values();
    let (Some(file), None) = (iter.next(), iter.next()) else {
        return None;
    };
    let text = file.file_type.starts_with("text/") || file.file_type == "text";
    file.preview.clone().filter(|_| text)
}

/// What a request offers, as the shells label a drag, by the top-level
/// entries: "IMG_0001.jpg", "文件夹 照片", "IMG_0001.jpg 等 3 个文件",
/// "a 等 2 个文件夹" or "a 等 3 个文件或文件夹"; "照片 (folder)",
/// "IMG_0001.jpg and 2 more files", "a and 1 more folder", "a and 2 more
/// items"; "フォルダ「照片」", "IMG_0001.jpg など 3 個のファイル", "a など
/// 2 個のフォルダ", "a など 3 個の項目".
fn label(files: &HashMap<String, FileDto>) -> String {
    label_in(crate::i18n::current(), files)
}

fn label_in(lang: Lang, files: &HashMap<String, FileDto>) -> String {
    // Top-level name → whether it is a folder.
    let mut entries: BTreeMap<String, bool> = BTreeMap::new();
    for file in files.values() {
        match file.file_name.trim_start_matches('/').split_once('/') {
            Some((top, _)) => {
                entries.insert(single_line(top), true);
            }
            None => {
                entries.entry(single_line(&file.file_name)).or_insert(false);
            }
        }
    }
    let Some((name, &folder)) = entries.iter().next() else {
        return match lang {
            Lang::Zh => "文件",
            Lang::En => "Files",
            Lang::Ja => "ファイル",
        }
        .to_string();
    };
    let folders = entries.values().filter(|folder| **folder).count();
    let count = entries.len();
    // Files, folders, or both.
    let kind = match folders {
        0 => 0,
        n if n == count => 1,
        _ => 2,
    };
    match (lang, count) {
        (Lang::Zh, 1) if folder => format!("文件夹 {name}"),
        (Lang::En, 1) if folder => format!("{name} (folder)"),
        (Lang::Ja, 1) if folder => format!("フォルダ「{name}」"),
        (_, 1) => name.clone(),
        (Lang::Zh, _) => format!("{name} 等 {count} 个{}", ["文件", "文件夹", "文件或文件夹"][kind]),
        (Lang::Ja, _) => format!("{name} など {count} 個の{}", ["ファイル", "フォルダ", "項目"][kind]),
        (Lang::En, _) => {
            let more = count - 1;
            let plural = if more == 1 { "" } else { "s" };
            format!("{name} and {more} more {}{plural}", ["file", "folder", "item"][kind])
        }
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

#[cfg(test)]
mod tests {
    use super::*;

    fn offer(names: &[(&str, &str, Option<&str>)]) -> HashMap<String, FileDto> {
        names
            .iter()
            .enumerate()
            .map(|(i, (name, file_type, preview))| {
                let file = FileDto {
                    id: i.to_string(),
                    file_name: name.to_string(),
                    size: 1,
                    file_type: file_type.to_string(),
                    sha256: None,
                    preview: preview.map(str::to_string),
                    metadata: None,
                };
                (i.to_string(), file)
            })
            .collect()
    }

    fn files(names: &[&str]) -> HashMap<String, FileDto> {
        offer(&names.iter().map(|name| (*name, "image/jpeg", None)).collect::<Vec<_>>())
    }

    #[test]
    fn labels_by_top_level_entries() {
        assert_eq!(label(&files(&["IMG_2.jpg"])), "IMG_2.jpg");
        assert_eq!(label(&files(&["IMG_2.jpg", "IMG_1.jpg", "IMG_3.jpg"])), "IMG_1.jpg 等 3 个文件");
        assert_eq!(label(&files(&["照片/a.jpg", "照片/b/c.jpg"])), "文件夹 照片");
        assert_eq!(label(&files(&["b/x", "a/y"])), "a 等 2 个文件夹");
        assert_eq!(label(&files(&["b/x", "a.txt"])), "a.txt 等 2 个文件或文件夹");
        let en = |names: &[&str]| label_in(Lang::En, &files(names));
        assert_eq!(en(&["IMG_2.jpg"]), "IMG_2.jpg");
        assert_eq!(en(&["照片/a.jpg"]), "照片 (folder)");
        assert_eq!(en(&["b.jpg", "a.jpg"]), "a.jpg and 1 more file");
        assert_eq!(en(&["c/x", "b/x", "a/y"]), "a and 2 more folders");
        assert_eq!(en(&["b/x", "a.txt"]), "a.txt and 1 more item");
        let ja = |names: &[&str]| label_in(Lang::Ja, &files(names));
        assert_eq!(ja(&["照片/a.jpg"]), "フォルダ「照片」");
        assert_eq!(ja(&["b.jpg", "a.jpg", "c.jpg"]), "a.jpg など 3 個のファイル");
        assert_eq!(ja(&["b/x", "a/y"]), "a など 2 個のフォルダ");
        assert_eq!(ja(&["b/x", "a.txt"]), "a.txt など 2 個の項目");
    }

    #[test]
    fn text_messages_are_a_single_text_file_with_a_preview() {
        assert_eq!(message_text(&offer(&[("m.txt", "text/plain", Some("你好"))])).as_deref(), Some("你好"));
        assert_eq!(message_text(&offer(&[("m.txt", "text", Some("hi"))])).as_deref(), Some("hi"));
        assert_eq!(message_text(&offer(&[("m.txt", "text/plain", None)])), None);
        assert_eq!(message_text(&offer(&[("a.jpg", "image/jpeg", Some("x"))])), None);
        let two = offer(&[("a.txt", "text/plain", Some("a")), ("b.txt", "text/plain", Some("b"))]);
        assert_eq!(message_text(&two), None);
    }
}
