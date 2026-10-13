//! The control service: HTTPS with mandatory client certificates on the
//! transfer port + 1. Pairing requests are open to any device; unpair
//! notices and the receive queue only to paired ones.

use super::dto::{API, ErrorBody, Info, PairConfirm, PairRequest, PairReveal, QueueTicket};
use super::tls;
use crate::node::Inner;
use crate::pairing;
use bytes::Bytes;
use http_body_util::{BodyExt, Full, Limited};
use hyper::body::Incoming;
use hyper::header::CONTENT_TYPE;
use hyper::service::service_fn;
use hyper::{Method, Request, Response, StatusCode};
use hyper_util::rt::TokioIo;
use serde::Serialize;
use serde::de::DeserializeOwned;
use socket2::{Domain, Protocol, Socket, Type};
use std::convert::Infallible;
use std::net::{Ipv4Addr, Ipv6Addr, SocketAddr};
use std::sync::Arc;
use std::time::Duration;
use tokio::task::JoinHandle;
use tokio_rustls::TlsAcceptor;

const MAX_BODY: usize = 64 * 1024;
const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(10);

/// The device on the other end of a request, as proven by its certificate.
#[derive(Clone, Debug)]
pub(crate) struct Peer {
    pub fingerprint: String,
    pub addr: SocketAddr,
}

impl Peer {
    /// The peer's address as discovery writes hosts: an IPv6 link-local
    /// address keeps its scope ID, so it can be dialed back.
    pub fn host(&self) -> String {
        match self.addr {
            SocketAddr::V6(v6) if v6.scope_id() != 0 => format!("{}%{}", v6.ip(), v6.scope_id()),
            addr => addr.ip().to_string(),
        }
    }
}

/// Binds the control port on IPv4 and, where available, IPv6.
pub(crate) fn bind(port: u16) -> std::io::Result<Vec<std::net::TcpListener>> {
    let v4 = std::net::TcpListener::bind((Ipv4Addr::UNSPECIFIED, port))?;
    v4.set_nonblocking(true)?;
    let mut listeners = vec![v4];
    // Peers often know each other by IPv6 link-local address; a missing IPv6
    // stack is not an error.
    if let Ok(v6) = bind_v6_only(port) {
        listeners.push(v6);
    }
    Ok(listeners)
}

fn bind_v6_only(port: u16) -> std::io::Result<std::net::TcpListener> {
    let socket = Socket::new(Domain::IPV6, Type::STREAM, Some(Protocol::TCP))?;
    socket.set_only_v6(true)?;
    socket.bind(&SocketAddr::new(Ipv6Addr::UNSPECIFIED.into(), port).into())?;
    socket.listen(128)?;
    socket.set_nonblocking(true)?;
    Ok(socket.into())
}

pub(crate) fn spawn(inner: Arc<Inner>, listeners: Vec<std::net::TcpListener>) -> anyhow::Result<Vec<JoinHandle<()>>> {
    let acceptor = TlsAcceptor::from(tls::server_config(&inner.identity)?);
    let mut tasks = Vec::new();
    for listener in listeners {
        let listener = tokio::net::TcpListener::from_std(listener)?;
        let (inner, acceptor) = (inner.clone(), acceptor.clone());
        tasks.push(tokio::spawn(async move {
            loop {
                let Ok((stream, address)) = listener.accept().await else {
                    tokio::time::sleep(Duration::from_millis(100)).await;
                    continue;
                };
                let (inner, acceptor) = (inner.clone(), acceptor.clone());
                tokio::spawn(async move {
                    let Ok(Ok(stream)) = tokio::time::timeout(HANDSHAKE_TIMEOUT, acceptor.accept(stream)).await
                    else {
                        return;
                    };
                    let Some(fingerprint) = tls::peer_fingerprint(stream.get_ref().1) else {
                        return;
                    };
                    let peer = Peer { fingerprint, addr: address };
                    let service = service_fn(move |request| {
                        let (inner, peer) = (inner.clone(), peer.clone());
                        async move { Ok::<_, Infallible>(route(&inner, &peer, request).await) }
                    });
                    let _ = hyper::server::conn::http1::Builder::new()
                        .serve_connection(TokioIo::new(stream), service)
                        .await;
                });
            }
        }));
    }
    Ok(tasks)
}

async fn route(inner: &Arc<Inner>, peer: &Peer, request: Request<Incoming>) -> Response<Full<Bytes>> {
    let method = request.method().clone();
    let path = request.uri().path().to_string();
    let query = request.uri().query().unwrap_or_default().to_string();
    let Some(route) = path.strip_prefix(API) else {
        return error(StatusCode::NOT_FOUND, "not found");
    };
    let paired = || inner.trust().contains(&peer.fingerprint);
    match (method, route) {
        (Method::GET, "/info") => json(&Info {
            app: "wormhole-send".to_string(),
            version: env!("CARGO_PKG_VERSION").to_string(),
            alias: inner.identity.alias.clone(),
            fingerprint: inner.identity.fingerprint.clone(),
        }),
        (Method::POST, "/pair/request") => match read::<PairRequest>(request).await {
            Ok(body) => respond(pairing::on_request(inner, peer, body)),
            Err(response) => *response,
        },
        (Method::POST, "/pair/reveal") => match read::<PairReveal>(request).await {
            Ok(body) => respond(pairing::on_reveal(inner, peer, body)),
            Err(response) => *response,
        },
        (Method::POST, "/pair/confirm") => match read::<PairConfirm>(request).await {
            Ok(body) => respond(pairing::on_confirm(inner, peer, body)),
            Err(response) => *response,
        },
        (Method::POST, "/unpair") => {
            pairing::on_unpair(inner, &peer.fingerprint);
            json(&serde_json::Value::Null)
        }
        (_, "/queue/join" | "/queue/wait" | "/queue/leave") if !paired() => {
            error(StatusCode::FORBIDDEN, "本机没有与你配对")
        }
        (Method::POST, "/queue/join") => match inner.queue.join(&peer.fingerprint) {
            Some(status) => json(&status),
            None => error(StatusCode::SERVICE_UNAVAILABLE, "排队人数已满"),
        },
        (Method::GET, "/queue/wait") => {
            let ticket = query.strip_prefix("ticket=").unwrap_or_default();
            match inner.queue.wait(ticket, &peer.fingerprint).await {
                Some(status) => json(&status),
                None => error(StatusCode::NOT_FOUND, "票号不存在或已过期"),
            }
        }
        (Method::POST, "/queue/leave") => match read::<QueueTicket>(request).await {
            Ok(body) => {
                inner.queue.leave(&body.ticket, &peer.fingerprint);
                json(&serde_json::Value::Null)
            }
            Err(response) => *response,
        },
        _ => error(StatusCode::NOT_FOUND, "not found"),
    }
}

/// The JSON body, or the error response to send instead.
async fn read<T: DeserializeOwned>(request: Request<Incoming>) -> Result<T, Box<Response<Full<Bytes>>>> {
    let body = Limited::new(request.into_body(), MAX_BODY)
        .collect()
        .await
        .map_err(|_| Box::new(error(StatusCode::PAYLOAD_TOO_LARGE, "body too large")))?
        .to_bytes();
    serde_json::from_slice(&body).map_err(|e| Box::new(error(StatusCode::BAD_REQUEST, &e.to_string())))
}

fn respond<T: Serialize>(result: Result<T, (StatusCode, String)>) -> Response<Full<Bytes>> {
    match result {
        Ok(body) => json(&body),
        Err((status, message)) => error(status, &message),
    }
}

fn json(body: &impl Serialize) -> Response<Full<Bytes>> {
    let bytes = serde_json::to_vec(body).unwrap_or_default();
    Response::builder()
        .header(CONTENT_TYPE, "application/json")
        .body(Full::new(Bytes::from(bytes)))
        .expect("valid response")
}

fn error(status: StatusCode, message: &str) -> Response<Full<Bytes>> {
    let mut response = json(&ErrorBody { error: message.to_string() });
    *response.status_mut() = status;
    response
}
