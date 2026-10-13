//! Request and response bodies of the control service (`/api/wormhole/v1`).

use serde::{Deserialize, Serialize};

pub(crate) const API: &str = "/api/wormhole/v1";

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Info {
    pub app: String,
    pub version: String,
    pub alias: String,
    pub fingerprint: String,
}

/// ① The initiator asks to pair and commits to its nonce.
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct PairRequest {
    pub pair_id: String,
    pub alias: String,
    pub device_type: Option<String>,
    /// The initiator's transfer port; its control port is this + 1.
    pub port: u16,
    /// SHA-256 of the initiator's nonce, hex.
    pub commit: String,
}

/// ② The responder answers with its own nonce.
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct PairRequestResponse {
    pub nonce: String,
}

/// ③ The initiator reveals its nonce.
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct PairReveal {
    pub pair_id: String,
    pub nonce: String,
}

/// ⑥ This side's user compared the codes and accepted or declined.
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct PairConfirm {
    pub pair_id: String,
    pub accept: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct QueueStatus {
    pub ticket: String,
    /// 0 once it is this sender's turn.
    pub position: usize,
    pub ready: bool,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct QueueTicket {
    pub ticket: String,
}

#[derive(Debug, Serialize, Deserialize)]
pub(crate) struct ErrorBody {
    pub error: String,
}
