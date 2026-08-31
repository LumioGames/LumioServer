//! Transport: loopback WebSocket listener, per-connection reader/writer tasks
//! and envelope validation.
//!
//! The network layer never produces Deltas: it parses envelopes, enforces the
//! contract's field shapes and pushes validated events into bounded
//! per-session queues consumed by the world loop.

use std::path::Path;
use std::sync::atomic::AtomicU64;
use std::sync::atomic::Ordering;

use futures_util::StreamExt;
use futures_util::sink::SinkExt;
use serde_json::{Value, json};
use tokio::net::TcpListener;
use tokio::net::TcpStream;
use tokio::sync::mpsc;
use tokio::sync::mpsc::Receiver;
use tokio::sync::mpsc::Sender;
use tokio::sync::mpsc::error::TrySendError;
use tokio::sync::watch;
use tokio::task::JoinHandle;
use tokio_tungstenite::WebSocketStream;
use tokio_tungstenite::accept_hdr_async_with_config;
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::tungstenite::handshake::server::ErrorResponse;
use tokio_tungstenite::tungstenite::handshake::server::Request;
use tokio_tungstenite::tungstenite::handshake::server::Response;
use tokio_tungstenite::tungstenite::http::HeaderValue;
use tokio_tungstenite::tungstenite::http::Response as HttpResponse;
use tokio_tungstenite::tungstenite::http::StatusCode;
use tokio_tungstenite::tungstenite::protocol::WebSocketConfig;

use crate::audit::SharedAudit;
use crate::runtime_bridge::RuntimeBridge;
use crate::runtime_bridge::SnapshotView;
use crate::wire::ErrorCode;
use crate::wire::Limits;
use crate::wire::Role;
use crate::wire::WireContract;
use crate::world::WorldArgs;
use crate::world::WorldOutcome;
use crate::world::run_world;

/// Prefix of the single-line readiness signal on stdout.
pub const SERVER_READY_PREFIX: &str = "SERVER_READY ";

/// Egress channel item: a wire message or a server-initiated close.
#[derive(Debug, Clone)]
pub(crate) enum Egress {
    /// Serialized wire JSON (HandshakeAck / FullSnapshot / Delta / Error).
    Text(String),
    /// Ask the writer to run the WebSocket close handshake.
    Close,
}

/// Validated client event (output of envelope validation).
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum IngressEvent {
    /// Handshake envelope.
    Handshake {
        /// Requested role.
        role: Role,
        /// Client-provided name.
        client_name: String,
    },
    /// BaselineAck envelope.
    BaselineAck {
        /// Acknowledged revision.
        revision: u64,
    },
    /// InputCommand envelope (original JSON kept for the runtime bridge).
    Command {
        /// Declared sender.
        sender: Role,
        /// Command sequence.
        sequence: u64,
        /// Payload hash as sent.
        payload_sha256: String,
        /// Original envelope JSON, forwarded verbatim to the runtime.
        envelope: String,
    },
}

/// Envelope validation failure.
#[derive(Debug, Clone)]
pub(crate) struct EnvelopeError {
    /// Stable code for the Error wire message.
    pub code: ErrorCode,
    /// Human-readable detail.
    pub detail: String,
    /// Whether the connection must be closed after replying.
    pub fatal: bool,
    /// Sender echo for the Error message, when the envelope carried one.
    pub sender: Option<Role>,
    /// Sequence echo for the Error message, when the envelope carried one.
    pub sequence: Option<u64>,
}

/// Events consumed by the world loop.
#[derive(Debug)]
#[allow(dead_code)] // Red-phase stub: all variants are consumed by the world implementation.
pub(crate) enum WorldEvent {
    /// A connection completed the WebSocket handshake and registered.
    Opened {
        /// Server-assigned session id.
        session_id: String,
        /// Remote socket address.
        remote: String,
        /// World-held egress sender.
        egress: mpsc::Sender<Egress>,
        /// Per-session bounded ingress queue (capacity from the contract).
        ingress_rx: mpsc::Receiver<IngressEvent>,
        /// Reader task (joined by the world during shutdown).
        reader: JoinHandle<()>,
        /// Writer task (joined by the world during shutdown).
        writer: JoinHandle<()>,
    },
    /// A validated client event arrived through a session queue.
    Ingress {
        /// Session id.
        session_id: String,
        /// The event.
        event: IngressEvent,
    },
    /// A connection's read loop ended (close frame, error, or fatal reject).
    Disconnected {
        /// Session id.
        session_id: String,
        /// Why the read loop ended (audit vocabulary).
        code: &'static str,
    },
}

/// Everything the server needs at start.
pub struct ServerConfig {
    /// Authoritative runtime bridge, owned by the world loop from here on.
    pub bridge: Box<dyn RuntimeBridge>,
    /// Shared audit sink.
    pub audit: SharedAudit,
    /// Parsed wire contract.
    pub contract: WireContract,
}

/// Running server handle returned by [`start`].
pub struct ServerInstance {
    /// Bound loopback port.
    pub port: u16,
    /// Process id (readiness evidence).
    pub pid: u32,
    shutdown_tx: watch::Sender<bool>,
    world: JoinHandle<WorldOutcome>,
}

impl ServerInstance {
    /// Ask the world loop to run the graceful shutdown sequence.
    pub fn request_shutdown(&self) {
        let _ = self.shutdown_tx.send(true);
    }

    /// Wait for the world loop to finish closing sessions and the runtime.
    ///
    /// # Panics
    ///
    /// Panics when the world task was cancelled or panicked itself.
    pub async fn join(self) -> WorldOutcome {
        self.world
            .await
            .expect("world loop must finish the graceful shutdown")
    }
}

/// Bind the loopback listener and start the accept + world loops.
///
/// # Errors
///
/// Returns a human-readable failure when the listener cannot be bound.
pub async fn start(config: ServerConfig) -> Result<ServerInstance, String> {
    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .map_err(|error| format!("bind 127.0.0.1:0: {error}"))?;
    let port = listener
        .local_addr()
        .map_err(|error| format!("local_addr: {error}"))?
        .port();
    Ok(spawn_server(listener, port, config))
}

/// Per-session egress bound. Deltas are tiny; 128 frames of slack before the
/// writer falls behind far enough that the session is closed instead.
const EGRESS_CAPACITY: usize = 128;

/// Disconnection notifications from connection readers to the world.
pub(crate) type DisconnectTx = Sender<(String, &'static str)>;

/// Spawn the world loop and accept loop around an already-bound listener.
fn spawn_server(listener: TcpListener, port: u16, config: ServerConfig) -> ServerInstance {
    let ServerConfig { bridge, audit, contract } = config;
    // The world inbox mirrors the per-session ingress bound so backpressure
    // reaches the reader queues (queue_full) instead of growing unbounded.
    let (world_tx, world_rx) = mpsc::channel(contract.limits.ingress_queue_per_session);
    let (disconnect_tx, disconnect_rx) = mpsc::channel::<(String, &'static str)>(32);
    let (shutdown_tx, shutdown_rx) = watch::channel(false);
    let world = tokio::spawn(run_world(WorldArgs {
        bridge,
        audit: std::sync::Arc::clone(&audit),
        contract: contract.clone(),
        world_rx,
        disconnect_rx,
        shutdown_rx: shutdown_rx.clone(),
    }));
    tokio::spawn(accept_loop(listener, world_tx, disconnect_tx, audit, contract, shutdown_rx));
    ServerInstance {
        port,
        pid: std::process::id(),
        shutdown_tx,
        world,
    }
}

async fn accept_loop(
    listener: TcpListener,
    world_tx: Sender<WorldEvent>,
    disconnect_tx: DisconnectTx,
    audit: SharedAudit,
    contract: WireContract,
    mut shutdown_rx: watch::Receiver<bool>,
) {
    loop {
        let accepted = tokio::select! {
            _ = shutdown_rx.changed() => break,
            accepted = listener.accept() => accepted,
        };
        let Ok((stream, remote)) = accepted else {
            break;
        };
        let task = connection_task(
            stream,
            remote,
            world_tx.clone(),
            disconnect_tx.clone(),
            std::sync::Arc::clone(&audit),
            contract.clone(),
        );
        tokio::spawn(task);
    }
}

type ServerWs = WebSocketStream<TcpStream>;

/// Handshake callback enforcing the contract subprotocol (a named type works
/// around rustc's higher-ranked closure inference on `accept_hdr_async`).
struct SubprotocolGuard {
    subprotocol: String,
}

impl tokio_tungstenite::tungstenite::handshake::server::Callback for SubprotocolGuard {
    fn on_request(self, request: &Request, mut response: Response) -> Result<Response, ErrorResponse> {
        subprotocol_guard(request, &mut response, &self.subprotocol)?;
        Ok(response)
    }
}

fn subprotocol_guard(
    request: &Request,
    response: &mut Response,
    subprotocol: &str,
) -> Result<(), ErrorResponse> {
    let header = request
        .headers()
        .get("sec-websocket-protocol")
        .and_then(|value| value.to_str().ok())
        .unwrap_or_default();
    let offered = header.split(',').map(str::trim).any(|p| p == subprotocol);
    if !offered {
        let error = HttpResponse::builder()
            .status(StatusCode::BAD_REQUEST)
            .body(Some(format!("subprotocol `{subprotocol}` is required")))
            .expect("static error response builds");
        return Err(error);
    }
    if let Ok(value) = HeaderValue::from_str(subprotocol) {
        response.headers_mut().insert("sec-websocket-protocol", value);
    }
    Ok(())
}

async fn connection_task(
    stream: TcpStream,
    remote: std::net::SocketAddr,
    world_tx: Sender<WorldEvent>,
    disconnect_tx: DisconnectTx,
    audit: SharedAudit,
    contract: WireContract,
) {
    let subprotocol = contract.subprotocol.clone();
    let frame_limit = contract.max_frame_bytes;
    let config = WebSocketConfig::default()
        .max_message_size(Some(frame_limit))
        .max_frame_size(Some(frame_limit));
    let ws = match accept_hdr_async_with_config(
        stream,
        SubprotocolGuard {
            subprotocol: subprotocol.clone(),
        },
        Some(config),
    )
    .await
    {
        Ok(ws) => ws,
        // HTTP-level rejection (missing subprotocol) or a malformed opening
        // handshake: no session was opened, nothing to audit.
        Err(_) => return,
    };

    let session_id = next_session_id();
    {
        let mut log = audit.lock().expect("audit lock");
        log.session_open(&session_id, &remote.to_string());
    }

    let (ingress_tx, ingress_rx) = mpsc::channel(contract.limits.ingress_queue_per_session);
    let (egress_tx, egress_rx) = mpsc::channel(EGRESS_CAPACITY);
    let (sink, read_half) = ws.split();
    let writer = tokio::spawn(writer_task(sink, egress_rx));
    let reader = tokio::spawn(reader_task(
        read_half,
        ingress_tx,
        egress_tx.clone(),
        session_id.clone(),
        disconnect_tx,
        std::sync::Arc::clone(&audit),
        contract.limits.clone(),
    ));

    let opened = WorldEvent::Opened {
        session_id: session_id.clone(),
        remote: remote.to_string(),
        egress: egress_tx,
        ingress_rx,
        reader,
        writer,
    };
    if world_tx.send(opened).await.is_err() {
        // The world is gone (shutdown race); aborting is safe here because
        // the tasks own nothing but the socket and this connection's queues.
        return;
    }
}

async fn writer_task(
    mut sink: futures_util::stream::SplitSink<ServerWs, Message>,
    mut egress_rx: Receiver<Egress>,
) {
    loop {
        let Some(item) = egress_rx.recv().await else {
            let _ = sink.send(Message::Close(None)).await;
            let _ = sink.close().await;
            break;
        };
        match item {
            Egress::Text(text) => {
                if sink.send(Message::Text(text.into())).await.is_err() {
                    break;
                }
            }
            Egress::Close => {
                let _ = sink.send(Message::Close(None)).await;
                let _ = sink.close().await;
                break;
            }
        }
    }
}

async fn reader_task(
    mut stream: futures_util::stream::SplitStream<ServerWs>,
    ingress_tx: Sender<IngressEvent>,
    egress_tx: Sender<Egress>,
    session_id: String,
    disconnect_tx: DisconnectTx,
    audit: SharedAudit,
    limits: Limits,
) {
    let code: &'static str = loop {
        let message = match stream.next().await {
            Some(Ok(message)) => message,
            Some(Err(_)) => break "connection_error",
            None => break "client_closed",
        };
        match message {
            Message::Text(text) => {
                match parse_envelope(text.as_str(), &limits) {
                    Ok(event) => {
                        if let Some(code) = push_ingress(
                            &ingress_tx,
                            &egress_tx,
                            &audit,
                            &session_id,
                            event,
                        ) {
                            break code;
                        }
                    }
                    Err(error) => {
                        if matches!(
                            error.code,
                            ErrorCode::UnsupportedContract
                        ) || (error.code == ErrorCode::UnknownRole && error.fatal)
                        {
                            let mut log = audit.lock().expect("audit lock");
                            log.handshake_rejected(
                                &session_id,
                                error.code.as_str(),
                                &error.detail,
                            );
                        }
                        let _ = egress_tx.try_send(Egress::Text(error_message(
                            error.code,
                            &error.detail,
                            error.sender,
                            error.sequence,
                        )));
                        if error.fatal {
                            let _ = egress_tx.try_send(Egress::Close);
                            break error.code.as_str();
                        }
                    }
                }
            }
            Message::Binary(_) => {
                let _ = egress_tx.try_send(Egress::Text(error_message(
                    ErrorCode::BadEnvelope,
                    "binary frames are not part of the wire contract",
                    None,
                    None,
                )));
                let _ = egress_tx.try_send(Egress::Close);
                break ErrorCode::BadEnvelope.as_str();
            }
            Message::Close(_) => break "client_closed",
            // Ping/Pong are handled by tungstenite itself.
            _ => continue,
        }
    };
    let _ = disconnect_tx.try_send((session_id, code));
}

/// Push one validated event into the bounded session queue.
///
/// Returns `Some(code)` when the read loop must stop (`queue_full` or the
/// world being gone), `None` to continue reading.
fn push_ingress(
    ingress_tx: &Sender<IngressEvent>,
    egress_tx: &Sender<Egress>,
    audit: &SharedAudit,
    session_id: &str,
    event: IngressEvent,
) -> Option<&'static str> {
    let command_meta = match &event {
        IngressEvent::Command { sender, sequence, .. } => Some((*sender, *sequence)),
        _ => None,
    };
    match ingress_tx.try_send(event) {
        Ok(()) => None,
        Err(TrySendError::Full(_)) => {
            if let Some((role, sequence)) = command_meta {
                let mut log = audit.lock().expect("audit lock");
                log.ingress_rejected(session_id, role.as_str(), sequence, ErrorCode::QueueFull.as_str());
            }
            let _ = egress_tx.try_send(Egress::Text(error_message(
                ErrorCode::QueueFull,
                "ingress queue is full",
                command_meta.map(|(role, _)| role),
                command_meta.map(|(_, sequence)| sequence),
            )));
            let _ = egress_tx.try_send(Egress::Close);
            Some(ErrorCode::QueueFull.as_str())
        }
        Err(TrySendError::Closed(_)) => Some("world_closed"),
    }
}

/// Readiness JSON object (`{"port","pid","contractId"}`).
#[must_use]
pub fn readiness_json(port: u16, pid: u32, contract_id: &str) -> String {
    json!({ "port": port, "pid": pid, "contractId": contract_id }).to_string()
}

/// Write the ready file (same JSON object as the stdout line).
///
/// # Errors
///
/// Propagates filesystem errors from creating/writing the ready file.
pub fn write_ready_file(path: &Path, port: u16, pid: u32, contract_id: &str) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(path, readiness_json(port, pid, contract_id))
}

/// Session id allocator shared by the accept loop.
pub(crate) static SESSION_COUNTER: AtomicU64 = AtomicU64::new(1);

/// Allocate the next session id.
#[must_use]
pub(crate) fn next_session_id() -> String {
    format!("s-{}", SESSION_COUNTER.fetch_add(1, Ordering::Relaxed))
}

/// Build an Error wire message.
#[must_use]
pub(crate) fn error_message(
    code: ErrorCode,
    detail: &str,
    sender: Option<Role>,
    sequence: Option<u64>,
) -> String {
    let mut value = json!({
        "messageType": "Error",
        "code": code.as_str(),
        "detail": detail
    });
    if let Some(sender) = sender {
        value["sender"] = json!(sender.as_str());
    }
    if let Some(sequence) = sequence {
        value["sequence"] = json!(sequence);
    }
    value.to_string()
}

/// Build a HandshakeAck wire message.
#[must_use]
#[allow(dead_code)] // Red-phase stub: consumed by the world implementation.
pub(crate) fn handshake_ack(
    session_id: &str,
    role: Role,
    accepted: bool,
    reason: Option<&str>,
) -> String {
    let mut value = json!({
        "messageType": "HandshakeAck",
        "sessionId": session_id,
        "role": role.as_str(),
        "accepted": accepted,
        "contractId": crate::wire::EXPECTED_CONTRACT_ID
    });
    if let Some(reason) = reason {
        value["reason"] = json!(reason);
    }
    value.to_string()
}

/// Build a FullSnapshot wire message from a snapshot view.
#[must_use]
#[allow(dead_code)] // Red-phase stub: consumed by the world implementation.
pub(crate) fn full_snapshot(session_id: &str, view: &SnapshotView) -> String {
    json!({
        "messageType": "FullSnapshot",
        "sessionId": session_id,
        "tickId": view.tick_id,
        "revision": view.revision,
        "helloLog": view.hello_log
    })
    .to_string()
}

fn bad(code: ErrorCode, detail: &str, fatal: bool) -> EnvelopeError {
    EnvelopeError {
        code,
        detail: detail.to_owned(),
        fatal,
        sender: None,
        sequence: None,
    }
}

fn field_u64(object: &serde_json::Map<String, Value>, name: &str) -> Result<u64, EnvelopeError> {
    object
        .get(name)
        .and_then(Value::as_u64)
        .ok_or_else(|| bad(ErrorCode::BadEnvelope, &format!("field `{name}` must be an unsigned integer"), true))
}

fn field_string(object: &serde_json::Map<String, Value>, name: &str) -> Result<String, EnvelopeError> {
    object
        .get(name)
        .and_then(Value::as_str)
        .map(str::to_owned)
        .ok_or_else(|| bad(ErrorCode::BadEnvelope, &format!("field `{name}` must be a string"), true))
}

fn is_sha256_hex(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

fn payload_sha256(payload: &str) -> String {
    use sha2::{Digest, Sha256};
    let digest = Sha256::digest(payload.as_bytes());
    let mut out = String::with_capacity(64);
    for byte in digest {
        use std::fmt::Write as _;
        let _ = write!(out, "{byte:02x}");
    }
    out
}

/// Validate one client text frame against the wire contract.
///
/// Shape/const violations are `bad_envelope` (fatal); an unknown
/// `messageType` is `unknown_mapping` (fatal, contract fieldSemantics); a
/// payload hash mismatch is `bad_payload_hash` (recoverable); an InputCommand
/// from an unparseable sender is `unknown_role` (recoverable).
pub(crate) fn parse_envelope(text: &str, limits: &Limits) -> Result<IngressEvent, EnvelopeError> {
    let value: Value = serde_json::from_str(text)
        .map_err(|error| bad(ErrorCode::BadEnvelope, &format!("not valid JSON: {error}"), true))?;
    let Value::Object(object) = &value else {
        return Err(bad(ErrorCode::BadEnvelope, "envelope must be a JSON object", true));
    };
    let message_type = object
        .get("messageType")
        .and_then(Value::as_str)
        .ok_or_else(|| bad(ErrorCode::UnknownMapping, "missing messageType", true))?;

    match message_type {
        "Handshake" => parse_handshake(object),
        "BaselineAck" => Ok(IngressEvent::BaselineAck { revision: revision(object)? }),
        "InputCommand" => parse_command(object, text, limits),
        // The Shutdown shape exists for in-process test vocabulary only; a
        // real server never accepts it from a client.
        "Shutdown" => Err(bad(
            ErrorCode::UnknownMapping,
            "server does not accept client Shutdown",
            true,
        )),
        other => Err(bad(
            ErrorCode::UnknownMapping,
            &format!("unknown messageType `{other}`"),
            true,
        )),
    }
}

fn revision(object: &serde_json::Map<String, Value>) -> Result<u64, EnvelopeError> {
    field_u64(object, "revision")
}

fn parse_handshake(object: &serde_json::Map<String, Value>) -> Result<IngressEvent, EnvelopeError> {
    let role_text = field_string(object, "role")?;
    let Some(role) = Role::parse(&role_text) else {
        return Err(bad(
            ErrorCode::UnknownRole,
            &format!("unknown role `{role_text}`"),
            true,
        ));
    };
    let contract_id = field_string(object, "contractId")?;
    if contract_id != crate::wire::EXPECTED_CONTRACT_ID {
        return Err(bad(
            ErrorCode::UnsupportedContract,
            &format!("contractId `{contract_id}` is not supported"),
            true,
        ));
    }
    let client_name = field_string(object, "clientName")?;
    Ok(IngressEvent::Handshake { role, client_name })
}

fn parse_command(
    object: &serde_json::Map<String, Value>,
    text: &str,
    limits: &Limits,
) -> Result<IngressEvent, EnvelopeError> {
    let sender_text = field_string(object, "sender")?;
    let sender = Role::parse(&sender_text);
    let sequence = field_u64(object, "sequence")?;
    let kind = field_string(object, "kind")?;
    if kind != "hello" {
        return Err(bad(ErrorCode::BadEnvelope, &format!("kind `{kind}` is not supported"), true));
    }
    let payload = field_string(object, "payload")?;
    if payload.len() > limits.max_payload_bytes {
        return Err(bad(
            ErrorCode::BadEnvelope,
            &format!("payload exceeds maxPayloadBytes ({})", limits.max_payload_bytes),
            true,
        ));
    }
    let payload_sha = field_string(object, "payloadSha256")?;
    if !is_sha256_hex(&payload_sha) {
        return Err(bad(ErrorCode::BadEnvelope, "payloadSha256 must be lowercase hex sha256", true));
    }
    let _ = field_u64(object, "sentAtMs")?;
    let sender = sender.ok_or(EnvelopeError {
        code: ErrorCode::UnknownRole,
        detail: format!("unknown sender `{sender_text}`"),
        fatal: false,
        sender: None,
        sequence: Some(sequence),
    })?;
    if payload_sha256(&payload) != payload_sha {
        return Err(EnvelopeError {
            code: ErrorCode::BadPayloadHash,
            detail: "payloadSha256 does not match payload bytes".to_owned(),
            fatal: false,
            sender: Some(sender),
            sequence: Some(sequence),
        });
    }
    Ok(IngressEvent::Command {
        sender,
        sequence,
        payload_sha256: payload_sha,
        envelope: text.to_owned(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn limits() -> Limits {
        Limits {
            max_payload_bytes: 4096,
            max_sessions: 2,
            ingress_queue_per_session: 64,
            hello_log_capacity: 32,
            handshake_timeout_ms: 5000,
            baseline_timeout_ms: 5000,
            scenario_timeout_ms: 30_000,
        }
    }

    const HELLO_SHA: &str = "a591a6d40bf420404a011733cfb7b190d62c65bf0bcda32b57b277d9ad9f146e";

    fn handshake_body() -> Value {
        json!({
            "messageType": "Handshake",
            "role": "browser",
            "clientName": "browser-client",
            "contractId": crate::wire::EXPECTED_CONTRACT_ID
        })
    }

    fn command_body() -> Value {
        json!({
            "messageType": "InputCommand",
            "sender": "browser",
            "sequence": 1,
            "kind": "hello",
            "payload": "Hello World",
            "payloadSha256": HELLO_SHA,
            "sentAtMs": 123_456
        })
    }

    #[test]
    fn readiness_line_shape() {
        let json = readiness_json(5123, 77, "lumio.hello-wire.v1");
        assert_eq!(
            json,
            r#"{"contractId":"lumio.hello-wire.v1","pid":77,"port":5123}"#
        );
        assert!(json.contains("\"port\":5123"));
        assert!(format!("{SERVER_READY_PREFIX}{json}").starts_with("SERVER_READY "));
    }

    #[test]
    fn ready_file_is_written() {
        let dir = tempfile::tempdir().expect("tempdir");
        let path = dir.path().join("nested").join("ready.json");
        write_ready_file(&path, 9, 1, "lumio.hello-wire.v1").expect("write");
        let body = std::fs::read_to_string(path).expect("read");
        assert!(body.contains("\"port\":9"));
        assert!(body.contains("\"contractId\":\"lumio.hello-wire.v1\""));
    }

    #[test]
    fn parses_handshake_baseline_and_command() {
        let event = parse_envelope(&handshake_body().to_string(), &limits()).expect("handshake");
        let IngressEvent::Handshake { role, client_name } = event else {
            panic!("expected handshake");
        };
        assert_eq!(role, Role::Browser);
        assert_eq!(client_name, "browser-client");

        let event = parse_envelope(
            &json!({"messageType": "BaselineAck", "revision": 4}).to_string(),
            &limits(),
        )
        .expect("baseline");
        assert_eq!(event, IngressEvent::BaselineAck { revision: 4 });

        let event = parse_envelope(&command_body().to_string(), &limits()).expect("command");
        let IngressEvent::Command { sender, sequence, payload_sha256, envelope } = event else {
            panic!("expected command");
        };
        assert_eq!(sender, Role::Browser);
        assert_eq!(sequence, 1);
        assert_eq!(payload_sha256, HELLO_SHA);
        assert!(envelope.contains("Hello World"));
    }

    #[test]
    fn non_json_and_non_object_are_fatal_bad_envelope() {
        for text in ["not json", "42", "[1,2]"] {
            let error = parse_envelope(text, &limits()).expect_err("must fail");
            assert_eq!(error.code, ErrorCode::BadEnvelope);
            assert!(error.fatal);
        }
    }

    #[test]
    fn unknown_or_missing_message_type_is_unknown_mapping() {
        for text in [
            json!({"messageType": "Wat", "role": "browser"}).to_string(),
            json!({"role": "browser"}).to_string(),
            json!({"messageType": "Shutdown", "reason": "x"}).to_string(),
        ] {
            let error = parse_envelope(&text, &limits()).expect_err("must fail");
            assert_eq!(error.code, ErrorCode::UnknownMapping, "text: {text}");
            assert!(error.fatal);
        }
    }

    #[test]
    fn handshake_rejections() {
        let wrong_contract = json!({
            "messageType": "Handshake", "role": "browser",
            "clientName": "c", "contractId": "lumio.other.v1"
        });
        let error = parse_envelope(&wrong_contract.to_string(), &limits()).expect_err("contract");
        assert_eq!(error.code, ErrorCode::UnsupportedContract);

        let wrong_role_handshake = json!({
            "messageType": "Handshake", "role": "admin",
            "clientName": "c", "contractId": crate::wire::EXPECTED_CONTRACT_ID
        });
        let error = parse_envelope(&wrong_role_handshake.to_string(), &limits()).expect_err("role");
        assert_eq!(error.code, ErrorCode::UnknownRole);

        let missing = json!({
            "messageType": "Handshake",
            "role": "browser",
            "contractId": crate::wire::EXPECTED_CONTRACT_ID
        });
        let error = parse_envelope(&missing.to_string(), &limits()).expect_err("clientName");
        assert_eq!(error.code, ErrorCode::BadEnvelope);
    }

    #[test]
    fn command_shape_violations_are_fatal() {
        let mut body = command_body();
        body["kind"] = json!("move");
        assert_eq!(
            parse_envelope(&body.to_string(), &limits()).unwrap_err().code,
            ErrorCode::BadEnvelope
        );

        let mut body = command_body();
        body["payloadSha256"] = json!("nothex");
        assert_eq!(
            parse_envelope(&body.to_string(), &limits()).unwrap_err().code,
            ErrorCode::BadEnvelope
        );

        let mut body = command_body();
        body["sequence"] = json!(-1);
        assert_eq!(
            parse_envelope(&body.to_string(), &limits()).unwrap_err().code,
            ErrorCode::BadEnvelope
        );

        let mut body = command_body();
        body["payload"] = json!("x".repeat(4097));
        assert_eq!(
            parse_envelope(&body.to_string(), &limits()).unwrap_err().code,
            ErrorCode::BadEnvelope
        );
    }

    #[test]
    fn payload_hash_mismatch_is_recoverable() {
        let mut body = command_body();
        body["payloadSha256"] = json!("b".repeat(64));
        let error = parse_envelope(&body.to_string(), &limits()).expect_err("hash");
        assert_eq!(error.code, ErrorCode::BadPayloadHash);
        assert!(!error.fatal);
        assert_eq!(error.sender, Some(Role::Browser));
        assert_eq!(error.sequence, Some(1));
    }

    #[test]
    fn unknown_command_sender_is_recoverable_unknown_role() {
        let mut body = command_body();
        body["sender"] = json!("admin");
        let error = parse_envelope(&body.to_string(), &limits()).expect_err("sender");
        assert_eq!(error.code, ErrorCode::UnknownRole);
        assert!(!error.fatal);
        assert_eq!(error.sequence, Some(1));
    }

    #[test]
    fn wire_builders() {
        let error = error_message(ErrorCode::QueueFull, "full", Some(Role::Bot), Some(3));
        let value: Value = serde_json::from_str(&error).expect("json");
        assert_eq!(value["messageType"], "Error");
        assert_eq!(value["code"], "queue_full");
        assert_eq!(value["sender"], "bot");
        assert_eq!(value["sequence"], 3);

        let ack = handshake_ack("s-1", Role::Browser, false, Some("role_taken: taken"));
        let value: Value = serde_json::from_str(&ack).expect("json");
        assert_eq!(value["messageType"], "HandshakeAck");
        assert_eq!(value["accepted"], false);
        assert_eq!(value["reason"], "role_taken: taken");

        let snapshot = full_snapshot(
            "s-1",
            &SnapshotView { tick_id: 2, revision: 3, hello_log: vec![json!({"sender": "bot"})] },
        );
        let value: Value = serde_json::from_str(&snapshot).expect("json");
        assert_eq!(value["messageType"], "FullSnapshot");
        assert_eq!(value["revision"], 3);
        assert_eq!(value["helloLog"].as_array().map(Vec::len), Some(1));
    }
}
