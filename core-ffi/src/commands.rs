//! The commands of `wh_command`, documented in core-ffi/README.md.

use crate::Shared;
use anyhow::{Context, anyhow, bail};
use serde_json::{Value, json};
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::Ordering;
use wormhole_core::i18n::{self, Lang};
use wormhole_core::{Kind, Node, tr};
use wormhole_core::config::Settings;
use wormhole_core::identity::Identity;

pub(crate) fn run(shared: &Arc<Shared>, command: &Value) -> anyhow::Result<Value> {
    let name = command.get("command").and_then(Value::as_str).with_context(|| tr!("命令缺少 command 字段", "The command has no command field", "コマンドに command フィールドがありません"))?;
    match name {
        "settings.get" => return settings(shared),
        "settings.set" => return set_settings(shared, command),
        // Messages made from now on use it; nothing restarts.
        "language.set" => {
            i18n::set(Lang::parse(string(command, "language")?));
            return Ok(Value::Null);
        }
        _ => {}
    }
    let restarting = || anyhow!(tr!("正在重新启动服务，请稍后再试", "The services are restarting; try again in a moment", "サービスを再起動中です。しばらくしてからやり直してください"));
    if shared.restarting.load(Ordering::SeqCst) {
        return Err(restarting());
    }
    let slot = shared.node.try_read().map_err(|_| restarting())?;
    let node = slot.as_ref().with_context(|| tr!("服务没有在运行", "The services aren't running", "サービスが動いていません"))?;
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
            // Only devices heard from within the online window: discovery
            // keeps every device seen since the start, gone or not. LocalSend
            // devices can't pair.
            Ok(serde_json::to_value(
                node.devices()
                    .into_iter()
                    .filter(|d| !d.paired && d.online && d.kind == Kind::Wormhole)
                    .collect::<Vec<_>>(),
            )?)
        }
        "localsend.list" => {
            node.refresh_in_background();
            Ok(serde_json::to_value(
                node.devices()
                    .into_iter()
                    .filter(|d| d.kind == Kind::LocalSend && d.online)
                    .collect::<Vec<_>>(),
            )?)
        }
        "send" => {
            let device = string(command, "deviceId")?;
            let paths: Vec<PathBuf> = command
                .get("paths")
                .and_then(Value::as_array)
                .with_context(|| tr!("缺少 paths", "paths is missing", "paths がありません"))?
                .iter()
                .map(|p| p.as_str().map(PathBuf::from).with_context(|| tr!("paths 只能包含字符串", "paths can only hold strings", "paths には文字列しか入れられません")))
                .collect::<anyhow::Result<_>>()?;
            Ok(json!(send(shared, node, device, paths)?))
        }
        "transfer.cancel" => Ok(json!(node.cancel(string(command, "transferId")?))),
        "transfer.retry" => {
            let id = string(command, "transferId")?;
            let (device, paths) = shared.sends.lock().unwrap().get(id).cloned().with_context(|| tr!("找不到这次发送", "No such send", "この送信が見つかりません"))?;
            Ok(json!(send(shared, node, &device, paths)?))
        }
        "receive.respond" => {
            let id = string(command, "requestId")?;
            let accept = command.get("accept").and_then(Value::as_bool).with_context(|| tr!("缺少 accept", "accept is missing", "accept がありません"))?;
            shared.active.lock().unwrap().remove(id);
            Ok(json!(node.respond_receive(id, accept)))
        }
        "pair.start" => Ok(json!(node.pair(string(command, "deviceId")?)?)),
        "pair.confirm" => {
            let accept = command.get("accept").and_then(Value::as_bool).with_context(|| tr!("缺少 accept", "accept is missing", "accept がありません"))?;
            node.pair_confirm(string(command, "pairId")?, accept)?;
            Ok(Value::Null)
        }
        "device.unpair" => Ok(json!({ "wasPaired": node.unpair_in_background(string(command, "deviceId")?)? })),
        "device.setDefault" => {
            if !node.set_default(string(command, "deviceId")?)? {
                bail!(tr!("这台设备没有配对", "This device isn't paired", "このデバイスはペアリングされていません"));
            }
            Ok(Value::Null)
        }
        _ => bail!(tr!("未知命令：{name}", "Unknown command: {name}", "不明なコマンド：{name}")),
    }
}

fn send(shared: &Shared, node: &Node, device: &str, paths: Vec<PathBuf>) -> anyhow::Result<String> {
    if paths.is_empty() {
        bail!(tr!("没有要发送的文件", "Nothing to send", "送信するファイルがありません"));
    }
    if let Some(missing) = paths.iter().find(|p| !p.exists()) {
        bail!(tr!("找不到 {}", "Can't find {}", "{} が見つかりません", missing.display()));
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
    if !shared.restarting.load(Ordering::SeqCst)
        && let Ok(slot) = shared.node.try_read()
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
        bail!(tr!("设备名称不能为空，最多 64 个字", "The device name can't be empty, and has 64 characters at most", "デバイス名は空にできません。64 文字までです"));
    }
    if let Some(dir) = &save_dir {
        if !dir.is_absolute() {
            bail!(tr!("保存位置要写完整路径", "The save location needs a full path", "保存先はフルパスで指定してください"));
        }
        std::fs::create_dir_all(dir).with_context(|| tr!("无法使用这个文件夹：{}", "Can't use this folder: {}", "このフォルダは使用できません：{}", dir.display()))?;
    }
    if !shared.active.lock().unwrap().is_empty() {
        bail!(tr!("正在传输文件，结束后再修改设置", "Files are being transferred; change the settings when they're done", "ファイルを転送中です。終わってから設定を変更してください"));
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
    command.get(field).and_then(Value::as_str).with_context(|| tr!("缺少 {field}", "{field} is missing", "{field} がありません"))
}
