//! TLS for the control service. Both sides present their device certificate;
//! a device is identified by the SHA-256 fingerprint of its certificate, and
//! the handshake signature proves it holds the matching private key. There
//! is no certificate authority: which fingerprints to trust is decided by
//! the trust store, above this layer.

use crate::identity::Identity;
use anyhow::Context;
use localsend::crypto::cert::fingerprint_from_cert_der;
use rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier};
use rustls::crypto::WebPkiSupportedAlgorithms;
use rustls::pki_types::pem::PemObject;
use rustls::pki_types::{CertificateDer, PrivateKeyDer, ServerName, UnixTime};
use rustls::server::danger::{ClientCertVerified, ClientCertVerifier};
use rustls::{DigitallySignedStruct, DistinguishedName, Error, SignatureScheme};
use std::sync::Arc;

fn algorithms() -> WebPkiSupportedAlgorithms {
    rustls::crypto::ring::default_provider().signature_verification_algorithms
}

fn certified_key(identity: &Identity) -> anyhow::Result<(Vec<CertificateDer<'static>>, PrivateKeyDer<'static>)> {
    let cert = CertificateDer::from_pem_slice(identity.cert_pem.as_bytes()).context("invalid certificate")?;
    let key = PrivateKeyDer::from_pem_slice(identity.key_pem.as_bytes()).context("invalid private key")?;
    Ok((vec![cert], key))
}

/// Server side: every client must present a certificate.
pub(crate) fn server_config(identity: &Identity) -> anyhow::Result<Arc<rustls::ServerConfig>> {
    let _ = rustls::crypto::ring::default_provider().install_default();
    let (certs, key) = certified_key(identity)?;
    let mut config = rustls::ServerConfig::builder()
        .with_client_cert_verifier(Arc::new(AnyClientCert { algorithms: algorithms() }))
        .with_single_cert(certs, key)?;
    config.alpn_protocols = vec![b"http/1.1".to_vec()];
    Ok(Arc::new(config))
}

/// Client side: presents this device's certificate and, when `expected` is
/// given, accepts only a server with that fingerprint.
pub(crate) fn client_config(identity: &Identity, expected: Option<&str>) -> anyhow::Result<Arc<rustls::ClientConfig>> {
    let _ = rustls::crypto::ring::default_provider().install_default();
    let (certs, key) = certified_key(identity)?;
    let mut config = rustls::ClientConfig::builder()
        .dangerous()
        .with_custom_certificate_verifier(Arc::new(PinnedServer {
            expected: expected.map(|fp| fp.to_ascii_uppercase()),
            algorithms: algorithms(),
        }))
        .with_client_auth_cert(certs, key)?;
    config.alpn_protocols = vec![b"http/1.1".to_vec()];
    Ok(Arc::new(config))
}

/// The fingerprint of the certificate the peer presented in the handshake.
pub(crate) fn peer_fingerprint(connection: &rustls::CommonState) -> Option<String> {
    connection
        .peer_certificates()
        .and_then(|certs| certs.first())
        .map(|cert| fingerprint_from_cert_der(cert.as_ref()))
}

#[derive(Debug)]
struct AnyClientCert {
    algorithms: WebPkiSupportedAlgorithms,
}

impl ClientCertVerifier for AnyClientCert {
    fn offer_client_auth(&self) -> bool {
        true
    }

    fn client_auth_mandatory(&self) -> bool {
        true
    }

    fn root_hint_subjects(&self) -> &[DistinguishedName] {
        &[]
    }

    fn verify_client_cert(
        &self,
        _end_entity: &CertificateDer<'_>,
        _intermediates: &[CertificateDer<'_>],
        _now: UnixTime,
    ) -> Result<ClientCertVerified, Error> {
        // Any certificate: what it is trusted for is decided by its
        // fingerprint. Possession of its key is checked by the signatures below.
        Ok(ClientCertVerified::assertion())
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, Error> {
        rustls::crypto::verify_tls12_signature(message, cert, dss, &self.algorithms)
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, Error> {
        rustls::crypto::verify_tls13_signature(message, cert, dss, &self.algorithms)
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.algorithms.supported_schemes()
    }
}

#[derive(Debug)]
struct PinnedServer {
    expected: Option<String>,
    algorithms: WebPkiSupportedAlgorithms,
}

impl ServerCertVerifier for PinnedServer {
    fn verify_server_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        _intermediates: &[CertificateDer<'_>],
        _server_name: &ServerName<'_>,
        _ocsp_response: &[u8],
        _now: UnixTime,
    ) -> Result<ServerCertVerified, Error> {
        // Peers are addressed by IP and carry no matching name: the
        // fingerprint is what identifies them.
        if let Some(expected) = &self.expected {
            let actual = fingerprint_from_cert_der(end_entity.as_ref());
            if &actual != expected {
                return Err(Error::General(format!(
                    "server certificate fingerprint mismatch: expected {expected}, got {actual}"
                )));
            }
        }
        Ok(ServerCertVerified::assertion())
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, Error> {
        rustls::crypto::verify_tls12_signature(message, cert, dss, &self.algorithms)
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, Error> {
        rustls::crypto::verify_tls13_signature(message, cert, dss, &self.algorithms)
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.algorithms.supported_schemes()
    }
}
