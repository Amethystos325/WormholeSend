//! Two-way pairing with a 6-digit code, and unpairing.
//!
//! ① The initiator A sends its commitment SHA-256(n_A); ② the responder B
//! answers with n_B; ③ A reveals n_A and B checks it against the commitment.
//! ④ Both derive the code from both certificate fingerprints and both
//! nonces; ⑤ both users compare the codes and confirm; ⑥ each side tells the
//! other; ⑦ once both have confirmed, each side trusts the other's
//! fingerprint. Committing first keeps a man in the middle from trying
//! nonces until the two codes happen to match.

use crate::config::write_atomic;
use crate::control::client::{ControlClient, control_addr};
use crate::control::dto::{PairConfirm, PairRequest, PairRequestResponse, PairReveal};
use crate::control::server::Peer;
use crate::event::CoreEvent;
use crate::node::{Inner, Kind, parse_type, type_name};
use crate::trust::{TrustStore, TrustedDevice, normalize_fingerprint};
use anyhow::{Context, anyhow, bail};
use hyper::StatusCode;
use localsend::crypto::hash::sha256;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::Path;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

/// A code nobody confirmed within this time expires.
const CODE_TTL: Duration = Duration::from_secs(60);
/// After declining a device, its new requests are refused for this long.
const DECLINE_COOLDOWN: Duration = Duration::from_secs(30);
const PENDING_FILE: &str = "pending.json";

#[derive(Default)]
pub(crate) struct Pairings {
    state: Mutex<State>,
}

#[derive(Default)]
struct State {
    pairs: HashMap<String, Pair>,
    /// Devices whose requests are refused until the given time.
    cooldown: HashMap<String, Instant>,
}

#[derive(Clone)]
struct Pair {
    id: String,
    initiator: bool,
    /// The other device's fingerprint.
    peer: String,
    alias: String,
    device_type: Option<String>,
    /// The other device's transfer service; its control service is port + 1.
    host: String,
    port: u16,
    nonce: [u8; 32],
    /// The responder keeps the initiator's commitment until the reveal.
    peer_commit: Option<Vec<u8>>,
    code: Option<String>,
    local: Option<bool>,
    remote: Option<bool>,
}

// MARK: - Initiator

/// Starts pairing with a discovered device and returns the pairing ID right
/// away. The code follows as `pair.code` once both sides have it; a failure
/// on the way is reported as `pair.result` with `ok: false`.
pub(crate) fn start(inner: &Arc<Inner>, fingerprint: &str) -> anyhow::Result<String> {
    let peer = normalize_fingerprint(fingerprint);
    if peer == inner.identity.fingerprint {
        bail!(tr!("不能和本机自己配对", "Can't pair with this device itself", "このデバイス自身とはペアリングできません"));
    }
    let device = inner
        .discovery
        .device_by_fingerprint(&peer)
        .with_context(|| tr!("找不到这台设备，确认它在线", "Can't find this device; make sure it's online", "このデバイスが見つかりません。オンラインか確認してください"))?;
    if Kind::of(device.device.device_model.as_deref()) == Kind::LocalSend {
        bail!(tr!("{} 是 LocalSend 设备，不能配对；可以直接发送，对方确认后接收", "{} is a LocalSend device and can't pair; send to it directly, and its user accepts", "{} は LocalSend デバイスのためペアリングできません。直接送信でき、相手が承認すると受信されます", device.device.alias));
    }
    let http = device
        .get_best_channel()
        .and_then(|channel| channel.http())
        .cloned()
        .with_context(|| tr!("这台设备没有可用的地址", "This device has no usable address", "このデバイスには使用できるアドレスがありません"))?;
    let addr = control_addr(&http.host, http.port).with_context(|| tr!("这台设备的地址无效", "This device's address is invalid", "このデバイスのアドレスが無効です"))?;

    let nonce = random_nonce()?;
    let pair = Pair {
        id: uuid::Uuid::new_v4().to_string(),
        initiator: true,
        peer: peer.clone(),
        alias: device.device.alias.clone(),
        device_type: device.device.device_type.as_ref().map(type_name),
        host: http.host.clone(),
        port: http.port,
        nonce,
        peer_commit: None,
        code: None,
        local: None,
        remote: None,
    };
    let id = pair.id.clone();
    {
        let mut state = inner.pairings.state.lock().unwrap();
        if state.pairs.values().any(|p| p.peer == peer) {
            bail!(tr!("正在和这台设备配对", "Already pairing with this device", "このデバイスとペアリング中です"));
        }
        state.pairs.insert(id.clone(), pair.clone());
    }
    let inner = inner.clone();
    tokio::spawn(async move {
        let code = match exchange(&inner, &pair, addr).await {
            Ok(code) => code,
            Err(err) => {
                inner.pairings.state.lock().unwrap().pairs.remove(&pair.id);
                inner.emit(result(&pair, false, Some(&format!("{err:#}"))));
                return;
            }
        };
        if let Some(pair) = inner.pairings.state.lock().unwrap().pairs.get_mut(&pair.id) {
            pair.code = Some(code.clone());
        }
        inner.emit(CoreEvent::PairCode {
            pair_id: pair.id.clone(),
            device_id: pair.peer.clone(),
            alias: pair.alias.clone(),
            code,
        });
        expire_later(&inner, &pair.id);
    });
    Ok(id)
}

/// ① to ③ from the initiator's side; returns the code.
async fn exchange(inner: &Inner, pair: &Pair, addr: std::net::SocketAddr) -> anyhow::Result<String> {
    let client = ControlClient {
        identity: &inner.identity,
        addr,
        expected: Some(&pair.peer),
    };
    let response = client
        .pair_request(&PairRequest {
            pair_id: pair.id.clone(),
            alias: inner.identity.alias.clone(),
            device_type: Some("desktop".to_string()),
            port: inner.identity.port,
            commit: hex(&sha256(&pair.nonce)),
        })
        .await
        .map_err(|err| match err {
            crate::control::client::ControlError::Status(409, _) => anyhow!(tr!("对方正在处理另一个配对请求", "The other device is handling another pairing request", "相手は別のペアリング要求を処理中です")),
            crate::control::client::ControlError::Status(429, _) => anyhow!(tr!("对方刚拒绝过配对，请稍后再试", "The other device just declined pairing; try again later", "相手がペアリングを拒否したばかりです。しばらくしてからやり直してください")),
            other => anyhow!("{other}"),
        })?;
    let peer_nonce = unhex(&response.nonce).filter(|n| n.len() == 32).with_context(|| tr!("对方的响应无效", "The other device's response is invalid", "相手からの応答が無効です"))?;
    client
        .pair_reveal(&PairReveal {
            pair_id: pair.id.clone(),
            nonce: hex(&pair.nonce),
        })
        .await
        .map_err(|err| anyhow!("{err}"))?;
    Ok(code(&inner.identity.fingerprint, &pair.peer, &pair.nonce, &peer_nonce))
}

// MARK: - Responder

pub(crate) fn on_request(
    inner: &Arc<Inner>,
    peer: &Peer,
    body: PairRequest,
) -> Result<PairRequestResponse, (StatusCode, String)> {
    if peer.fingerprint == inner.identity.fingerprint {
        return Err((StatusCode::BAD_REQUEST, tr!("不能和自己配对", "Can't pair with itself", "自分自身とはペアリングできません")));
    }
    let commit = unhex(&body.commit)
        .filter(|c| c.len() == 32)
        .ok_or((StatusCode::BAD_REQUEST, tr!("commit 无效", "Invalid commit", "commit が無効です")))?;
    let nonce = random_nonce().map_err(|e| (StatusCode::INTERNAL_SERVER_ERROR, e.to_string()))?;
    let alias = single_line(&body.alias);
    {
        let mut state = inner.pairings.state.lock().unwrap();
        let now = Instant::now();
        state.cooldown.retain(|_, until| *until > now);
        if state.cooldown.contains_key(&peer.fingerprint) {
            return Err((StatusCode::TOO_MANY_REQUESTS, tr!("刚拒绝过配对，请稍后再试", "Pairing was just declined; try again later", "ペアリングを拒否したばかりです。しばらくしてからやり直してください")));
        }
        if state.pairs.values().any(|p| p.peer == peer.fingerprint) || state.pairs.contains_key(&body.pair_id) {
            return Err((StatusCode::CONFLICT, tr!("已有待处理的配对请求", "A pairing request is already waiting", "保留中のペアリング要求があります")));
        }
        state.pairs.insert(
            body.pair_id.clone(),
            Pair {
                id: body.pair_id.clone(),
                initiator: false,
                peer: peer.fingerprint.clone(),
                alias: alias.clone(),
                device_type: body.device_type.clone(),
                host: peer.host(),
                port: body.port,
                nonce,
                peer_commit: Some(commit),
                code: None,
                local: None,
                remote: None,
            },
        );
    }
    inner.emit(CoreEvent::PairIncoming {
        pair_id: body.pair_id.clone(),
        device_id: peer.fingerprint.clone(),
        alias,
        device_type: body.device_type,
    });
    expire_later(inner, &body.pair_id);
    Ok(PairRequestResponse { nonce: hex(&nonce) })
}

pub(crate) fn on_reveal(
    inner: &Arc<Inner>,
    peer: &Peer,
    body: PairReveal,
) -> Result<serde_json::Value, (StatusCode, String)> {
    let not_found = || (StatusCode::NOT_FOUND, tr!("没有这个配对请求", "No such pairing request", "このペアリング要求はありません"));
    let their_nonce = unhex(&body.nonce)
        .filter(|n| n.len() == 32)
        .ok_or((StatusCode::BAD_REQUEST, tr!("nonce 无效", "Invalid nonce", "nonce が無効です")))?;
    let mut state = inner.pairings.state.lock().unwrap();
    let pair = state
        .pairs
        .get_mut(&body.pair_id)
        .filter(|p| p.peer == peer.fingerprint && !p.initiator && p.code.is_none())
        .ok_or_else(not_found)?;
    if pair.peer_commit.as_deref() != Some(sha256(&their_nonce).as_slice()) {
        let pair = state.pairs.remove(&body.pair_id).expect("pair exists");
        drop(state);
        inner.emit(CoreEvent::PairResult {
            pair_id: pair.id,
            device_id: pair.peer,
            alias: pair.alias,
            ok: false,
            reason: Some(tr!("校验失败，配对已取消", "Verification failed; pairing cancelled", "検証に失敗したため、ペアリングを中止しました")),
        });
        return Err((StatusCode::BAD_REQUEST, tr!("nonce 与 commit 不符", "The nonce doesn't match the commit", "nonce が commit と一致しません")));
    }
    // The initiator's fingerprint comes first.
    let code = code(&pair.peer, &inner.identity.fingerprint, &their_nonce, &pair.nonce);
    pair.code = Some(code.clone());
    let event = CoreEvent::PairCode {
        pair_id: pair.id.clone(),
        device_id: pair.peer.clone(),
        alias: pair.alias.clone(),
        code,
    };
    drop(state);
    inner.emit(event);
    Ok(serde_json::Value::Null)
}

pub(crate) fn on_confirm(
    inner: &Arc<Inner>,
    peer: &Peer,
    body: PairConfirm,
) -> Result<serde_json::Value, (StatusCode, String)> {
    let mut state = inner.pairings.state.lock().unwrap();
    let pair = state
        .pairs
        .get_mut(&body.pair_id)
        .filter(|p| p.peer == peer.fingerprint && p.code.is_some())
        .ok_or((StatusCode::NOT_FOUND, tr!("没有这个配对请求", "No such pairing request", "このペアリング要求はありません")))?;
    pair.remote = Some(body.accept);
    if !body.accept {
        let pair = state.pairs.remove(&body.pair_id).expect("pair exists");
        drop(state);
        inner.emit(result(&pair, false, Some(&tr!("对方拒绝了配对", "The other device declined", "相手がペアリングを拒否しました"))));
        return Ok(serde_json::Value::Null);
    }
    finish_if_confirmed(inner, &mut state, &body.pair_id);
    Ok(serde_json::Value::Null)
}

// MARK: - Both sides

/// This side's user compared the codes.
pub(crate) fn confirm(inner: &Arc<Inner>, pair_id: &str, accept: bool) -> anyhow::Result<()> {
    let pair = {
        let mut state = inner.pairings.state.lock().unwrap();
        let pair = state
            .pairs
            .get_mut(pair_id)
            .filter(|p| p.code.is_some() && p.local.is_none())
            .with_context(|| tr!("没有等待确认的配对", "No pairing is waiting for confirmation", "確認待ちのペアリングはありません"))?;
        pair.local = Some(accept);
        let pair = pair.clone();
        if !accept {
            state.pairs.remove(pair_id);
            if !pair.initiator {
                state.cooldown.insert(pair.peer.clone(), Instant::now() + DECLINE_COOLDOWN);
            }
        }
        pair
    };
    let inner = inner.clone();
    tokio::spawn(async move {
        let delivered = match control_addr(&pair.host, pair.port) {
            Some(addr) => ControlClient {
                identity: &inner.identity,
                addr,
                expected: Some(&pair.peer),
            }
            .pair_confirm(&PairConfirm {
                pair_id: pair.id.clone(),
                accept,
            })
            .await
            .is_ok(),
            None => false,
        };
        if !accept {
            // Reported only after the other side was told, so a short-lived
            // process (whs pair) doesn't exit before the decline reaches it.
            inner.emit(result(&pair, false, Some(&tr!("已拒绝", "Declined", "拒否しました"))));
            return;
        }
        let mut state = inner.pairings.state.lock().unwrap();
        if delivered {
            // Trust only once the other side knows this side accepted.
            finish_if_confirmed(&inner, &mut state, &pair.id);
        } else if state.pairs.remove(&pair.id).is_some() {
            drop(state);
            inner.emit(result(&pair, false, Some(&tr!("无法联系对方", "Can't reach the other device", "相手に接続できません"))));
        }
    });
    Ok(())
}

fn finish_if_confirmed(inner: &Inner, state: &mut State, pair_id: &str) {
    let confirmed = state
        .pairs
        .get(pair_id)
        .is_some_and(|p| p.local == Some(true) && p.remote == Some(true));
    if !confirmed {
        return;
    }
    let pair = state.pairs.remove(pair_id).expect("pair exists");
    let mut device = TrustedDevice::new(&pair.peer, pair.alias.clone());
    device.device_type = pair.device_type.as_deref().and_then(parse_type);
    device.last_host = Some(pair.host.clone());
    device.last_port = Some(pair.port);
    let saved = TrustStore::update(&inner.home, |store| {
        store.add(device);
        (true, ())
    });
    remove_pending(&inner.home, &pair.peer);
    inner.emit(match saved {
        Ok(()) => result(&pair, true, None),
        Err(err) => result(&pair, false, Some(&tr!("无法保存：{err:#}", "Can't save: {err:#}", "保存できません：{err:#}"))),
    });
}

fn expire_later(inner: &Arc<Inner>, pair_id: &str) {
    let (inner, pair_id) = (inner.clone(), pair_id.to_string());
    tokio::spawn(async move {
        tokio::time::sleep(CODE_TTL).await;
        let expired = inner.pairings.state.lock().unwrap().pairs.remove(&pair_id);
        if let Some(pair) = expired {
            inner.emit(result(&pair, false, Some(&tr!("配对已超时", "Pairing timed out", "ペアリングがタイムアウトしました"))));
        }
    });
}

fn result(pair: &Pair, ok: bool, reason: Option<&str>) -> CoreEvent {
    CoreEvent::PairResult {
        pair_id: pair.id.clone(),
        device_id: pair.peer.clone(),
        alias: pair.alias.clone(),
        ok,
        reason: reason.map(str::to_string),
    }
}

/// The code both sides show: the first 4 bytes of SHA-256 over both
/// fingerprints (uppercase hex, initiator first) and both nonces (initiator
/// first), as a big-endian number modulo 10^6, zero-padded to 6 digits.
pub(crate) fn code(initiator: &str, responder: &str, initiator_nonce: &[u8], responder_nonce: &[u8]) -> String {
    let mut input = Vec::new();
    input.extend_from_slice(initiator.as_bytes());
    input.extend_from_slice(responder.as_bytes());
    input.extend_from_slice(initiator_nonce);
    input.extend_from_slice(responder_nonce);
    let hash = sha256(&input);
    let value = u32::from_be_bytes([hash[0], hash[1], hash[2], hash[3]]) % 1_000_000;
    format!("{value:06}")
}

// MARK: - Unpairing

/// Removes a paired device here. Returns it, or `None` when it was not
/// paired; tell it with [`notify_removed`].
pub(crate) fn remove(inner: &Inner, fingerprint: &str) -> anyhow::Result<Option<TrustedDevice>> {
    let fingerprint = normalize_fingerprint(fingerprint);
    TrustStore::update(&inner.home, |store| {
        let removed = store.remove(&fingerprint);
        (removed.is_some(), removed)
    })
}

/// Tells a removed device it was unpaired. Returns `false` when it could
/// not be told now; it is told when it is seen again.
pub(crate) async fn notify_removed(inner: &Arc<Inner>, device: TrustedDevice) -> anyhow::Result<bool> {
    let fingerprint = device.fingerprint;
    let seen = inner
        .discovery
        .device_by_fingerprint(&fingerprint)
        .and_then(|d| d.get_best_channel().and_then(|c| c.http()).map(|h| (h.host.clone(), h.port)));
    let address = seen.or(device.last_host.zip(device.last_port));
    let notified = match address {
        Some((host, port)) => notify_unpair(inner, &fingerprint, &host, port).await,
        None => false,
    };
    if !notified {
        add_pending(&inner.home, &fingerprint)?;
    }
    Ok(notified)
}

/// The other device unpaired this one.
pub(crate) fn on_unpair(inner: &Inner, fingerprint: &str) {
    let removed = TrustStore::update(&inner.home, |store| {
        let removed = store.remove(fingerprint);
        (removed.is_some(), removed)
    });
    if let Ok(Some(device)) = removed {
        inner.emit(CoreEvent::DeviceUnpaired {
            id: device.fingerprint,
            alias: device.alias,
        });
    }
}

/// A device was seen: deliver an unpair notice it missed.
pub(crate) fn deliver_pending(inner: &Arc<Inner>, fingerprint: &str, host: &str, port: u16) {
    if !load_pending(&inner.home).unpair_notices.iter().any(|f| f == fingerprint) {
        return;
    }
    let (inner, fingerprint, host) = (inner.clone(), fingerprint.to_string(), host.to_string());
    tokio::spawn(async move {
        if notify_unpair(&inner, &fingerprint, &host, port).await {
            remove_pending(&inner.home, &fingerprint);
        }
    });
}

async fn notify_unpair(inner: &Inner, fingerprint: &str, host: &str, port: u16) -> bool {
    let Some(addr) = control_addr(host, port) else {
        return false;
    };
    ControlClient {
        identity: &inner.identity,
        addr,
        expected: Some(fingerprint),
    }
    .unpair()
    .await
    .is_ok()
}

/// `pending.json`: notices still to deliver.
#[derive(Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Pending {
    #[serde(default)]
    unpair_notices: Vec<String>,
}

fn load_pending(home: &Path) -> Pending {
    std::fs::read_to_string(home.join(PENDING_FILE))
        .ok()
        .and_then(|text| serde_json::from_str(&text).ok())
        .unwrap_or_default()
}

fn save_pending(home: &Path, pending: &Pending) -> anyhow::Result<()> {
    write_atomic(&home.join(PENDING_FILE), serde_json::to_string_pretty(pending)?.as_bytes())?;
    Ok(())
}

fn add_pending(home: &Path, fingerprint: &str) -> anyhow::Result<()> {
    let mut pending = load_pending(home);
    if !pending.unpair_notices.iter().any(|f| f == fingerprint) {
        pending.unpair_notices.push(fingerprint.to_string());
        save_pending(home, &pending)?;
    }
    Ok(())
}

fn remove_pending(home: &Path, fingerprint: &str) {
    let mut pending = load_pending(home);
    let before = pending.unpair_notices.len();
    pending.unpair_notices.retain(|f| f != fingerprint);
    if pending.unpair_notices.len() != before {
        let _ = save_pending(home, &pending);
    }
}

// MARK: - Helpers

fn random_nonce() -> anyhow::Result<[u8; 32]> {
    let mut nonce = [0u8; 32];
    getrandom::fill(&mut nonce).map_err(|e| anyhow!("no randomness: {e}"))?;
    Ok(nonce)
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn unhex(text: &str) -> Option<Vec<u8>> {
    if !text.len().is_multiple_of(2) {
        return None;
    }
    (0..text.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(text.get(i..i + 2)?, 16).ok())
        .collect()
}

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

    #[test]
    fn code_is_six_digits_and_order_sensitive() {
        let (a, b) = ("A".repeat(64), "B".repeat(64));
        let (na, nb) = ([1u8; 32], [2u8; 32]);
        let code1 = code(&a, &b, &na, &nb);
        assert_eq!(code1.len(), 6);
        assert!(code1.chars().all(|c| c.is_ascii_digit()));
        assert_eq!(code1, code(&a, &b, &na, &nb));
        assert_ne!(code1, code(&b, &a, &nb, &na));
    }

    #[test]
    fn hex_round_trip() {
        let bytes = [0u8, 1, 0xab, 0xff];
        assert_eq!(unhex(&hex(&bytes)).unwrap(), bytes);
        assert!(unhex("abc").is_none());
        assert!(unhex("zz").is_none());
    }
}
