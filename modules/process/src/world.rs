//! World loop: the single authoritative orchestrator.
//!
//! Owns the runtime bridge, the session table, tick scheduling, delta routing
//! and the graceful shutdown sequence. Network tasks only feed bounded queues;
//! every Delta sent to a client comes from a bridge `tick`/`snapshot` response
//! (runtime-shaped, `messageType` injected here), never from ingress.

use std::collections::HashMap;
use std::time::Duration;

use serde_json::json;
use serde_json::Value;
use tokio::sync::mpsc;
use tokio::sync::watch;
use tokio::task::JoinHandle;
use tokio::time::Instant;

use crate::audit::SharedAudit;
use crate::runtime_bridge::parse_snapshot_response;
use crate::runtime_bridge::BridgeError;
use crate::runtime_bridge::RuntimeBridge;
use crate::server::error_message;
use crate::server::full_snapshot;
use crate::server::handshake_ack;
use crate::server::Egress;
use crate::server::IngressEvent;
use crate::server::WorldEvent;
use crate::session::AdmissionError;
use crate::session::SessionPhase;
use crate::session::SessionTable;
use crate::wire::ErrorCode;
use crate::wire::Role;
use crate::wire::WireContract;

/// How often admission/baseline deadlines are swept.
const SWEEP_PERIOD: Duration = Duration::from_millis(100);
/// How long shutdown waits for each connection's close handshake.
const CLOSE_JOIN_TIMEOUT: Duration = Duration::from_millis(1000);

/// What the world loop reports after graceful shutdown.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorldOutcome {
    /// Why shutdown started (`stdin shutdown`, `ctrl-c`, ...).
    pub reason: String,
    /// Sessions that were open when shutdown began.
    pub sessions: usize,
}

/// Wiring handed to the world loop by [`crate::server::start`].
pub(crate) struct WorldArgs {
    /// Authoritative runtime bridge.
    pub(crate) bridge: Box<dyn RuntimeBridge>,
    /// Shared audit sink.
    pub(crate) audit: SharedAudit,
    /// Parsed wire contract.
    pub(crate) contract: WireContract,
    /// Clone of the world inbox sender (used to spawn per-session forwarders).
    pub(crate) inbox_tx: mpsc::Sender<WorldEvent>,
    /// Central world inbox (opened sessions + validated ingress).
    pub(crate) inbox_rx: mpsc::Receiver<WorldEvent>,
    /// Connection teardown notifications.
    pub(crate) disconnect_rx: mpsc::Receiver<(String, &'static str)>,
    /// Shutdown signal (true = stop serving and quiesce).
    pub(crate) shutdown_rx: watch::Receiver<bool>,
}

/// Connection plumbing for one registered session.
struct SessionConn {
    egress: mpsc::Sender<Egress>,
    reader: Option<JoinHandle<()>>,
    writer: Option<JoinHandle<()>>,
    handshake_deadline: Instant,
    baseline_deadline: Option<Instant>,
}

/// A command accepted into the runtime since the last tick.
struct PendingCommand {
    session_id: String,
    role: Role,
    sequence: u64,
}

/// One delta on its way to one recipient session.
struct PendingRoute {
    session_id: String,
    text: String,
    sender: Option<Role>,
    sequence: Option<u64>,
    payload_sha256: String,
}

/// The world loop state.
struct World {
    bridge: Box<dyn RuntimeBridge>,
    audit: SharedAudit,
    contract: WireContract,
    inbox_tx: mpsc::Sender<WorldEvent>,
    inbox_rx: mpsc::Receiver<WorldEvent>,
    disconnect_rx: mpsc::Receiver<(String, &'static str)>,
    shutdown_rx: watch::Receiver<bool>,
    table: SessionTable,
    conns: HashMap<String, SessionConn>,
    pending: Vec<PendingCommand>,
    dirty: bool,
}

fn epoch_now_ms() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .map(|millis| u64::try_from(millis).unwrap_or(u64::MAX))
        .unwrap_or_default()
}

/// Run the world loop until shutdown; returns the shutdown outcome after the
/// graceful sequence (sessions closed, bridge shutdown, audit flushed).
pub(crate) async fn run_world(args: WorldArgs) -> WorldOutcome {
    let WorldArgs {
        bridge,
        audit,
        contract,
        inbox_tx,
        inbox_rx,
        disconnect_rx,
        shutdown_rx,
    } = args;
    let mut world = World {
        table: SessionTable::new(contract.limits.max_sessions),
        bridge,
        audit,
        contract,
        inbox_tx,
        inbox_rx,
        disconnect_rx,
        shutdown_rx,
        conns: HashMap::new(),
        pending: Vec::new(),
        dirty: false,
    };
    let reason = world.serve().await;
    world.graceful_shutdown(reason).await
}

async fn forward_ingress(
    session_id: String,
    mut ingress_rx: mpsc::Receiver<IngressEvent>,
    inbox_tx: mpsc::Sender<WorldEvent>,
) {
    while let Some(event) = ingress_rx.recv().await {
        let event = WorldEvent::Ingress {
            session_id: session_id.clone(),
            event,
        };
        if inbox_tx.send(event).await.is_err() {
            break;
        }
    }
}

enum Trigger {
    World(Option<WorldEvent>),
    Disconnect(Option<(String, &'static str)>),
    Sweep,
    Shutdown,
}

impl World {
    async fn serve(&mut self) -> String {
        let mut sweep = tokio::time::interval(SWEEP_PERIOD);
        loop {
            let trigger = tokio::select! {
                biased;
                _ = self.shutdown_rx.changed() => Trigger::Shutdown,
                disconnected = self.disconnect_rx.recv() => Trigger::Disconnect(disconnected),
                event = self.inbox_rx.recv() => Trigger::World(event),
                _ = sweep.tick() => Trigger::Sweep,
            };
            match trigger {
                Trigger::Shutdown => {
                    // The watch sender only fires for a real shutdown request;
                    // dropping it (server handle gone) stops the world too.
                    return "shutdown signal".to_owned();
                }
                Trigger::Disconnect(Some((session_id, code))) => {
                    self.handle_disconnected(&session_id, code);
                    self.drain_and_tick();
                }
                Trigger::Disconnect(None) | Trigger::World(None) => {
                    return "accept loop gone".to_owned();
                }
                Trigger::World(Some(event)) => {
                    self.handle_event(event);
                    self.drain_and_tick();
                }
                Trigger::Sweep => self.sweep_deadlines(),
            }
        }
    }

    fn handle_event(&mut self, event: WorldEvent) {
        match event {
            WorldEvent::Opened {
                session_id,
                egress,
                ingress_rx,
                reader,
                writer,
            } => {
                self.table.open(&session_id);
                self.conns.insert(
                    session_id.clone(),
                    SessionConn {
                        egress,
                        reader: Some(reader),
                        writer: Some(writer),
                        handshake_deadline: Instant::now()
                            + Duration::from_millis(self.contract.limits.handshake_timeout_ms),
                        baseline_deadline: None,
                    },
                );
                let forwarder =
                    forward_ingress(session_id, ingress_rx, mpsc::Sender::clone(&self.inbox_tx));
                tokio::spawn(forwarder);
            }
            WorldEvent::Ingress { session_id, event } => self.handle_ingress(&session_id, event),
        }
    }

    fn handle_ingress(&mut self, session_id: &str, event: IngressEvent) {
        match event {
            IngressEvent::Handshake { role, client_name } => {
                self.handle_handshake(session_id, role, &client_name);
            }
            IngressEvent::BaselineAck { revision } => {
                self.handle_baseline_ack(session_id, revision);
            }
            IngressEvent::Command {
                sender,
                sequence,
                payload_sha256,
                envelope,
            } => {
                self.handle_command(session_id, sender, sequence, &payload_sha256, &envelope);
            }
        }
    }

    fn handle_handshake(&mut self, session_id: &str, role: Role, client_name: &str) {
        let admission = self.table.admit(session_id, role, client_name);
        let Err(error) = admission else {
            self.admit_session(session_id, role, client_name);
            return;
        };
        let (code, detail) = match error {
            AdmissionError::SessionLimit => {
                (ErrorCode::SessionLimit, "session limit reached".to_owned())
            }
            AdmissionError::RoleTaken(taken) => (
                ErrorCode::RoleTaken,
                format!("role `{taken}` is already held"),
            ),
        };
        self.send_text(
            session_id,
            handshake_ack(
                session_id,
                role,
                false,
                Some(&format!("{}: {detail}", code.as_str())),
            ),
        );
        self.send_text(session_id, error_message(code, &detail, None, None));
        if let Some(mut log) = self.audit_lock() {
            log.handshake_rejected(session_id, code.as_str(), &detail);
        }
        self.close_session(session_id, code.as_str());
    }

    fn admit_session(&mut self, session_id: &str, role: Role, client_name: &str) {
        let snapshot = self
            .bridge
            .snapshot()
            .and_then(|body| parse_snapshot_response(&body));
        let Ok(view) = snapshot else {
            self.send_text(
                session_id,
                handshake_ack(
                    session_id,
                    role,
                    false,
                    Some("runtime_failure: snapshot failed"),
                ),
            );
            self.send_text(
                session_id,
                error_message(ErrorCode::RuntimeFailure, "snapshot failed", None, None),
            );
            if let Some(mut log) = self.audit_lock() {
                log.handshake_rejected(
                    session_id,
                    ErrorCode::RuntimeFailure.as_str(),
                    "snapshot failed",
                );
            }
            self.close_session(session_id, ErrorCode::RuntimeFailure.as_str());
            return;
        };
        self.send_text(session_id, handshake_ack(session_id, role, true, None));
        self.send_text(session_id, full_snapshot(session_id, &view));
        if let Some(mut log) = self.audit_lock() {
            log.handshake_accepted(session_id, role.as_str(), client_name);
            log.baseline_sent(session_id, view.revision, view.tick_id);
        }
        self.table.mark_baselined(session_id, view.revision);
        if let Some(conn) = self.conns.get_mut(session_id) {
            conn.baseline_deadline = Some(
                Instant::now() + Duration::from_millis(self.contract.limits.baseline_timeout_ms),
            );
        }
    }

    fn handle_baseline_ack(&mut self, session_id: &str, revision: u64) {
        if self.table.phase(session_id) != Some(SessionPhase::Baselined) {
            self.reject_command(
                session_id,
                ErrorCode::BadEnvelope,
                "BaselineAck outside the baselined phase",
                None,
                None,
            );
            self.close_session(session_id, ErrorCode::BadEnvelope.as_str());
            return;
        }
        if self.table.baselined_revision(session_id) != Some(revision) {
            self.reject_command(
                session_id,
                ErrorCode::StaleRevision,
                "BaselineAck revision does not match the sent baseline",
                None,
                None,
            );
            self.close_session(session_id, ErrorCode::StaleRevision.as_str());
            return;
        }
        if let Some(mut log) = self.audit_lock() {
            log.baseline_acked(session_id, revision);
        }
        self.table.mark_active(session_id);
        if let Some(conn) = self.conns.get_mut(session_id) {
            conn.baseline_deadline = None;
        }
    }

    fn handle_command(
        &mut self,
        session_id: &str,
        sender: Role,
        sequence: u64,
        payload_sha256: &str,
        envelope: &str,
    ) {
        if self.table.phase(session_id) == Some(SessionPhase::Closed) {
            // Queue residue after the session ended; nothing to report.
            return;
        }
        if let Some(mut log) = self.audit_lock() {
            log.ingress_received(session_id, sender.as_str(), sequence, payload_sha256);
        }
        if self.table.phase(session_id) != Some(SessionPhase::Active) {
            self.reject_command(
                session_id,
                ErrorCode::SessionClosed,
                "session is not in an input-accepting state",
                Some(sender),
                Some(sequence),
            );
            return;
        }
        if self.table.role(session_id) != Some(sender) {
            self.reject_command(
                session_id,
                ErrorCode::UnknownRole,
                "sender does not match the session role",
                Some(sender),
                Some(sequence),
            );
            return;
        }
        if self.table.is_duplicate_sequence(sender, sequence) {
            self.reject_command(
                session_id,
                ErrorCode::DuplicateSequence,
                "sequence is not above the committed watermark",
                Some(sender),
                Some(sequence),
            );
            return;
        }
        match self.bridge.enqueue(envelope) {
            Ok(()) => {
                self.pending.push(PendingCommand {
                    session_id: session_id.to_owned(),
                    role: sender,
                    sequence,
                });
                self.dirty = true;
            }
            Err(BridgeError::Rejected { code }) => {
                self.reject_command(
                    session_id,
                    code,
                    "runtime rejected the command",
                    Some(sender),
                    Some(sequence),
                );
            }
            Err(BridgeError::Failed { detail }) => {
                self.reject_command(
                    session_id,
                    ErrorCode::RuntimeFailure,
                    detail,
                    Some(sender),
                    Some(sequence),
                );
                self.close_session(session_id, ErrorCode::RuntimeFailure.as_str());
            }
        }
    }

    fn handle_disconnected(&mut self, session_id: &str, code: &'static str) {
        if self.table.phase(session_id) == Some(SessionPhase::Closed) {
            return;
        }
        if let Some(mut log) = self.audit_lock() {
            log.session_closed(session_id, code);
        }
        self.table.close(session_id);
        self.conns.remove(session_id);
    }

    fn sweep_deadlines(&mut self) {
        let now = Instant::now();
        let expired: Vec<(String, &'static str)> = self
            .conns
            .iter()
            .filter_map(|(session_id, conn)| {
                let phase = self.table.phase(session_id)?;
                let deadline = match phase {
                    SessionPhase::AwaitHandshake => {
                        Some((conn.handshake_deadline, "handshake_timeout"))
                    }
                    SessionPhase::Baselined => conn
                        .baseline_deadline
                        .map(|deadline| (deadline, "baseline_timeout")),
                    _ => None,
                }?;
                (now >= deadline.0).then(|| (session_id.clone(), deadline.1))
            })
            .collect();
        for (session_id, code) in expired {
            self.close_session(&session_id, code);
        }
    }

    fn drain_and_tick(&mut self) {
        while let Ok(event) = self.inbox_rx.try_recv() {
            self.handle_event(event);
        }
        self.run_tick();
    }

    fn run_tick(&mut self) {
        if !self.dirty {
            return;
        }
        self.dirty = false;
        match self.bridge.tick(epoch_now_ms()) {
            Err(error) => self.fail_pending(&error),
            Ok(outcome) => {
                if !outcome.deltas.is_empty() {
                    self.route_deltas(outcome.tick_id, outcome.revision, &outcome.deltas);
                }
                self.pending.clear();
            }
        }
    }

    fn route_deltas(&mut self, tick_id: u64, revision: u64, deltas: &[Value]) {
        let delta_count = deltas.len();
        let mut senders: Vec<String> = Vec::new();
        let mut routed: Vec<PendingRoute> = Vec::new();
        for delta in deltas {
            let sender = delta
                .get("sender")
                .and_then(Value::as_str)
                .and_then(Role::parse);
            let sequence = delta.get("sequence").and_then(Value::as_u64);
            let payload_sha256 = delta
                .get("payloadSha256")
                .and_then(Value::as_str)
                .unwrap_or_default()
                .to_owned();
            if let (Some(sender), Some(sequence)) = (sender, sequence) {
                self.table.record_commit(sender, sequence);
            }
            if let Some(sender) = sender {
                let name = sender.as_str();
                if !senders.iter().any(|existing| existing == name) {
                    senders.push(name.to_owned());
                }
            }
            let mut wire = delta.clone();
            wire["messageType"] = json!("Delta");
            let text = wire.to_string();
            for session_id in self.active_session_ids_except(sender) {
                routed.push(PendingRoute {
                    session_id,
                    text: text.clone(),
                    sender,
                    sequence,
                    payload_sha256: payload_sha256.clone(),
                });
            }
        }
        if let Some(mut log) = self.audit_lock() {
            log.tick_committed(tick_id, revision, delta_count, &senders);
        }
        for target in routed {
            if let (Some(sender), Some(sequence)) = (target.sender, target.sequence) {
                if let Some(mut log) = self.audit_lock() {
                    log.delta_routed(
                        &target.session_id,
                        sender.as_str(),
                        sequence,
                        tick_id,
                        revision,
                        &target.payload_sha256,
                    );
                }
            }
            self.send_text(&target.session_id, target.text);
        }
    }

    fn active_session_ids_except(&self, sender: Option<Role>) -> Vec<String> {
        self.conns
            .keys()
            .filter(|session_id| self.table.phase(session_id) == Some(SessionPhase::Active))
            .filter(|session_id| self.table.role(session_id) != sender)
            .cloned()
            .collect()
    }

    fn fail_pending(&mut self, error: &BridgeError) {
        let code = error.wire_code();
        let detail = match error {
            BridgeError::Failed { detail } => *detail,
            BridgeError::Rejected { .. } => "runtime rejected the tick",
        };
        let pending = std::mem::take(&mut self.pending);
        let mut reported: Vec<String> = Vec::new();
        for command in pending {
            if reported.contains(&command.session_id) {
                continue;
            }
            reported.push(command.session_id.clone());
            self.reject_command(
                &command.session_id,
                code,
                detail,
                Some(command.role),
                Some(command.sequence),
            );
            if matches!(error, BridgeError::Failed { .. }) {
                self.close_session(&command.session_id, code.as_str());
            }
        }
    }

    fn reject_command(
        &mut self,
        session_id: &str,
        code: ErrorCode,
        detail: &str,
        sender: Option<Role>,
        sequence: Option<u64>,
    ) {
        self.send_text(session_id, error_message(code, detail, sender, sequence));
        if let (Some(role), Some(sequence)) = (sender, sequence) {
            if let Some(mut log) = self.audit_lock() {
                log.ingress_rejected(session_id, role.as_str(), sequence, code.as_str());
            }
        }
    }

    fn send_text(&mut self, session_id: &str, text: String) {
        let Some(conn) = self.conns.get_mut(session_id) else {
            return;
        };
        if conn.egress.try_send(Egress::Text(text)).is_err() {
            // Egress backlog: shed the session instead of blocking the loop.
            self.close_session(session_id, "egress_stalled");
        }
    }

    fn close_session(&mut self, session_id: &str, code: &'static str) {
        if let Some(conn) = self.conns.remove(session_id) {
            let _ = conn.egress.try_send(Egress::Close);
            if let Some(mut log) = self.audit_lock() {
                log.session_closed(session_id, code);
            }
            self.table.close(session_id);
        }
    }

    fn audit_lock(&self) -> Option<std::sync::MutexGuard<'_, crate::audit::AuditLog>> {
        self.audit.lock().ok()
    }

    async fn graceful_shutdown(mut self, reason: String) -> WorldOutcome {
        let sessions = self.table.live_count();
        let conns = std::mem::take(&mut self.conns);
        for (session_id, conn) in conns {
            if let Some(mut log) = self.audit_lock() {
                log.session_closed(&session_id, "server_shutdown");
            }
            self.table.close(&session_id);
            let SessionConn {
                egress,
                reader,
                writer,
                ..
            } = conn;
            // The reader task holds its own egress clone, so the writer only
            // observes the close through this explicit sentinel.
            let _ = egress.try_send(Egress::Close);
            drop(egress);
            if let Some(reader) = reader {
                let _ = tokio::time::timeout(CLOSE_JOIN_TIMEOUT, reader).await;
            }
            if let Some(writer) = writer {
                let _ = tokio::time::timeout(CLOSE_JOIN_TIMEOUT, writer).await;
            }
        }
        if let Err(error) = self.bridge.shutdown() {
            eprintln!("warning: runtime shutdown op failed: {error:?}");
        }
        if let Some(mut log) = self.audit_lock() {
            log.server_shutdown(&reason, sessions);
            log.flush();
        }
        // Dropping `self` destroys the bridge (destroy_clr_host) and then the
        // SDK lease (FreeLibrary), in the contract's shutdown order.
        WorldOutcome { reason, sessions }
    }
}

#[cfg(test)]
mod e2e {
    use std::path::PathBuf;
    use std::sync::Arc;
    use std::sync::Mutex;
    use std::time::Duration;

    use futures_util::sink::SinkExt;
    use futures_util::StreamExt;
    use serde_json::{json, Value};
    use tokio::net::TcpStream;
    use tokio_tungstenite::connect_async;
    use tokio_tungstenite::tungstenite::client::IntoClientRequest;
    use tokio_tungstenite::tungstenite::http::HeaderValue;
    use tokio_tungstenite::tungstenite::Message;
    use tokio_tungstenite::MaybeTlsStream;
    use tokio_tungstenite::WebSocketStream;

    use crate::audit::AuditLog;
    use crate::runtime_bridge::tests::StallGate;
    use crate::runtime_bridge::tests::TestBridge;
    use crate::server;
    use crate::server::ServerConfig;
    use crate::server::ServerInstance;

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
        let audit = Arc::new(Mutex::new(AuditLog::open(&audit_path).expect("open audit")));
        let instance = server::start(ServerConfig {
            bridge: Box::new(bridge),
            audit,
            contract,
        })
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
        connect_async(request)
            .await
            .expect("connect with subprotocol")
            .0
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
                None | Some(Err(_) | Ok(Message::Close(_))) => return,
                Some(Ok(_)) => {}
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
        assert_eq!(
            browser_snapshot["helloLog"].as_array().map(Vec::len),
            Some(0)
        );
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
        // Still open: the browser's own next command reaches the runtime.
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
        assert_eq!(
            events(&audit, "handshake_rejected")[0]["code"],
            "role_taken"
        );
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
        assert_eq!(
            events(&audit, "handshake_rejected")[0]["code"],
            "unsupported_contract"
        );
    }

    #[tokio::test]
    async fn flood_fills_the_ingress_queue_and_reports_queue_full() {
        let gate = StallGate::default();
        let (instance, audit_path) = default_server(TestBridge::new().with_stall(gate)).await;
        let mut browser = connect(instance.port).await;
        handshake_and_baseline(&mut browser, "browser").await;

        for sequence in 1..=300 {
            // The server closes mid-flood once the queue overflows; further
            // sends failing is expected, not an error.
            let frame = Message::Text(command("browser", sequence).to_string().into());
            if browser.send(frame).await.is_err() {
                break;
            }
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
        send(
            &mut browser,
            json!({"messageType": "BaselineAck", "revision": wrong}),
        )
        .await;
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
        assert_eq!(events(&audit, "baseline_acked").len(), 1);
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
        let error = connect_raw(instance.port)
            .await
            .expect_err("must be rejected");
        assert!(!error.is_empty());
        shutdown(instance).await;
    }
}
