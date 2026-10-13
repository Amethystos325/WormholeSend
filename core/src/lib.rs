//! Wormhole Send core: identity, trust store, discovery and transfers, on top
//! of the LocalSend protocol v2 (the `localsend` crate), which LocalSend
//! devices speak too.
//!
//! A transfer request is accepted without asking exactly when the sender's
//! certificate fingerprint, verified in the mTLS handshake, is in the trust
//! store. A LocalSend device, which cannot pair, waits while the user is
//! asked. Everything else is declined.

#[macro_use]
pub mod i18n;
pub mod config;
mod control;
pub mod event;
pub mod identity;
mod node;
mod pairing;
mod queue;
mod receive;
mod save_path;
mod send;
pub mod trust;

pub use node::{DeviceInfo, Kind, Node, NodeOptions};
