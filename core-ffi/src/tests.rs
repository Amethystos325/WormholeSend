//! Two cores in one process, driven through the C functions only.

use super::*;
use std::ffi::CStr;
use std::path::Path;
use std::sync::mpsc::Receiver;
use std::time::{Duration, Instant};

/// Events as the app gets them: the thread they arrived on, and the JSON.
type Events = Receiver<(String, Value)>;

/// The language is the whole process's: tests that look at messages take
/// turns.
static MESSAGES: Mutex<()> = Mutex::new(());

unsafe extern "C" fn collect(ctx: *mut c_void, json: *const c_char) {
    let sender = unsafe { &*(ctx as *const mpsc::Sender<(String, Value)>) };
    let text = unsafe { CStr::from_ptr(json) }.to_str().unwrap();
    let thread = std::thread::current().name().unwrap_or("").to_string();
    let _ = sender.send((thread, serde_json::from_str(text).unwrap()));
}

struct Core {
    core: *mut WhCore,
    events: Events,
    _sender: Box<mpsc::Sender<(String, Value)>>,
}

impl Core {
    fn start(home: &Path, port: u16, alias: &str, save_dir: &Path) -> Core {
        let (sender, events) = mpsc::channel();
        let sender = Box::new(sender);
        let config = json!({ "home": home, "port": port, "portFallback": true, "alias": alias, "saveDir": save_dir });
        let config = CString::new(config.to_string()).unwrap();
        let mut core = std::ptr::null_mut();
        let ctx = &*sender as *const mpsc::Sender<(String, Value)> as *mut c_void;
        let response = take(unsafe { wh_start(config.as_ptr(), Some(collect), ctx, &mut core) });
        assert_eq!(response["ok"], true, "{response}");
        assert_eq!(response["result"]["alias"], alias);
        Core { core, events, _sender: sender }
    }

    fn run(&self, command: Value) -> Value {
        let text = CString::new(command.to_string()).unwrap();
        take(unsafe { wh_command(self.core, text.as_ptr()) })
    }

    fn ok(&self, command: Value) -> Value {
        let response = self.run(command.clone());
        assert_eq!(response["ok"], true, "{command} → {response}");
        response["result"].clone()
    }

    fn wait(&self, event: &str) -> Value {
        let deadline = Instant::now() + Duration::from_secs(20);
        loop {
            let left = deadline.saturating_duration_since(Instant::now());
            let (thread, value) = self.events.recv_timeout(left).unwrap_or_else(|_| panic!("no {event}"));
            assert_eq!(thread, "wormhole-events", "callbacks come from the dispatch thread");
            if value["event"] == event {
                return value;
            }
        }
    }

    fn stop(self) {
        unsafe { wh_stop(self.core) };
    }
}

fn take(pointer: *mut c_char) -> Value {
    assert!(!pointer.is_null());
    let value = serde_json::from_str(unsafe { CStr::from_ptr(pointer) }.to_str().unwrap()).unwrap();
    unsafe { wh_free_string(pointer) };
    value
}

#[test]
fn version_is_a_static_string() {
    let version = unsafe { CStr::from_ptr(wh_version()) }.to_str().unwrap();
    assert!(version.starts_with("wormhole-ffi "), "{version}");
}

#[test]
fn failures_come_back_as_json() {
    let _messages = MESSAGES.lock().unwrap_or_else(|e| e.into_inner());
    let response = take(unsafe { wh_command(std::ptr::null(), c"{}".as_ptr()) });
    assert_eq!(response, json!({ "ok": false, "error": "核心没有启动" }));

    // Not NULL, to see that a failed start clears it.
    let mut core = std::ptr::dangling_mut::<WhCore>();
    let response = take(unsafe { wh_start(c"{not json".as_ptr(), None, std::ptr::null_mut(), &mut core) });
    assert_eq!(response["ok"], false);
    assert!(response["error"].as_str().unwrap().starts_with("配置不是有效的 JSON"));
    assert!(core.is_null());
}

#[test]
fn pair_send_retry_rename_and_unpair() {
    let _messages = MESSAGES.lock().unwrap_or_else(|e| e.into_inner());
    let dir = tempfile::tempdir().unwrap();
    let path = |name: &str| dir.path().join(name);
    let a = Core::start(&path("a"), 53460, "FfiA", &path("a-recv"));
    let b = Core::start(&path("b"), 53470, "FfiB", &path("b-recv"));

    assert_eq!(a.run(json!({ "command": "nope" }))["error"], "未知命令：nope");
    assert_eq!(a.run(json!({ "command": "send", "deviceId": "x", "paths": [] }))["error"], "没有要发送的文件");
    assert_eq!(a.ok(json!({ "command": "devices.list" })), json!([]));

    // Discovery, then pairing; pair.start returns before any network work.
    let deadline = Instant::now() + Duration::from_secs(15);
    let peer = loop {
        let nearby = a.ok(json!({ "command": "discover.list" }));
        if let Some(device) = nearby.as_array().unwrap().iter().find(|d| d["alias"] == "FfiB") {
            assert_eq!(device["kind"], "wormhole");
            break device["fingerprint"].as_str().unwrap().to_string();
        }
        assert!(Instant::now() < deadline, "FfiB not discovered");
        std::thread::sleep(Duration::from_millis(300));
    };
    // A LocalSend app may be on the network too; a Wormhole Send device is
    // never listed with them.
    let localsend = a.ok(json!({ "command": "localsend.list" }));
    assert!(localsend.as_array().unwrap().iter().all(|d| d["kind"] == "localsend" && d["alias"] != "FfiB"));
    assert_eq!(a.ok(json!({ "command": "receive.respond", "requestId": "none", "accept": true })), false);

    let started = Instant::now();
    let pair_id = a.ok(json!({ "command": "pair.start", "deviceId": peer }));
    assert!(started.elapsed() < Duration::from_millis(100), "pair.start took {:?}", started.elapsed());
    let code_a = a.wait("pair.code");
    let code_b = b.wait("pair.code");
    assert_eq!(code_a["pairId"], pair_id);
    assert_eq!(code_a["code"], code_b["code"]);
    a.ok(json!({ "command": "pair.confirm", "pairId": pair_id, "accept": true }));
    b.ok(json!({ "command": "pair.confirm", "pairId": code_b["pairId"], "accept": true }));
    assert_eq!(a.wait("pair.result")["ok"], true);
    assert_eq!(b.wait("pair.result")["ok"], true);
    let paired = a.ok(json!({ "command": "devices.list" }));
    assert_eq!(paired[0]["alias"], "FfiB");
    assert_eq!(paired[0]["isDefault"], true);
    assert_eq!(paired[0]["needsRepair"], false);

    // Send, then send the same again with retry.
    std::fs::write(path("报告.txt"), "Wormhole").unwrap();
    let id = a.ok(json!({ "command": "send", "deviceId": peer, "paths": [path("报告.txt")] }));
    assert_eq!(a.wait("transfer.finished")["status"], "done");
    let received = b.wait("transfer.finished");
    assert_eq!(received["status"], "done");
    assert_eq!(received["direction"], "receive");
    assert_eq!(std::fs::read_to_string(path("b-recv").join("报告.txt")).unwrap(), "Wormhole");
    let again = a.ok(json!({ "command": "transfer.retry", "transferId": id }));
    assert_ne!(again, id);
    assert_eq!(a.wait("transfer.finished")["status"], "done");
    assert!(path("b-recv").join("报告 (1).txt").exists());

    // Renaming restarts the services in the background.
    let renamed = a.ok(json!({ "command": "settings.set", "alias": "FfiA2" }));
    assert_eq!(renamed["alias"], "FfiA2");
    assert_eq!(renamed["restarting"], true);
    let deadline = Instant::now() + Duration::from_secs(10);
    while a.run(json!({ "command": "devices.list" }))["ok"] != true {
        assert!(Instant::now() < deadline, "no restart");
        std::thread::sleep(Duration::from_millis(100));
    }
    assert_eq!(a.ok(json!({ "command": "settings.get" }))["alias"], "FfiA2");

    // Unpairing returns at once; the other side hears about it.
    let started = Instant::now();
    assert_eq!(a.ok(json!({ "command": "device.unpair", "deviceId": peer })), json!({ "wasPaired": true }));
    assert!(started.elapsed() < Duration::from_millis(100));
    assert_eq!(b.wait("device.unpaired")["alias"], "FfiA2");
    assert_eq!(a.ok(json!({ "command": "devices.list" })), json!([]));

    a.stop();
    b.stop();
}

#[test]
fn messages_follow_the_language() {
    let _messages = MESSAGES.lock().unwrap_or_else(|e| e.into_inner());
    let dir = tempfile::tempdir().unwrap();
    let (sender, _events) = mpsc::channel::<(String, Value)>();
    let sender = Box::new(sender);
    let config = json!({ "home": dir.path().join("home"), "port": 53480, "portFallback": true, "language": "en-US" });
    let config = CString::new(config.to_string()).unwrap();
    let mut pointer = std::ptr::null_mut();
    let ctx = &*sender as *const mpsc::Sender<(String, Value)> as *mut c_void;
    assert_eq!(take(unsafe { wh_start(config.as_ptr(), Some(collect), ctx, &mut pointer) })["ok"], true);
    let core = Core { core: pointer, events: _events, _sender: sender };

    assert_eq!(core.run(json!({ "command": "nope" }))["error"], "Unknown command: nope");
    core.ok(json!({ "command": "language.set", "language": "ja" }));
    assert_eq!(core.run(json!({ "command": "nope" }))["error"], "不明なコマンド：nope");
    core.ok(json!({ "command": "language.set", "language": "zh-Hans" }));
    assert_eq!(core.run(json!({ "command": "nope" }))["error"], "未知命令：nope");
    core.stop();
}
