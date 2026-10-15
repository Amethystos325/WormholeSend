//! The C interface of the Wormhole Send core, shared by the macOS and
//! Windows apps: `wh_start`, `wh_command`,
//! `wh_free_string`, `wh_stop`, plus `wh_version`. Commands and results are
//! JSON; events reach the app through one callback, always on the same
//! thread, one at a time. `include/wormhole.h` is generated from this file.

mod commands;

use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};
use std::ffi::{CStr, CString, c_char, c_void};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, RwLock, mpsc};
use std::thread::JoinHandle;
use wormhole_core::config::{self, Settings};
use wormhole_core::event::CoreEvent;
use wormhole_core::i18n::{self, Lang};
use wormhole_core::tr;
use wormhole_core::{Node, NodeOptions};

/// Receives each event as JSON, e.g.
/// `{"event":"transfer.progress","id":"…","direction":"send","deviceId":"…","bytes":1024,"total":4096}`.
/// Called on one core thread, in order, never twice at once. `event_json` is
/// valid only during the call and owned by the core. Switch to the UI thread
/// asynchronously (DispatchQueue.main.async, Dispatcher.BeginInvoke):
/// `wh_stop` waits for the callback to return. Do not call `wh_*` functions
/// from it.
pub type WhEventCallback = Option<unsafe extern "C" fn(ctx: *mut c_void, event_json: *const c_char)>;

/// A running core. Opaque to the app.
pub struct WhCore {
    runtime: tokio::runtime::Runtime,
    shared: Arc<Shared>,
    dispatcher: Option<JoinHandle<()>>,
}

/// What commands and background restarts share.
pub(crate) struct Shared {
    handle: tokio::runtime::Handle,
    start: Mutex<StartConfig>,
    /// `None` while the services restart after a settings change.
    node: RwLock<Option<Node>>,
    /// Set by `wh_stop`: no restart may bring the node back.
    stopped: AtomicBool,
    /// From `settings.set` until the services run again. Set before
    /// `settings.set` returns, so no command reaches the old services after
    /// it (the restart thread may take the node a moment later).
    restarting: AtomicBool,
    events: Mutex<Option<mpsc::Sender<String>>>,
    /// Transfers in progress (sending, queued, waiting for a LocalSend user,
    /// receiving) and receive requests not answered yet, from the events.
    active: Mutex<HashSet<String>>,
    /// What each send was given, for `transfer.retry`.
    sends: Mutex<HashMap<String, (String, Vec<PathBuf>)>>,
}

/// `config_json` of `wh_start`.
#[derive(Clone, Default, serde::Deserialize)]
#[serde(rename_all = "camelCase", default)]
struct StartConfig {
    home: Option<PathBuf>,
    port: Option<u16>,
    port_fallback: bool,
    /// For this run instead of the saved settings (not saved).
    alias: Option<String>,
    /// Of the messages: "en", "zh", "ja" or a language tag; English if
    /// not given.
    language: Option<String>,
    save_dir: Option<PathBuf>,
}

impl StartConfig {
    fn home(&self) -> PathBuf {
        self.home.clone().unwrap_or_else(config::default_home)
    }

    fn options(&self) -> anyhow::Result<NodeOptions> {
        let home = self.home();
        let mut settings = Settings::load(&home)?;
        if let Some(port) = self.port {
            settings.port = port;
        }
        if let Some(alias) = &self.alias {
            settings.alias = alias.clone();
        }
        if let Some(dir) = &self.save_dir {
            settings.save_dir = dir.clone();
        }
        Ok(NodeOptions {
            home,
            settings,
            port_fallback: self.port_fallback,
        })
    }
}

/// The app's context pointer; the app promises it may be used from the
/// dispatch thread.
struct Context(*mut c_void);
unsafe impl Send for Context {}

/// The library and ABI version, e.g. "wormhole-ffi 0.1.0 (abi 1)". Static;
/// do not free.
#[unsafe(no_mangle)]
pub extern "C" fn wh_version() -> *const c_char {
    concat!("wormhole-ffi ", env!("CARGO_PKG_VERSION"), " (abi 1)\0").as_ptr().cast()
}

/// Starts the core: the transfer and control services, discovery, and
/// receiving from paired devices. Blocks while the ports are bound and, on
/// the first run, while the certificate is generated (a few seconds); call
/// it off the UI thread.
///
/// `config_json` may be NULL or `{}`. Optional fields: `home` (the data
/// directory; by default %APPDATA%\WormholeSend or ~/Library/Application
/// Support/WormholeSend), `port` (default from settings.json, else 53320),
/// `portFallback` (move to the next free port pair when taken; false for the
/// apps), `alias` and `saveDir` (for this run instead of the saved settings),
/// `language` (of the messages: "en", "zh" or "ja"; English if not given).
///
/// Returns `{"ok":true,"result":<settings.get>}` and sets `*core_out`, or
/// `{"ok":false,"error":"…"}` and sets `*core_out` to NULL. Free the string
/// with `wh_free_string`.
///
/// # Safety
///
/// `config_json` is NULL or a NUL-terminated string. `core_out` is NULL or
/// points to writable storage for a pointer. `on_event` and `ctx` stay
/// valid until `wh_stop` returns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wh_start(
    config_json: *const c_char,
    on_event: WhEventCallback,
    ctx: *mut c_void,
    core_out: *mut *mut WhCore,
) -> *mut c_char {
    let context = Context(ctx);
    let result = guard(|| {
        let config: StartConfig = match unsafe { text(config_json) } {
            Some(text) if !text.trim().is_empty() => {
                serde_json::from_str(text).map_err(|err| anyhow::anyhow!(tr!("配置不是有效的 JSON：{err}", "The configuration isn't valid JSON: {err}", "設定が有効な JSON ではありません：{err}")))?
            }
            _ => StartConfig::default(),
        };
        let core = start(config, on_event, context)?;
        let settings = commands::settings(&core.shared)?;
        Ok((core, settings))
    });
    let (core, response) = match result {
        Ok((core, settings)) => (Box::into_raw(core), ok(settings)),
        Err(err) => (std::ptr::null_mut(), failure(&err)),
    };
    if !core_out.is_null() {
        unsafe { *core_out = core };
    }
    into_c(response)
}

fn start(config: StartConfig, on_event: WhEventCallback, context: Context) -> anyhow::Result<Box<WhCore>> {
    if let Some(language) = &config.language {
        i18n::set(Lang::parse(language));
    }
    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .thread_name("wormhole-core")
        .build()?;
    let (node, events) = runtime.block_on(Node::start(config.options()?))?;

    // The app sees every event on this one thread, in order.
    let (sender, receiver) = mpsc::channel::<String>();
    let dispatcher = std::thread::Builder::new()
        .name("wormhole-events".to_string())
        .spawn(move || {
            let context = context;
            for json in receiver {
                let (Some(callback), Ok(text)) = (on_event, CString::new(json)) else { continue };
                let _ = catch_unwind(AssertUnwindSafe(|| unsafe { callback(context.0, text.as_ptr()) }));
            }
        })?;

    let shared = Arc::new(Shared {
        handle: runtime.handle().clone(),
        start: Mutex::new(config),
        node: RwLock::new(Some(node)),
        stopped: AtomicBool::new(false),
        restarting: AtomicBool::new(false),
        events: Mutex::new(Some(sender)),
        active: Mutex::default(),
        sends: Mutex::default(),
    });
    shared.forward(events);
    Ok(Box::new(WhCore {
        runtime,
        shared,
        dispatcher: Some(dispatcher),
    }))
}

impl Shared {
    /// Passes a node's events on to the dispatch thread.
    fn forward(self: &Arc<Self>, mut events: tokio::sync::mpsc::UnboundedReceiver<CoreEvent>) {
        let shared = self.clone();
        self.handle.spawn(async move {
            while let Some(event) = events.recv().await {
                shared.observe(&event);
                shared.emit(&event);
            }
        });
    }

    fn emit(&self, event: &CoreEvent) {
        if let (Some(sender), Ok(json)) = (self.events.lock().unwrap().as_ref(), serde_json::to_string(event)) {
            let _ = sender.send(json);
        }
    }

    fn observe(&self, event: &CoreEvent) {
        match event {
            CoreEvent::TransferStarted { id, .. }
            | CoreEvent::TransferQueued { id, .. }
            | CoreEvent::TransferWaiting { id, .. }
            | CoreEvent::ReceiveRequest { request_id: id, .. } => {
                self.active.lock().unwrap().insert(id.clone());
            }
            // An answered request is removed by `receive.respond`.
            CoreEvent::TransferFinished { id, .. } | CoreEvent::ReceiveClosed { request_id: id, .. } => {
                self.active.lock().unwrap().remove(id);
            }
            _ => {}
        }
    }

    /// Stops the services and starts them again with the saved settings, on
    /// a thread of its own; commands fail with "restarting" meanwhile.
    fn restart(self: &Arc<Self>) {
        self.restarting.store(true, Ordering::SeqCst);
        let shared = self.clone();
        std::thread::spawn(move || {
            let mut slot = shared.node.write().unwrap_or_else(|e| e.into_inner());
            let _done = Cleared(&shared.restarting);
            if shared.stopped.load(Ordering::SeqCst) {
                return;
            }
            if let Some(node) = slot.take() {
                shared.handle.block_on(node.stop());
            }
            let options = shared.start.lock().unwrap().options();
            match options.and_then(|options| shared.handle.block_on(Node::start(options))) {
                Ok((node, events)) => {
                    *slot = Some(node);
                    shared.forward(events);
                }
                Err(err) => shared.emit(&CoreEvent::Error {
                    message: tr!("无法重新启动服务：{err:#}", "Can't restart the services: {err:#}", "サービスを再起動できません：{err:#}"),
                }),
            }
        });
    }
}

/// Clears a flag when dropped, however the restart ends.
struct Cleared<'a>(&'a AtomicBool);

impl Drop for Cleared<'_> {
    fn drop(&mut self) {
        self.0.store(false, Ordering::SeqCst);
    }
}

/// Runs one command, `{"command":"…", …arguments}`, and returns
/// `{"ok":true,"result":…}` or `{"ok":false,"error":"…"}`; errors are
/// messages for the user. Returns within milliseconds, from any thread:
/// network work goes on in the background and reports through events.
/// Free the string with `wh_free_string`.
///
/// Commands: `devices.list`, `devices.refresh`, `discover.list`,
/// `localsend.list`, `send` (deviceId, paths), `transfer.cancel`
/// (transferId), `transfer.retry` (transferId), `pair.start` (deviceId),
/// `pair.confirm` (pairId, accept), `receive.respond` (requestId, accept),
/// `language.set` (language),
/// `device.unpair` (deviceId), `device.setDefault` (deviceId),
/// `settings.get`, `settings.set` (alias?, saveDir?). core-ffi/README.md
/// describes their results.
///
/// # Safety
///
/// `core` is NULL or a core from `wh_start` that `wh_stop` has not freed.
/// `command_json` is NULL or a NUL-terminated string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wh_command(core: *const WhCore, command_json: *const c_char) -> *mut c_char {
    let response = guard(|| {
        let core = unsafe { core.as_ref() }.ok_or_else(|| anyhow::anyhow!(tr!("核心没有启动", "The core isn't running", "コアが起動していません")))?;
        let text = unsafe { text(command_json) }.ok_or_else(|| anyhow::anyhow!(tr!("命令为空", "The command is empty", "コマンドが空です")))?;
        let command: Value =
            serde_json::from_str(text).map_err(|err| anyhow::anyhow!(tr!("命令不是有效的 JSON：{err}", "The command isn't valid JSON: {err}", "コマンドが有効な JSON ではありません：{err}")))?;
        commands::run(&core.shared, &command)
    });
    into_c(match response {
        Ok(result) => ok(result),
        Err(err) => failure(&err),
    })
}

/// Frees a string returned by `wh_start` or `wh_command`. NULL is ignored.
///
/// # Safety
///
/// `string` is NULL or a string from `wh_start` or `wh_command`, freed only
/// once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wh_free_string(string: *mut c_char) {
    if !string.is_null() {
        drop(unsafe { CString::from_raw(string) });
    }
}

/// Stops the core and frees it. Waits for an event callback in progress;
/// no events arrive after it returns. NULL is ignored.
///
/// # Safety
///
/// `core` is NULL or a core from `wh_start`, stopped only once and not used
/// afterwards. Not to be called from the event callback.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wh_stop(core: *mut WhCore) {
    if core.is_null() {
        return;
    }
    let WhCore { runtime, shared, dispatcher } = *unsafe { Box::from_raw(core) };
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let node = {
            let mut slot = shared.node.write().unwrap_or_else(|e| e.into_inner());
            shared.stopped.store(true, Ordering::SeqCst);
            slot.take()
        };
        if let Some(node) = node {
            runtime.block_on(node.stop());
        }
        runtime.shutdown_timeout(std::time::Duration::from_secs(1));
        // Closing the channel ends the dispatch thread once it has passed on
        // what is queued.
        shared.events.lock().unwrap_or_else(|e| e.into_inner()).take();
        if let Some(thread) = dispatcher {
            let _ = thread.join();
        }
    }));
}

/// Runs `body`, turning a panic into an error so it never crosses into C.
fn guard<T>(body: impl FnOnce() -> anyhow::Result<T>) -> anyhow::Result<T> {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(result) => result,
        Err(panic) => {
            let message = panic
                .downcast_ref::<&str>()
                .map(|s| s.to_string())
                .or_else(|| panic.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "unknown".to_string());
            Err(anyhow::anyhow!(tr!("核心内部错误：{message}", "Internal error in the core: {message}", "コアの内部エラー：{message}")))
        }
    }
}

unsafe fn text<'a>(pointer: *const c_char) -> Option<&'a str> {
    if pointer.is_null() {
        return None;
    }
    unsafe { CStr::from_ptr(pointer) }.to_str().ok()
}

fn ok(result: Value) -> Value {
    json!({ "ok": true, "result": result })
}

fn failure(err: &anyhow::Error) -> Value {
    json!({ "ok": false, "error": format!("{err:#}") })
}

fn into_c(value: Value) -> *mut c_char {
    // serde_json escapes control characters, so there is no NUL inside.
    CString::new(value.to_string()).map(CString::into_raw).unwrap_or(std::ptr::null_mut())
}

#[cfg(test)]
mod tests;
