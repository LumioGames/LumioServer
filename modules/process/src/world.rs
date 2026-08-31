//! World loop: the single authoritative orchestrator.
//!
//! Owns the runtime bridge, the session table, tick scheduling, delta routing
//! and the graceful shutdown sequence. Network tasks only feed bounded queues;
//! every Delta sent to a client comes from a bridge `tick`/`snapshot` response.

use tokio::sync::mpsc;
use tokio::sync::watch;

use crate::audit::SharedAudit;
use crate::runtime_bridge::RuntimeBridge;
use crate::server::WorldEvent;
use crate::wire::WireContract;

/// What the world loop reports after graceful shutdown.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorldOutcome {
    /// Why shutdown started (`stdin shutdown`, `ctrl-c`, ...).
    pub reason: String,
    /// Sessions that were open when shutdown began.
    pub sessions: usize,
}

/// Wiring handed to the world loop by [`crate::server::start`].
#[allow(dead_code)] // Red-phase stub: fields are consumed by the world implementation.
pub(crate) struct WorldArgs {
    /// Authoritative runtime bridge.
    pub(crate) bridge: Box<dyn RuntimeBridge>,
    /// Shared audit sink.
    pub(crate) audit: SharedAudit,
    /// Parsed wire contract.
    pub(crate) contract: WireContract,
    /// Central world inbox (opened sessions + validated ingress).
    pub(crate) world_rx: mpsc::Receiver<WorldEvent>,
    /// Connection teardown notifications.
    pub(crate) disconnect_rx: mpsc::Receiver<(String, &'static str)>,
    /// Shutdown signal (true = stop serving and quiesce).
    pub(crate) shutdown_rx: watch::Receiver<bool>,
}

/// Run the world loop until shutdown; returns the shutdown outcome after the
/// graceful sequence (sessions closed, bridge shutdown, audit flushed).
///
/// Red-phase stub: drains the wiring without serving any protocol, so the
/// e2e suite fails before the authoritative implementation lands.
pub(crate) async fn run_world(args: WorldArgs) -> WorldOutcome {
    let WorldArgs {
        bridge: _,
        audit: _,
        contract: _,
        mut world_rx,
        mut disconnect_rx,
        mut shutdown_rx,
    } = args;
    loop {
        tokio::select! {
            event = world_rx.recv() => {
                if event.is_none() {
                    break;
                }
            }
            _ = disconnect_rx.recv() => {}
            _ = shutdown_rx.changed() => break,
        }
    }
    WorldOutcome {
        reason: "stub".to_owned(),
        sessions: 0,
    }
}

#[cfg(test)]
mod e2e {
    use std::path::PathBuf;
    use std::sync::Arc;
    use std::sync::Mutex;
    use std::time::Duration;

    use futures_util::{SinkExt, StreamExt};
    use serde_json::{Value, json};
    use tokio::net::TcpStream;
    use tokio_tungstenite::MaybeTlsStream;
    use tokio_tungstenite::WebSocketStream;
    use tokio_tungstenite::connect_async;
    use tokio_tungstenite::tungstenite::Message;
    use tokio_tungstenite::tungstenite::client::IntoClientRequest;
    use tokio_tungstenite::tungstenite::http::HeaderValue;

    use crate::audit::AuditLog;
    use crate::runtime_bridge::tests::TestBridge;
    use crate::runtime_bridge::tests::StallGate;
    use crate::server::ServerConfig;
    use crate::server::ServerInstance;
    use crate::server;

    type Ws = WebSocketStream<MaybeTlsStream<TcpStream>>;

    const HELLO_SHA: &str = "a591a6d40bf420404a011733cfb7b190d62c65bf0bcda32b57b277d9ad9f146e";

    fn contract_file(dir: &std::path::Path, handshake_ms: u64, baseline_ms: u64) -> PathBuf {
        let body = json!({
            "contractId": "lumio.hello-wire.v1",
            "transport": { "kind": "websocket", "subprotocol": "lumio-hello-v1", "maxFrameBytes": 65536 },
            "roles": ["browser", "bot"],
            "limits": {
                "maxPayloadBytes": 4096,
                "maxSessions": 2,
                "ingressQueuePerSession": 64,
                "helloLogCapacity": 32,
                "handshakeTimeoutMs": handshake_ms,
                "baselineTimeoutMs": baseline_ms,
                "scenarioTimeoutMs": 30000
            }
        });
        let path = dir.join("contract.json");
        std::fs::write(&path, body.to_string()).expect("write contract fixture");
        path
    }

    async fn spawn_test_server(
        bridge: TestBridge,
        handshake_ms: u64,
        baseline_ms: u64,
    ) -> (ServerInstance, PathBuf) {
        let dir = tempfile::tempdir().expect("tempdir").keep();
        let contract = crate::wire::load(&contract_file(&dir, handshake_ms, baseline_ms))
            .expect("contract fixture");
        let audit_path = dir.join("audit.ndjson");
        let audit = Arc::new(Mutex::new(
            AuditLog::open(&audit_path).expect("open audit"),
        ));
        let instance = server::start(ServerConfig { bridge: Box::new(bridge), audit, contract })
            .await
            .expect("server start");
        (instance, audit_path)
    }

    async fn default_server(bridge: TestBridge) -> (ServerInstance, PathBuf) {
        spawn_test_server(bridge, 5000, 5000).await
    }

    async fn connect(port: u16) -> Ws {
        let mut request = format!("ws://127.0.0.1:{port}/")
            .into_client_request()
            .expect("build request");
        request.headers_mut().insert(
            "Sec-WebSocket-Protocol",
            HeaderValue::from_static("lumio-hello-v1"),
        );
        connect_async(request).await.expect("connect with subprotocol").0
    }

    async fn connect_raw(port: u16) -> Result<Ws, String> {
        let request = format!("ws://127.0.0.1:{port}/")
            .into_client_request()
            .expect("build request");
        connect_async(request)
            .await
            .map(|(ws, _)| ws)
            .map_err(|error| error.to_string())
    }

    async fn send(ws: &mut Ws, value: Value) {
        ws.send(Message::Text(value.to_string().into()))
            .await
            .expect("send");
    }

    async fn recv(ws: &mut Ws, ms: u64) -> Option<Value> {
        let received = tokio::time::timeout(Duration::from_millis(ms), ws.next())
            .await
            .ok()?;
        let message = received?.ok()?;
        match message {
            Message::Text(text) => serde_json::from_str(text.as_str()).ok(),
            _ => None,
        }
    }

    async fn expect_type(ws: &mut Ws, wanted: &str) -> Value {
        let value = recv(ws, 3000)
            .await
            .unwrap_or_else(|| panic!("expected `{wanted}`, got nothing"));
        assert_eq!(value["messageType"], wanted, "got: {value}");
        value
    }

    async fn expect_error(ws: &mut Ws, code: &str) -> Value {
        let value = expect_type(ws, "Error").await;
        assert_eq!(value["code"], code, "got: {value}");
        value
    }

    async fn expect_close(ws: &mut Ws) {
        let deadline = tokio::time::Instant::now() + Duration::from_millis(3000);
        while tokio::time::Instant::now() < deadline {
            let remaining = deadline.saturating_duration_since(tokio::time::Instant::now());
            let Ok(maybe) = tokio::time::timeout(remaining, ws.next()).await else {
                break;
            };
            match maybe {
                None | Some(Err(_)) | Some(Ok(Message::Close(_))) => return,
                Some(Ok(_)) => continue,
            }
        }
        panic!("expected the server to close the connection");
    }

    async fn assert_no_message(ws: &mut Ws, ms: u64) {
        if let Some(value) = recv(ws, ms).await {
            panic!("expected no message, got: {value}");
        }
    }

    async fn handshake_and_baseline(ws: &mut Ws, role: &str) -> (String, Value) {
        send(
            ws,
            json!({
                "messageType": "Handshake",
                "role": role,
                "clientName": format!("{role}-client"),
                "contractId": "lumio.hello-wire.v1"
            }),
        )
        .await;
        let ack = expect_type(ws, "HandshakeAck").await;
        assert_eq!(ack["accepted"], true, "got: {ack}");
        assert_eq!(ack["role"], role, "got: {ack}");
        let snapshot = expect_type(ws, "FullSnapshot").await;
        let session_id = ack["sessionId"].as_str().expect("sessionId").to_owned();
        send(
            ws,
            json!({"messageType": "BaselineAck", "revision": snapshot["revision"]}),
        )
        .await;
        (session_id, snapshot)
    }

    fn command(role: &str, sequence: u64) -> Value {
        json!({
            "messageType": "InputCommand",
            "sender": role,
            "sequence": sequence,
            "kind": "hello",
            "payload": "Hello World",
            "payloadSha256": HELLO_SHA,
            "sentAtMs": 1000
        })
    }

    fn read_audit(path: &std::path::Path) -> Vec<Value> {
        std::fs::read_to_string(path)
            .expect("read audit")
            .lines()
            .map(|line| serde_json::from_str(line).expect("audit line is JSON"))
            .collect()
    }

    fn events<'a>(audit: &'a [Value], kind: &str) -> Vec<&'a Value> {
        audit.iter().filter(|event| event["kind"] == kind).collect()
    }

    async fn shutdown(instance: ServerInstance) {
        instance.request_shutdown();
        instance.join().await;
    }

    #[tokio::test]
    async fn normal_flow_routes_deltas_between_roles() {
        let (instance, audit_path) = default_server(TestBridge::new()).await;
        let port = instance.port;

        let mut browser = connect(port).await;
        let mut bot = connect(port).await;
        let (browser_id, browser_snapshot) = handshake_and_baseline(&mut browser, "browser").await;
        let (bot_id, _bot_snapshot) = handshake_and_baseline(&mut bot, "bot").await;
        assert_eq!(browser_snapshot["revision"], 0);
        assert_eq!(browser_snapshot["helloLog"].as_array().map(Vec::len), Some(0));
        assert_ne!(browser_id, bot_id);

        // browser -> bot
        send(&mut browser, command("browser", 1)).await;
        let delta = expect_type(&mut bot, "Delta").await;
        assert_eq!(delta["sender"], "browser");
        assert_eq!(delta["sequence"], 1);
        assert_eq!(delta["commandSequence"], 1);
        assert_eq!(delta["tickId"], 1);
        assert_eq!(delta["revision"], 1);
        assert_eq!(delta["kind"], "hello");
        assert_eq!(delta["payload"], "Hello World");
        assert_eq!(delta["payloadSha256"], HELLO_SHA);
        assert_eq!(delta["originSentAtMs"], 1000);
        assert!(delta["committedAtMs"].as_u64().is_some_and(|t| t >= 1000));
        // Sender never receives its own echo.
        assert_no_message(&mut browser, 300).await;

        // bot -> browser (reverse direction)
        send(&mut bot, command("bot", 1)).await;
        let delta = expect_type(&mut browser, "Delta").await;
        assert_eq!(delta["sender"], "bot");
        assert_eq!(delta["revision"], 2);
        assert_eq!(delta["tickId"], 2);
        assert_eq!(delta["commandSequence"], 1);
        assert_no_message(&mut bot, 300).await;

        shutdown(instance).await;

        let audit = read_audit(&audit_path);
        assert_eq!(events(&audit, "session_open").len(), 2);
        assert_eq!(events(&audit, "handshake_accepted").len(), 2);
        assert_eq!(events(&audit, "baseline_sent").len(), 2);
        assert_eq!(events(&audit, "baseline_acked").len(), 2);
        assert_eq!(events(&audit, "ingress_received").len(), 2);
        let ticks = events(&audit, "tick_committed");
        assert_eq!(ticks.len(), 2);
        assert_eq!(ticks[0]["deltaCount"], 1);
        assert_eq!(ticks[0]["senders"], json!(["browser"]));
        assert_eq!(ticks[1]["senders"], json!(["bot"]));
        let routed = events(&audit, "delta_routed");
        assert_eq!(routed.len(), 2);
        assert_eq!(routed[0]["sessionId"], json!(bot_id));
        assert_eq!(routed[0]["tickId"], 1);
        assert_eq!(routed[0]["revision"], 1);
        let shutdown_events = events(&audit, "server_shutdown");
        assert_eq!(shutdown_events.len(), 1);
        assert_eq!(shutdown_events[0]["sessions"], 2);
    }

    #[tokio::test]
    async fn shutdown_closes_websocket_handshake() {
        let (instance, _audit) = default_server(TestBridge::new()).await;
        let mut browser = connect(instance.port).await;
        handshake_and_baseline(&mut browser, "browser").await;
        shutdown(instance).await;
        expect_close(&mut browser).await;
    }

    #[tokio::test]
    async fn non_json_frame_is_bad_envelope_and_closes() {
        let (instance, _audit) = default_server(TestBridge::new()).await;
        let mut browser = connect(instance.port).await;
        handshake_and_baseline(&mut browser, "browser").await;
        browser
            .send(Message::Text("this is not json".into()))
            .await
            .expect("send garbage");
        expect_error(&mut browser, "bad_envelope").await;
        expect_close(&mut browser).await;
        shutdown(instance).await;
    }

    #[tokio::test]
    async fn unknown_message_type_is_unknown_mapping_and_closes() {
        let (instance, _audit) = default_server(TestBridge::new()).await;
        let mut browser = connect(instance.port).await;
        handshake_and_baseline(&mut browser, "browser").await;
        send(&mut browser, json!({"messageType": "Poke", "x": 1})).await;
        expect_error(&mut browser, "unknown_mapping").await;
        expect_close(&mut browser).await;
        shutdown(instance).await;
    }

    #[tokio::test]
    async fn duplicate_sequence_is_rejected_without_close() {
        let (instance, _audit) = default_server(TestBridge::new()).await;
        let mut browser = connect(instance.port).await;
        let mut bot = connect(instance.port).await;
        handshake_and_baseline(&mut browser, "browser").await;
        handshake_and_baseline(&mut bot, "bot").await;

        send(&mut browser, command("browser", 1)).await;
        expect_type(&mut bot, "Delta").await;
        send(&mut browser, command("browser", 1)).await;
        let error = expect_error(&mut browser, "duplicate_sequence").await;
        assert_eq!(error["sender"], "browser");
        assert_eq!(error["sequence"], 1);

        // Connection survived: a fresh sequence still commits and routes.
        send(&mut browser, command("browser", 2)).await;
        let delta = expect_type(&mut bot, "Delta").await;
        assert_eq!(delta["sequence"], 2);
        assert_eq!(delta["revision"], 2);
        shutdown(instance).await;
    }

    #[tokio::test]
    async fn sender_role_mismatch_is_unknown_role_without_close() {
        let (instance, _audit) = default_server(TestBridge::new()).await;
        let mut browser = connect(instance.port).await;
        handshake_and_baseline(&mut browser, "browser").await;
        send(&mut browser, command("bot", 1)).await;
        expect_error(&mut browser, "unknown_role").await;
        // Still open: the browser's own next command goes through the bridge.
        send(&mut browser, command("browser", 1)).await;
        assert_no_message(&mut browser, 300).await;
        shutdown(instance).await;
    }

    #[tokio::test]
    async fn third_session_is_rejected_with_session_limit() {
        let (instance, audit_path) = default_server(TestBridge::new()).await;
        let mut browser = connect(instance.port).await;
        let mut bot = connect(instance.port).await;
        handshake_and_baseline(&mut browser, "browser").await;
        handshake_and_baseline(&mut bot, "bot").await;

        let mut third = connect(instance.port).await;
        send(
            &mut third,
            json!({
                "messageType": "Handshake",
                "role": "browser",
                "clientName": "third",
                "contractId": "lumio.hello-wire.v1"
            }),
        )
        .await;
        let ack = expect_type(&mut third, "HandshakeAck").await;
        assert_eq!(ack["accepted"], false, "got: {ack}");
        expect_error(&mut third, "session_limit").await;
        expect_close(&mut third).await;
        shutdown(instance).await;

        let audit = read_audit(&audit_path);
        let rejected = events(&audit, "handshake_rejected");
        assert_eq!(rejected.len(), 1);
        assert_eq!(rejected[0]["code"], "session_limit");
    }

    #[tokio::test]
    async fn duplicate_role_is_rejected_with_role_taken() {
        let (instance, audit_path) = default_server(TestBridge::new()).await;
        let mut browser = connect(instance.port).await;
        handshake_and_baseline(&mut browser, "browser").await;

        let mut second = connect(instance.port).await;
        send(
            &mut second,
            json!({
                "messageType": "Handshake",
                "role": "browser",
                "clientName": "second-browser",
                "contractId": "lumio.hello-wire.v1"
            }),
        )
        .await;
        let ack = expect_type(&mut second, "HandshakeAck").await;
        assert_eq!(ack["accepted"], false);
        expect_error(&mut second, "role_taken").await;
        expect_close(&mut second).await;
        shutdown(instance).await;

        let audit = read_audit(&audit_path);
        assert_eq!(events(&audit, "handshake_rejected")[0]["code"], "role_taken");
    }

    #[tokio::test]
    async fn wrong_contract_id_is_unsupported_contract() {
        let (instance, audit_path) = default_server(TestBridge::new()).await;
        let mut client = connect(instance.port).await;
        send(
            &mut client,
            json!({
                "messageType": "Handshake",
                "role": "browser",
                "clientName": "c",
                "contractId": "lumio.somebody-else.v9"
            }),
        )
        .await;
        expect_error(&mut client, "unsupported_contract").await;
        expect_close(&mut client).await;
        shutdown(instance).await;

        let audit = read_audit(&audit_path);
        assert_eq!(events(&audit, "handshake_rejected")[0]["code"], "unsupported_contract");
    }

    #[tokio::test]
    async fn flood_fills_the_ingress_queue_and_reports_queue_full() {
        let gate = StallGate::default();
        let (instance, audit_path) =
            default_server(TestBridge::new().with_stall(gate)).await;
        let mut browser = connect(instance.port).await;
        handshake_and_baseline(&mut browser, "browser").await;

        for sequence in 1..=300 {
            send(&mut browser, command("browser", sequence)).await;
        }
        expect_error(&mut browser, "queue_full").await;
        expect_close(&mut browser).await;
        shutdown(instance).await;

        let audit = read_audit(&audit_path);
        let closed = events(&audit, "session_closed");
        assert!(
            closed.iter().any(|event| event["code"] == "queue_full"),
            "audit: {audit:?}"
        );
    }

    #[tokio::test]
    async fn bridge_enqueue_failure_reports_runtime_failure() {
        let mut bridge = TestBridge::new();
        bridge.fail_enqueue = true;
        let (instance, audit_path) = default_server(bridge).await;
        let mut browser = connect(instance.port).await;
        handshake_and_baseline(&mut browser, "browser").await;

        send(&mut browser, command("browser", 1)).await;
        expect_error(&mut browser, "runtime_failure").await;
        expect_close(&mut browser).await;
        shutdown(instance).await;

        let audit = read_audit(&audit_path);
        let rejected = events(&audit, "ingress_rejected");
        assert_eq!(rejected.len(), 1);
        assert_eq!(rejected[0]["code"], "runtime_failure");
        assert_eq!(rejected[0]["sender"], "browser");
        assert_eq!(rejected[0]["sequence"], 1);
    }

    #[tokio::test]
    async fn payload_hash_mismatch_is_recoverable() {
        let (instance, audit_path) = default_server(TestBridge::new()).await;
        let mut browser = connect(instance.port).await;
        handshake_and_baseline(&mut browser, "browser").await;

        let mut bad = command("browser", 1);
        bad["payloadSha256"] = json!("c".repeat(64));
        send(&mut browser, bad).await;
        expect_error(&mut browser, "bad_payload_hash").await;

        // Connection survived; the corrected command commits.
        send(&mut browser, command("browser", 1)).await;
        assert_no_message(&mut browser, 300).await;
        shutdown(instance).await;

        let audit = read_audit(&audit_path);
        assert!(events(&audit, "ingress_rejected")
            .iter()
            .any(|event| event["code"] == "bad_payload_hash"));
    }

    #[tokio::test]
    async fn stale_baseline_ack_closes() {
        let (instance, audit_path) = default_server(TestBridge::new()).await;
        let mut browser = connect(instance.port).await;
        send(
            &mut browser,
            json!({
                "messageType": "Handshake",
                "role": "browser",
                "clientName": "c",
                "contractId": "lumio.hello-wire.v1"
            }),
        )
        .await;
        expect_type(&mut browser, "HandshakeAck").await;
        let snapshot = expect_type(&mut browser, "FullSnapshot").await;
        let wrong = snapshot["revision"].as_u64().expect("revision") + 9;
        send(&mut browser, json!({"messageType": "BaselineAck", "revision": wrong})).await;
        expect_error(&mut browser, "stale_revision").await;
        expect_close(&mut browser).await;
        shutdown(instance).await;

        let audit = read_audit(&audit_path);
        assert!(events(&audit, "session_closed")
            .iter()
            .any(|event| event["code"] == "stale_revision"));
    }

    #[tokio::test]
    async fn missing_handshake_times_out() {
        let (instance, audit_path) = spawn_test_server(TestBridge::new(), 300, 5000).await;
        let mut client = connect(instance.port).await;
        expect_close(&mut client).await;
        shutdown(instance).await;

        let audit = read_audit(&audit_path);
        assert!(events(&audit, "session_closed")
            .iter()
            .any(|event| event["code"] == "handshake_timeout"));
    }

    #[tokio::test]
    async fn missing_baseline_ack_times_out() {
        let (instance, audit_path) = spawn_test_server(TestBridge::new(), 5000, 300).await;
        let mut client = connect(instance.port).await;
        send(
            &mut client,
            json!({
                "messageType": "Handshake",
                "role": "browser",
                "clientName": "c",
                "contractId": "lumio.hello-wire.v1"
            }),
        )
        .await;
        expect_type(&mut client, "HandshakeAck").await;
        expect_type(&mut client, "FullSnapshot").await;
        expect_close(&mut client).await;
        shutdown(instance).await;

        let audit = read_audit(&audit_path);
        assert!(events(&audit, "session_closed")
            .iter()
            .any(|event| event["code"] == "baseline_timeout"));
    }

    #[tokio::test]
    async fn input_before_baseline_ack_is_session_closed() {
        let (instance, audit_path) = default_server(TestBridge::new()).await;
        let mut browser = connect(instance.port).await;
        send(
            &mut browser,
            json!({
                "messageType": "Handshake",
                "role": "browser",
                "clientName": "c",
                "contractId": "lumio.hello-wire.v1"
            }),
        )
        .await;
        expect_type(&mut browser, "HandshakeAck").await;
        let snapshot = expect_type(&mut browser, "FullSnapshot").await;

        send(&mut browser, command("browser", 1)).await;
        expect_error(&mut browser, "session_closed").await;

        // The session still completes its baseline afterwards.
        send(
            &mut browser,
            json!({"messageType": "BaselineAck", "revision": snapshot["revision"]}),
        )
        .await;
        send(&mut browser, command("browser", 1)).await;
        assert_no_message(&mut browser, 300).await;
        shutdown(instance).await;

        let audit = read_audit(&audit_path);
        assert!(events(&audit, "baseline_acked").len() == 1);
        assert!(events(&audit, "ingress_rejected")
            .iter()
            .any(|event| event["code"] == "session_closed"));
    }

    #[tokio::test]
    async fn late_joiner_baselines_on_current_state() {
        let (instance, _audit) = default_server(TestBridge::new()).await;
        let mut browser = connect(instance.port).await;
        handshake_and_baseline(&mut browser, "browser").await;
        send(&mut browser, command("browser", 1)).await;
        assert_no_message(&mut browser, 300).await;

        let mut bot = connect(instance.port).await;
        let (_bot_id, snapshot) = handshake_and_baseline(&mut bot, "bot").await;
        assert_eq!(snapshot["revision"], 1, "snapshot: {snapshot}");
        assert_eq!(snapshot["tickId"], 1);
        let log = snapshot["helloLog"].as_array().expect("helloLog");
        assert_eq!(log.len(), 1);
        assert_eq!(log[0]["sender"], "browser");
        assert_eq!(log[0]["revision"], 1);
        shutdown(instance).await;
    }

    #[tokio::test]
    async fn subprotocol_is_enforced() {
        let (instance, _audit) = default_server(TestBridge::new()).await;
        let error = connect_raw(instance.port).await.expect_err("must be rejected");
        assert!(!error.is_empty());
        shutdown(instance).await;
    }
}
