//! Events the core reports to the shell (the CLI now, the native apps later).
//! Serialized as JSON with an `event` tag, as the FFI will hand them over.

use serde::Serialize;
use std::path::PathBuf;

#[derive(Clone, Debug, Serialize)]
#[serde(tag = "event", rename_all_fields = "camelCase")]
pub enum CoreEvent {
    /// A device was seen, or a paired device went online or offline.
    #[serde(rename = "device.updated")]
    DeviceUpdated {
        id: String,
        alias: String,
        device_type: Option<String>,
        online: bool,
        paired: bool,
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

    /// A device that is not paired asked to send files; it was declined.
    #[serde(rename = "receive.rejected")]
    ReceiveRejected {
        device_id: String,
        alias: String,
        ip: String,
    },

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
    /// The receiver is busy with another transfer (HTTP 409).
    Busy,
    /// The device could not be reached.
    Offline,
}
