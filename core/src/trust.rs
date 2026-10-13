//! `trust.json`: the paired devices. A transfer request is accepted without
//! asking exactly when the sender's certificate fingerprint is in here.
//!
//! The file is the source of truth: every read loads it and every change is a
//! load-modify-save, so several processes sharing a home stay consistent.

use crate::config::write_atomic;
use anyhow::Context;
use localsend::model::discovery::DeviceType;
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

const TRUST_FILE: &str = "trust.json";

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TrustedDevice {
    /// SHA-256 fingerprint of the device certificate, uppercase hex.
    pub fingerprint: String,
    pub alias: String,
    #[serde(default)]
    pub device_type: Option<DeviceType>,
    /// Unix seconds.
    pub paired_at: u64,
    /// The address the device was last reached at.
    #[serde(default)]
    pub last_host: Option<String>,
    #[serde(default)]
    pub last_port: Option<u16>,
    /// Unix seconds of the last transfer sent to the device.
    #[serde(default)]
    pub last_sent_at: Option<u64>,
    #[serde(default)]
    pub is_default: bool,
    /// The device refused a transfer: it no longer trusts this one.
    #[serde(default)]
    pub needs_repair: bool,
}

impl TrustedDevice {
    pub fn new(fingerprint: &str, alias: String) -> Self {
        Self {
            fingerprint: normalize_fingerprint(fingerprint),
            alias,
            device_type: None,
            paired_at: unix_now(),
            last_host: None,
            last_port: None,
            last_sent_at: None,
            is_default: false,
            needs_repair: false,
        }
    }
}

#[derive(Debug, Default, Serialize, Deserialize)]
pub struct TrustStore {
    #[serde(skip)]
    path: PathBuf,
    devices: Vec<TrustedDevice>,
}

impl TrustStore {
    pub fn load(home: &Path) -> anyhow::Result<Self> {
        let path = home.join(TRUST_FILE);
        let mut store = match std::fs::read_to_string(&path) {
            Ok(text) => serde_json::from_str::<TrustStore>(&text)
                .with_context(|| format!("Invalid trust file: {}", path.display()))?,
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => TrustStore::default(),
            Err(err) => return Err(err).context(format!("Could not read {}", path.display())),
        };
        store.path = path;
        Ok(store)
    }

    pub fn save(&self) -> anyhow::Result<()> {
        if let Some(dir) = self.path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        write_atomic(&self.path, serde_json::to_string_pretty(self)?.as_bytes())
            .with_context(|| format!("Could not save {}", self.path.display()))
    }

    /// Loads the store, applies `change` and saves it when `change` reports a
    /// modification. Returns what `change` returned.
    pub fn update<T>(home: &Path, change: impl FnOnce(&mut TrustStore) -> (bool, T)) -> anyhow::Result<T> {
        let mut store = Self::load(home)?;
        let (changed, result) = change(&mut store);
        if changed {
            store.save()?;
        }
        Ok(result)
    }

    pub fn devices(&self) -> &[TrustedDevice] {
        &self.devices
    }

    pub fn get(&self, fingerprint: &str) -> Option<&TrustedDevice> {
        let fingerprint = normalize_fingerprint(fingerprint);
        self.devices.iter().find(|device| device.fingerprint == fingerprint)
    }

    pub fn contains(&self, fingerprint: &str) -> bool {
        self.get(fingerprint).is_some()
    }

    pub fn default_device(&self) -> Option<&TrustedDevice> {
        self.devices.iter().find(|device| device.is_default)
    }

    /// Adds or replaces a device. The first paired device becomes the default.
    pub fn add(&mut self, mut device: TrustedDevice) {
        device.fingerprint = normalize_fingerprint(&device.fingerprint);
        match self.devices.iter_mut().find(|d| d.fingerprint == device.fingerprint) {
            Some(existing) => {
                device.is_default = existing.is_default;
                *existing = device;
            }
            None => {
                device.is_default = self.devices.is_empty();
                self.devices.push(device);
            }
        }
    }

    /// Removes a device. When it was the default, the most recently used of
    /// the remaining devices becomes the default.
    pub fn remove(&mut self, fingerprint: &str) -> Option<TrustedDevice> {
        let fingerprint = normalize_fingerprint(fingerprint);
        let index = self.devices.iter().position(|d| d.fingerprint == fingerprint)?;
        let removed = self.devices.remove(index);
        if removed.is_default
            && let Some(next) = self
                .devices
                .iter_mut()
                .max_by_key(|d| (d.last_sent_at.unwrap_or(0), d.paired_at))
        {
            next.is_default = true;
        }
        Some(removed)
    }

    pub fn set_default(&mut self, fingerprint: &str) -> bool {
        let fingerprint = normalize_fingerprint(fingerprint);
        if !self.contains(&fingerprint) {
            return false;
        }
        for device in &mut self.devices {
            device.is_default = device.fingerprint == fingerprint;
        }
        true
    }

    /// Remembers where a paired device was reached and its current name.
    /// Returns whether anything changed.
    pub fn record_seen(&mut self, fingerprint: &str, alias: &str, host: &str, port: u16) -> bool {
        let fingerprint = normalize_fingerprint(fingerprint);
        let Some(device) = self.devices.iter_mut().find(|d| d.fingerprint == fingerprint) else {
            return false;
        };
        // Keep an IPv4 address over an IPv6 one: a link-local IPv6 address
        // carries an interface index that may differ after a reboot.
        let keeps_ipv4 = device
            .last_host
            .as_deref()
            .is_some_and(|stored| stored.parse::<std::net::Ipv4Addr>().is_ok())
            && host.parse::<std::net::Ipv4Addr>().is_err();
        if keeps_ipv4 {
            let changed = device.alias != alias;
            device.alias = alias.to_string();
            return changed;
        }
        let changed = device.alias != alias
            || device.last_host.as_deref() != Some(host)
            || device.last_port != Some(port);
        device.alias = alias.to_string();
        device.last_host = Some(host.to_string());
        device.last_port = Some(port);
        changed
    }

    pub fn mark_needs_repair(&mut self, fingerprint: &str) -> bool {
        let fingerprint = normalize_fingerprint(fingerprint);
        match self.devices.iter_mut().find(|d| d.fingerprint == fingerprint) {
            Some(device) if !device.needs_repair => {
                device.needs_repair = true;
                true
            }
            _ => false,
        }
    }

    pub fn record_sent(&mut self, fingerprint: &str) -> bool {
        let fingerprint = normalize_fingerprint(fingerprint);
        match self.devices.iter_mut().find(|d| d.fingerprint == fingerprint) {
            Some(device) => {
                device.last_sent_at = Some(unix_now());
                true
            }
            None => false,
        }
    }

    /// Finds a paired device by its exact name (case-insensitive) or a unique
    /// fingerprint prefix of at least 4 characters.
    pub fn resolve(&self, query: &str) -> Result<&TrustedDevice, String> {
        resolve_device(&self.devices, query, |d| (&d.alias, &d.fingerprint))
    }
}

/// Finds an item by its exact name (case-insensitive) or a unique fingerprint
/// prefix of at least 4 characters.
pub fn resolve_device<'a, T>(
    items: &'a [T],
    query: &str,
    key: impl Fn(&T) -> (&String, &String),
) -> Result<&'a T, String> {
    let query = query.trim();
    let by_name: Vec<&T> = items
        .iter()
        .filter(|item| key(item).0.eq_ignore_ascii_case(query))
        .collect();
    if by_name.len() == 1 {
        return Ok(by_name[0]);
    }
    if by_name.len() > 1 {
        return Err(format!("有多台设备叫“{query}”，请改用指纹前缀"));
    }
    let prefix = normalize_fingerprint(query);
    if prefix.len() < 4 || !prefix.chars().all(|c| c.is_ascii_hexdigit()) {
        return Err(format!("找不到设备“{query}”"));
    }
    let by_prefix: Vec<&T> = items
        .iter()
        .filter(|item| key(item).1.starts_with(&prefix))
        .collect();
    match by_prefix.len() {
        1 => Ok(by_prefix[0]),
        0 => Err(format!("找不到设备“{query}”")),
        _ => Err(format!("指纹前缀“{query}”对应多台设备，请写长一些")),
    }
}

pub fn normalize_fingerprint(fingerprint: &str) -> String {
    fingerprint.trim().replace(':', "").to_ascii_uppercase()
}

/// Whether `text` is a whole SHA-256 fingerprint (64 hex digits).
pub fn is_full_fingerprint(text: &str) -> bool {
    let text = normalize_fingerprint(text);
    text.len() == 64 && text.chars().all(|c| c.is_ascii_hexdigit())
}

pub fn short_fingerprint(fingerprint: &str) -> &str {
    &fingerprint[..fingerprint.len().min(8)]
}

fn unix_now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn device(fp: char, alias: &str) -> TrustedDevice {
        TrustedDevice::new(&fp.to_string().repeat(64), alias.to_string())
    }

    #[test]
    fn first_device_becomes_default_and_default_moves_on_remove() {
        let mut store = TrustStore::default();
        store.add(device('a', "Mac"));
        store.add(device('b', "PC"));
        assert_eq!(store.default_device().unwrap().alias, "Mac");

        store.remove(&"A".repeat(64));
        assert_eq!(store.default_device().unwrap().alias, "PC");
    }

    #[test]
    fn re_adding_keeps_the_default_flag() {
        let mut store = TrustStore::default();
        store.add(device('a', "Mac"));
        store.add(device('a', "Mac renamed"));
        assert_eq!(store.devices().len(), 1);
        assert!(store.devices()[0].is_default);
        assert_eq!(store.devices()[0].alias, "Mac renamed");
    }

    #[test]
    fn resolves_by_name_or_prefix() {
        let mut store = TrustStore::default();
        store.add(device('a', "Mac"));
        store.add(device('b', "PC"));
        assert_eq!(store.resolve("mac").unwrap().alias, "Mac");
        assert_eq!(store.resolve("bbbb").unwrap().alias, "PC");
        assert!(store.resolve("bb").is_err());
        assert!(store.resolve("nobody").is_err());
    }

    #[test]
    fn keeps_ipv4_address_over_ipv6() {
        let mut store = TrustStore::default();
        store.add(device('a', "Mac"));
        let fp = "A".repeat(64);
        assert!(store.record_seen(&fp, "Mac", "fe80::1%10", 53320));
        assert!(store.record_seen(&fp, "Mac", "192.168.1.5", 53320));
        assert!(!store.record_seen(&fp, "Mac", "fe80::1%10", 53320));
        assert_eq!(store.get(&fp).unwrap().last_host.as_deref(), Some("192.168.1.5"));
        assert!(store.record_seen(&fp, "Mac", "192.168.1.9", 53320));
    }

    #[test]
    fn fingerprints_are_normalized() {
        let mut store = TrustStore::default();
        store.add(device('a', "Mac"));
        assert!(store.contains(&"a".repeat(64)));
        assert!(is_full_fingerprint(&"aB".repeat(32)));
        assert!(!is_full_fingerprint("abc"));
    }
}
