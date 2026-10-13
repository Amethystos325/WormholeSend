//! Wormhole Send core: identity, trust store, discovery and transfers, on top
//! of the LocalSend protocol v2 (the `localsend` crate).
//!
//! A transfer request is accepted without asking exactly when the sender's
//! certificate fingerprint, verified in the mTLS handshake, is in the trust
//! store. Everything else is declined.

pub mod config;
pub mod event;
pub mod identity;
mod node;
mod receive;
mod save_path;
mod send;
pub mod trust;

pub use node::{DeviceInfo, Node, NodeOptions};
