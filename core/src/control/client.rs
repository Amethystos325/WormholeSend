//! Calls to another device's control service. Uses hyper over TLS on a plain
//! socket address, so IPv6 link-local addresses with a scope ID (which a URL
//! cannot carry) work like any other.

use super::dto::{API, ErrorBody, PairConfirm, PairRequest, PairRequestResponse, PairReveal, QueueStatus, QueueTicket};
use super::tls;
use crate::identity::Identity;
use bytes::Bytes;
use http_body_util::{BodyExt, Full};
use hyper::header::{CONTENT_TYPE, HOST};
use hyper::{Method, Request};
use hyper_util::rt::TokioIo;
use rustls::pki_types::ServerName;
use serde::Serialize;
use serde::de::{DeserializeOwned, IgnoredAny};
use std::net::{Ipv4Addr, Ipv6Addr, SocketAddr, SocketAddrV6};
use std::time::Duration;
use tokio::net::TcpStream;
use tokio_rustls::TlsConnector;

const TIMEOUT: Duration = Duration::from_secs(5);

/// A long poll waits up to 30 s on the server; leave room for the network.
const WAIT_TIMEOUT: Duration = Duration::from_secs(40);

#[derive(Debug)]
pub(crate) enum ControlError {
    /// The device answered with an error status.
    Status(u16, String),
    /// The device could not be reached (or its certificate did not match).
    Unreachable(String),
}

impl std::fmt::Display for ControlError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ControlError::Status(status, message) if message.is_empty() => write!(f, "HTTP {status}"),
            ControlError::Status(status, message) => write!(f, "{message}（HTTP {status}）"),
            ControlError::Unreachable(reason) => write!(f, "无法连接：{reason}"),
        }
    }
}

/// The control service of a device whose transfer service listens on
/// `host:transfer_port`: same host, the next port.
pub(crate) fn control_addr(host: &str, transfer_port: u16) -> Option<SocketAddr> {
    let port = transfer_port.checked_add(1)?;
    if let Ok(ip) = host.parse::<Ipv4Addr>() {
        return Some(SocketAddr::new(ip.into(), port));
    }
    let host = host.trim_matches(['[', ']']);
    let (address, scope) = match host.split_once('%') {
        Some((address, scope)) => (address, scope.parse::<u32>().ok()?),
        None => (host, 0),
    };
    let ip: Ipv6Addr = address.parse().ok()?;
    Some(SocketAddr::V6(SocketAddrV6::new(ip, port, 0, scope)))
}

/// A connection to one device's control service, pinned to its fingerprint
/// (when known).
pub(crate) struct ControlClient<'a> {
    pub identity: &'a Identity,
    pub addr: SocketAddr,
    pub expected: Option<&'a str>,
}

impl ControlClient<'_> {
    pub async fn pair_request(&self, body: &PairRequest) -> Result<PairRequestResponse, ControlError> {
        self.call(Method::POST, "/pair/request", Some(body), TIMEOUT).await
    }

    pub async fn pair_reveal(&self, body: &PairReveal) -> Result<(), ControlError> {
        self.call::<IgnoredAny>(Method::POST, "/pair/reveal", Some(body), TIMEOUT).await.map(|_| ())
    }

    pub async fn pair_confirm(&self, body: &PairConfirm) -> Result<(), ControlError> {
        self.call::<IgnoredAny>(Method::POST, "/pair/confirm", Some(body), TIMEOUT).await.map(|_| ())
    }

    pub async fn unpair(&self) -> Result<(), ControlError> {
        self.call::<IgnoredAny>(Method::POST, "/unpair", None::<&()>, TIMEOUT).await.map(|_| ())
    }

    pub async fn queue_join(&self) -> Result<QueueStatus, ControlError> {
        self.call(Method::POST, "/queue/join", None::<&()>, TIMEOUT).await
    }

    pub async fn queue_wait(&self, ticket: &str) -> Result<QueueStatus, ControlError> {
        let path = format!("/queue/wait?ticket={ticket}");
        self.call(Method::GET, &path, None::<&()>, WAIT_TIMEOUT).await
    }

    pub async fn queue_leave(&self, ticket: &str) -> Result<(), ControlError> {
        let body = QueueTicket { ticket: ticket.to_string() };
        self.call::<IgnoredAny>(Method::POST, "/queue/leave", Some(&body), TIMEOUT).await.map(|_| ())
    }

    async fn call<T: DeserializeOwned>(
        &self,
        method: Method,
        path: &str,
        body: Option<&impl Serialize>,
        timeout: Duration,
    ) -> Result<T, ControlError> {
        let unreachable = |e: &dyn std::fmt::Display| ControlError::Unreachable(e.to_string());
        let request = async {
            let config = tls::client_config(self.identity, self.expected).map_err(|e| unreachable(&e))?;
            let tcp = TcpStream::connect(self.addr).await.map_err(|e| unreachable(&e))?;
            let stream = TlsConnector::from(config)
                .connect(ServerName::IpAddress(self.addr.ip().into()), tcp)
                .await
                .map_err(|e| unreachable(&e))?;
            let (mut sender, connection) = hyper::client::conn::http1::handshake(TokioIo::new(stream))
                .await
                .map_err(|e| unreachable(&e))?;
            tokio::spawn(async move {
                let _ = connection.await;
            });
            let bytes = match body {
                Some(body) => serde_json::to_vec(body).map_err(|e| unreachable(&e))?,
                None => Vec::new(),
            };
            let request = Request::builder()
                .method(method)
                .uri(format!("{API}{path}"))
                .header(HOST, "wormhole-send")
                .header(CONTENT_TYPE, "application/json")
                .body(Full::new(Bytes::from(bytes)))
                .map_err(|e| unreachable(&e))?;
            let response = sender.send_request(request).await.map_err(|e| unreachable(&e))?;
            let status = response.status();
            let body = response
                .into_body()
                .collect()
                .await
                .map_err(|e| unreachable(&e))?
                .to_bytes();
            if !status.is_success() {
                let message = serde_json::from_slice::<ErrorBody>(&body)
                    .map(|e| e.error)
                    .unwrap_or_default();
                return Err(ControlError::Status(status.as_u16(), message));
            }
            let body: &[u8] = if body.is_empty() { b"null" } else { &body };
            serde_json::from_slice(body).map_err(|e| unreachable(&e))
        };
        tokio::time::timeout(timeout, request)
            .await
            .unwrap_or_else(|_| Err(ControlError::Unreachable("超时".to_string())))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn control_addresses() {
        assert_eq!(control_addr("192.168.1.5", 53320), Some("192.168.1.5:53321".parse().unwrap()));
        let v6 = control_addr("fe80::1%10", 53320).unwrap();
        match v6 {
            SocketAddr::V6(v6) => {
                assert_eq!(v6.port(), 53321);
                assert_eq!(v6.scope_id(), 10);
            }
            _ => panic!("expected IPv6"),
        }
        assert!(control_addr("fe80::1%en0", 53320).is_none());
        assert!(control_addr("not an address", 53320).is_none());
    }
}
