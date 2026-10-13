//! Events the core reports to the shell (the CLI now, the native apps later).
//! Serialized as JSON with an `event` tag, as the FFI will hand them over.

use crate::node::Kind;
use serde::Serialize;
use std::path::PathBuf;

#[derive(Clone, Debug, Serialize)]
#[serde(tag = "event", rename_all_fields = "camelCase")]
pub enum CoreEvent {
    /// A device was seen, or a paired or LocalSend device went online or
    /// offline.
    #[serde(rename = "device.updated")]
    DeviceUpdated {
        id: String,
        alias: String,
        device_type: Option<String>,
        kind: Kind,
        online: bool,
        paired: bool,
    },

    /// Sending to a device that is not paired (LocalSend): waiting for its
    /// user to accept. `transfer.started` follows once they did, or
    /// `transfer.finished` with status `declined`.
    #[serde(rename = "transfer.waiting")]
    TransferWaiting {
        id: String,
        device_id: String,
        alias: String,
    },

    #[serde(rename = "transfer.started")]
    TransferStarted {
        id: String,
        direction: Direction,
        device_id: String,
        alias: String,
        files: usize,
        total: u64,
    },

    #[serde(rename = "transfer.progress")]
    TransferProgress {
        id: String,
        direction: Direction,
        device_id: String,
        bytes: u64,
        total: u64,
        /// Files completely transferred so far (of `files` in `transfer.started`).
        files_done: usize,
    },

    #[serde(rename = "transfer.finished")]
    TransferFinished {
        id: String,
        direction: Direction,
        device_id: String,
        alias: String,
        status: TransferStatus,
        files_done: usize,
        bytes: u64,
        /// For a receive: the top-level files and folders that were saved.
        saved_paths: Vec<PathBuf>,
        error: Option<String>,
    },

    /// A device that is not paired (LocalSend) asks to send: accept or
    /// decline with `respond_receive` within a minute. A text message
    /// carries its `text`; accepting it transfers nothing (the shell shows
    /// or copies the text), accepting files starts a `transfer.started`.
    #[serde(rename = "receive.request")]
    ReceiveRequest {
        request_id: String,
        device_id: String,
        alias: String,
        device_type: Option<String>,
        /// What is offered, e.g. "IMG_0001.jpg 等 3 个文件".
        label: String,
        files: usize,
        total: u64,
        text: Option<String>,
    },

    /// A `receive.request` closed without an answer: `timeout` (declined
    /// after a minute) or `cancelled` (the sender gave up).
    #[serde(rename = "receive.closed")]
    ReceiveClosed { request_id: String, reason: String },

    /// A device that is not paired was declined without asking: a Wormhole
    /// Send device (it has to pair again), or a LocalSend device while
    /// another transfer was running or waiting.
    #[serde(rename = "receive.rejected")]
    ReceiveRejected {
        device_id: String,
        alias: String,
        ip: String,
    },

    /// Waiting for this device's turn in the receiver's queue.
    #[serde(rename = "transfer.queued")]
    TransferQueued {
        id: String,
        device_id: String,
        position: usize,
    },

    /// How many paired devices wait to send to this one.
    #[serde(rename = "receive.waiting")]
    ReceiveWaiting { count: usize },

    /// Another device asks to pair; its code follows as `pair.code`.
    #[serde(rename = "pair.incoming")]
    PairIncoming {
        pair_id: String,
        device_id: String,
        alias: String,
        device_type: Option<String>,
    },

    /// The 6-digit code to compare with the other device's screen, then
    /// accept or decline.
    #[serde(rename = "pair.code")]
    PairCode {
        pair_id: String,
        device_id: String,
        alias: String,
        code: String,
    },

    #[serde(rename = "pair.result")]
    PairResult {
        pair_id: String,
        device_id: String,
        alias: String,
        ok: bool,
        reason: Option<String>,
    },

    /// The other device unpaired this one.
    #[serde(rename = "device.unpaired")]
    DeviceUnpaired { id: String, alias: String },

    #[serde(rename = "error")]
    Error { message: String },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Direction {
    Send,
    Receive,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum TransferStatus {
    Done,
    Failed,
    Cancelled,
    /// The receiver does not trust this device (HTTP 403).
    Rejected,
    /// The receiver's user declined (HTTP 403 from a device that is not
    /// paired, which asks every time).
    Declined,
    /// The receiver is busy with another transfer (HTTP 409).
    Busy,
    /// The device could not be reached.
    Offline,
}
