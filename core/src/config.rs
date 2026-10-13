//! User settings (`settings.json`) and the default locations.

use anyhow::Context;
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// TCP port of the transfer service, and UDP port of the multicast discovery.
/// Deliberately not LocalSend's 53317, so the two apps don't collide and
/// LocalSend devices don't see Wormhole Send devices.
pub const DEFAULT_PORT: u16 = 53320;

/// UDP port of the multicast discovery. Fixed for every device, whatever
/// port its transfer service listens on.
pub const MULTICAST_PORT: u16 = 53320;

const SETTINGS_FILE: &str = "settings.json";

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    /// The name other devices show for this device.
    pub alias: String,

    /// Where received files are saved.
    pub save_dir: PathBuf,

    /// TCP port of the transfer service.
    #[serde(default = "default_port")]
    pub port: u16,
}

fn default_port() -> u16 {
    DEFAULT_PORT
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            alias: default_alias(),
            save_dir: default_save_dir(),
            port: DEFAULT_PORT,
        }
    }
}

impl Settings {
    /// Loads `settings.json` from `home`, or the defaults when there is none.
    pub fn load(home: &Path) -> anyhow::Result<Self> {
        let path = home.join(SETTINGS_FILE);
        match std::fs::read_to_string(&path) {
            Ok(text) => serde_json::from_str(&text)
                .with_context(|| format!("Invalid settings file: {}", path.display())),
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => Ok(Self::default()),
            Err(err) => Err(err).context(format!("Could not read {}", path.display())),
        }
    }

    pub fn save(&self, home: &Path) -> anyhow::Result<()> {
        std::fs::create_dir_all(home)?;
        let path = home.join(SETTINGS_FILE);
        write_atomic(&path, serde_json::to_string_pretty(self)?.as_bytes())
            .with_context(|| format!("Could not save {}", path.display()))
    }
}

/// The directory holding the identity, the settings and the trust store.
pub fn default_home() -> PathBuf {
    dirs::config_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("WormholeSend")
}

/// The computer's name, without the `.local` suffix macOS adds.
pub fn default_alias() -> String {
    let name = gethostname::gethostname().to_string_lossy().into_owned();
    let name = name.strip_suffix(".local").unwrap_or(&name).trim().to_string();
    match name.is_empty() {
        true => "Wormhole Send".to_string(),
        false => name,
    }
}

pub fn default_save_dir() -> PathBuf {
    dirs::download_dir()
        .or_else(dirs::home_dir)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Wormhole Send")
}

/// Writes `contents` to a temporary file next to `path`, then renames it over
/// `path`, so readers never see a half-written file.
pub(crate) fn write_atomic(path: &Path, contents: &[u8]) -> std::io::Result<()> {
    let tmp = path.with_extension("tmp");
    std::fs::write(&tmp, contents)?;
    std::fs::rename(&tmp, path)
}
