//! The commands of `wh_command` (design doc, section 4.2).

use crate::Shared;
use anyhow::{Context, anyhow, bail};
use serde_json::{Value, json};
use std::path::PathBuf;
use std::sync::Arc;
use wormhole_core::Node;
use wormhole_core::config::Settings;
use wormhole_core::identity::Identity;

pub(crate) fn run(shared: &Arc<Shared>, command: &Value) -> anyhow::Result<Value> {
    let name = command.get("command").and_then(Value::as_str).context("命令缺少 command 字段")?;
    match name {
        "settings.get" => return settings(shared),
        "settings.set" => return set_settings(shared, command),
        _ => {}
    }
    let slot = shared.node.try_read().map_err(|_| anyhow!("正在重新启动服务，请稍后再试"))?;
    let node = slot.as_ref().context("服务没有在运行")?;
    // Commands that spawn work need the runtime.
    let _runtime = shared.handle.enter();
    match name {
        "devices.list" => Ok(serde_json::to_value(
            node.devices().into_iter().filter(|d| d.paired).collect::<Vec<_>>(),
        )?),
        "devices.refresh" => {
            node.refresh_in_background();
            Ok(Value::Null)
        }
        "discover.list" => {
            node.refresh_in_background();
            Ok(serde_json::to_value(
                node.devices().into_iter().filter(|d| !d.paired).collect::<Vec<_>>(),
            )?)
        }
        "send" => {
            let device = string(command, "deviceId")?;
            let paths: Vec<PathBuf> = command
                .get("paths")
                .and_then(Value::as_array)
                .context("缺少 paths")?
                .iter()
                .map(|p| p.as_str().map(PathBuf::from).context("paths 只能包含字符串"))
                .collect::<anyhow::Result<_>>()?;
            Ok(json!(send(shared, node, device, paths)?))
        }
        "transfer.cancel" => Ok(json!(node.cancel(string(command, "transferId")?))),
        "transfer.retry" => {
            let id = string(command, "transferId")?;
            let (device, paths) = shared.sends.lock().unwrap().get(id).cloned().context("找不到这次发送")?;
            Ok(json!(send(shared, node, &device, paths)?))
        }
        "pair.start" => Ok(json!(node.pair(string(command, "deviceId")?)?)),
        "pair.confirm" => {
            let accept = command.get("accept").and_then(Value::as_bool).context("缺少 accept")?;
            node.pair_confirm(string(command, "pairId")?, accept)?;
            Ok(Value::Null)
        }
        "device.unpair" => Ok(json!({ "wasPaired": node.unpair_in_background(string(command, "deviceId")?)? })),
        "device.setDefault" => {
            if !node.set_default(string(command, "deviceId")?)? {
                bail!("这台设备没有配对");
            }
            Ok(Value::Null)
        }
        _ => bail!("未知命令：{name}"),
    }
}

fn send(shared: &Shared, node: &Node, device: &str, paths: Vec<PathBuf>) -> anyhow::Result<String> {
    if paths.is_empty() {
        bail!("没有要发送的文件");
    }
    if let Some(missing) = paths.iter().find(|p| !p.exists()) {
        bail!("找不到 {}", missing.display());
    }
    let id = node.send(device, paths.clone());
    shared.active.lock().unwrap().insert(id.clone());
    shared.sends.lock().unwrap().insert(id.clone(), (device.to_string(), paths));
    Ok(id)
}

/// `{"alias","saveDir","port","fingerprint","home"}`: what the running
/// services use, or the saved settings while they restart.
pub(crate) fn settings(shared: &Shared) -> anyhow::Result<Value> {
    let home = shared.start.lock().unwrap().home();
    if let Ok(slot) = shared.node.try_read()
        && let Some(node) = slot.as_ref()
    {
        let settings = node.settings();
        let identity = node.identity();
        return Ok(json!({
            "alias": settings.alias,
            "saveDir": settings.save_dir,
            "port": identity.port,
            "fingerprint": identity.fingerprint,
            "home": home,
        }));
    }
    let settings = Settings::load(&home)?;
    Ok(json!({
        "alias": settings.alias,
        "saveDir": settings.save_dir,
        "port": settings.port,
        "fingerprint": Identity::read_fingerprint(&home)?,
        "home": home,
    }))
}

/// Saves the settings and restarts the services in the background (the
/// name is in every announcement). The result is the new settings, with
/// `"restarting": true`.
fn set_settings(shared: &Arc<Shared>, command: &Value) -> anyhow::Result<Value> {
    let alias = command.get("alias").and_then(Value::as_str).map(str::trim);
    let save_dir = command.get("saveDir").and_then(Value::as_str).map(PathBuf::from);
    if let Some(alias) = alias
        && (alias.is_empty() || alias.chars().count() > 64)
    {
        bail!("设备名称不能为空，最多 64 个字");
    }
    if let Some(dir) = &save_dir {
        if !dir.is_absolute() {
            bail!("保存位置要写完整路径");
        }
        std::fs::create_dir_all(dir).with_context(|| format!("无法使用这个文件夹：{}", dir.display()))?;
    }
    if !shared.active.lock().unwrap().is_empty() {
        bail!("正在传输文件，结束后再修改设置");
    }

    let home = {
        let mut start = shared.start.lock().unwrap();
        // The saved settings apply from now on, not the ones given at start.
        start.alias = None;
        start.save_dir = None;
        start.home()
    };
    let mut settings = Settings::load(&home)?;
    if let Some(alias) = alias {
        settings.alias = alias.to_string();
    }
    if let Some(dir) = save_dir {
        settings.save_dir = dir;
    }
    settings.save(&home)?;
    shared.restart();
    Ok(json!({
        "alias": settings.alias,
        "saveDir": settings.save_dir,
        "port": settings.port,
        "fingerprint": Identity::read_fingerprint(&home)?,
        "home": home,
        "restarting": true,
    }))
}

fn string<'a>(command: &'a Value, field: &str) -> anyhow::Result<&'a str> {
    command.get(field).and_then(Value::as_str).with_context(|| format!("缺少 {field}"))
}
