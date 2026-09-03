//! Consume-only Room host: session table + Runtime forward + NativeCore timers + wire.

use std::collections::HashMap;
use std::thread;
use std::thread::ThreadId;
use std::time::Duration;

use lumio_host_runtime::{
    bounded_channel, spawn_supervised, HostClock, KernelHandle, KernelTimer, RecvError, Sender,
    SharedClock, SupervisedTask, TimerMode,
};
use serde_json::{json, Map, Value};

use super::admission::{is_bot_namespace, verify_admission, AdmissionPayload};
use super::envelope::{connection_superseded_json, normalize_net_entity_id, InputCommand};
use super::log::NdjsonLog;
use super::runtime::BoundEntityKind;
use super::runtime::{
    AttributeQueryScope, ChatOpKind, ChatOperation, PersistRecord, QueryResult, RebindMode,
    RuntimeAdmit, RuntimeBinding, RuntimeQuery, RuntimeSurface, RuntimeTick,
};
use super::wire::{RoomListener, WireEvent, WireSender};
use super::{MAX_CHAT_INPUTS_PER_TICK, OWNER_PUMP_INTERVAL_MS};

/// WallClock expire dispatch id (NativeCore slot).
pub const DISPATCH_EXPIRE: u32 = 1;
/// TickFrame room tick dispatch id (NativeCore slot).
pub const DISPATCH_TICK: u32 = 2;

fn session_id_for(login_name: &str, reconnected: bool) -> String {
    if reconnected {
        format!("sess-{login_name}-re")
    } else {
        format!("sess-{login_name}")
    }
}

/// Binding five-tuple plus the host session id (not a Runtime binding field).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConnectionBinding {
    pub account_id: String,
    pub room_id: String,
    pub net_entity_id: String,
    pub session_id: String,
    pub entity_type: BoundEntityKind,
    pub connection_generation: u64,
}

impl ConnectionBinding {
    fn from_runtime(binding: RuntimeBinding, session_id: String) -> Self {
        Self {
            account_id: binding.account_id,
            room_id: binding.room_id,
            net_entity_id: binding.net_entity_id,
            session_id,
            entity_type: binding.entity_type,
            connection_generation: binding.connection_generation,
        }
    }
}

/// One live admit row for host-audit / census.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AdmitTrace {
    pub connection_id: String,
    pub session_id: String,
    pub net_entity_id: String,
    pub entity_type: BoundEntityKind,
    pub account_id: String,
    pub login_name: String,
}

/// Server resolution of a live entity.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EntityResolution {
    pub net_entity_id: String,
    pub room_id: String,
    pub entity_type: BoundEntityKind,
    pub account_id: String,
}

/// Room admission outcome.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RoomAdmitResult {
    pub accepted: bool,
    pub error_code: Option<String>,
    pub binding: Option<ConnectionBinding>,
    pub reconnected: bool,
    pub takeover: bool,
}

impl RoomAdmitResult {
    fn ok(binding: ConnectionBinding, reconnected: bool, takeover: bool) -> Self {
        Self {
            accepted: true,
            error_code: None,
            binding: Some(binding),
            reconnected,
            takeover,
        }
    }

    fn reject(code: &str) -> Self {
        Self {
            accepted: false,
            error_code: Some(code.to_owned()),
            binding: None,
            reconnected: false,
            takeover: false,
        }
    }
}

/// Live Bot + Player census for one room.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RoomCensus {
    pub bot_count: usize,
    pub player_count: usize,
    pub total: usize,
    pub net_entity_ids: Vec<String>,
    pub entity_types: Vec<BoundEntityKind>,
}

/// Attribute query request forwarded to Runtime.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AttributeQueryRequest {
    pub caller_scope: AttributeQueryScope,
    pub room_id: String,
    pub net_entity_id: String,
    pub attribute_id: String,
    pub connection_generation: Option<u64>,
}

struct Session {
    connection_id: String,
    session_id: String,
    account_id: String,
    login_name: String,
    room_id: String,
    net_entity_id: String,
    entity_type: BoundEntityKind,
    generation: u64,
    egresses: Vec<WireSender>,
}

struct Inner {
    clock: SharedClock,
    reconnect_window_ms: u64,
    admission_key_id: u8,
    admission_public: Vec<u8>,
    unix_seconds: u64,
    runtime: Box<dyn RuntimeSurface>,
    kernel: Box<dyn KernelTimer>,
    sessions: HashMap<String, Session>,
    expire_watch: HashMap<KernelHandle, String>,
    pending_egress: HashMap<String, Vec<WireSender>>,
    kernel_frame: u64,
    wire_chat_pending: u64,
    log: NdjsonLog,
    last_applied_tick: u64,
}

enum OwnerWork {
    Run(Box<dyn FnOnce(&mut Inner) + Send>),
    Wire(WireEvent),
}

/// Slice-scoped Room host. All authoritative work runs on one owner thread.
pub struct EntityChatHost {
    tx: Sender<OwnerWork>,
    _listener: RoomListener,
    _forward: SupervisedTask,
    _owner: SupervisedTask,
    owner_id: ThreadId,
    listen_uri: String,
    clock: SharedClock,
    log: NdjsonLog,
}

impl EntityChatHost {
    /// Builds a consume-only host. Kernel due-decision stays in NativeCore ABI.
    #[must_use]
    pub fn new(
        reconnect_window_ms: u64,
        clock: SharedClock,
        runtime: Box<dyn RuntimeSurface>,
        kernel: Box<dyn KernelTimer>,
        admission_key_id: u8,
        admission_public: Vec<u8>,
        unix_seconds: u64,
    ) -> Self {
        let (tx, rx) = bounded_channel(256);
        let (wire_tx, wire_rx) = bounded_channel(256);
        let (id_tx, id_rx) = bounded_channel(1);
        let listener = RoomListener::bind(wire_tx).expect("room wire bind");
        let listen_uri = listener.uri();
        let forward_tx = tx.clone();
        let forward = spawn_supervised("lumio-entity-chat-wire-fwd", move |_| {
            while let Ok(event) = wire_rx.recv() {
                if forward_tx.send(OwnerWork::Wire(event)).is_err() {
                    break;
                }
            }
        });
        let owner_clock = clock.clone();
        let owner_log = NdjsonLog::buffer();
        let host_log = owner_log.clone();
        let owner = spawn_supervised("lumio-entity-chat-owner", move |_cancel| {
            let _ = id_tx.send(thread::current().id());
            let mut inner = Inner {
                clock: owner_clock,
                reconnect_window_ms,
                admission_key_id,
                admission_public,
                unix_seconds,
                runtime,
                kernel,
                sessions: HashMap::new(),
                expire_watch: HashMap::new(),
                pending_egress: HashMap::new(),
                kernel_frame: 0,
                wire_chat_pending: 0,
                log: owner_log,
                last_applied_tick: 0,
            };
            if inner
                .kernel
                .schedule_repeating(TimerMode::TickFrame, 1, 1, DISPATCH_TICK)
                .is_err()
            {
                return;
            }
            loop {
                inner.drive_wall();
                match rx.recv_timeout(Duration::from_millis(OWNER_PUMP_INTERVAL_MS)) {
                    Ok(OwnerWork::Run(work)) => work(&mut inner),
                    Ok(OwnerWork::Wire(event)) => inner.on_wire(event),
                    Err(RecvError::Empty) => {}
                    Err(RecvError::Closed) => break,
                }
            }
        });
        let owner_id = id_rx.recv().expect("owner thread id");
        Self {
            tx,
            _listener: listener,
            _forward: forward,
            _owner: owner,
            owner_id,
            listen_uri,
            clock,
            log: host_log,
        }
    }

    fn on_owner<T, F>(&self, work: F) -> T
    where
        T: Send + 'static,
        F: FnOnce(&mut Inner) -> T + Send + 'static,
    {
        let (tx, rx) = bounded_channel(1);
        self.tx
            .send(OwnerWork::Run(Box::new(move |inner| {
                let _ = tx.send(work(inner));
            })))
            .unwrap_or_else(|_| panic!("entity-chat owner thread closed"));
        rx.recv().expect("entity-chat owner result")
    }

    /// Loopback Room wire URI.
    #[must_use]
    pub fn listen_uri(&self) -> String {
        self.listen_uri.clone()
    }

    /// Game Server never accepts username/password in place of admission.
    #[must_use]
    pub fn try_admit_username_password(
        &self,
        _room_id: &str,
        _connection_id: &str,
        _login_name: &str,
        _password: &str,
    ) -> bool {
        false
    }

    /// Admits a connection by verifying the Account Server credential.
    #[must_use]
    pub fn admit(
        &self,
        room_id: String,
        connection_id: String,
        credential: String,
    ) -> RoomAdmitResult {
        self.on_owner(move |inner| inner.admit(&room_id, &connection_id, &credential))
    }

    /// Admits an already-verified payload (suite path after local verify).
    #[must_use]
    pub fn admit_verified(
        &self,
        room_id: String,
        connection_id: String,
        payload: AdmissionPayload,
    ) -> RoomAdmitResult {
        self.on_owner(move |inner| inner.admit_verified(&room_id, &connection_id, &payload))
    }

    /// Disconnects a live connection and schedules the NativeCore wallClock expire.
    #[must_use]
    pub fn disconnect(&self, connection_id: String) -> bool {
        self.on_owner(move |inner| inner.disconnect(&connection_id))
    }

    /// Pumps NativeCore wallClock at the host monotonic reading.
    pub fn drive_kernel(&self) {
        self.on_owner(Inner::drive_wall);
    }

    /// Test/suite clock handle. Expiry still fires only via kernel pump.
    #[must_use]
    pub fn clock(&self) -> SharedClock {
        self.clock.clone()
    }

    /// Structured JSON lines emitted by this host (tests / oracle).
    #[must_use]
    pub fn log_lines(&self) -> Vec<String> {
        self.log.lines()
    }

    /// Decodes a frozen InputCommand (chat.input) envelope, then queues ChatInput.
    #[must_use]
    pub fn admit_chat_input(&self, connection_id: String, envelope: InputCommand) -> ChatOperation {
        self.on_owner(move |inner| inner.admit_chat_input(&connection_id, &envelope))
    }

    /// Advances kernel tickFrame and broadcasts Runtime BuildDelta bytes.
    #[must_use]
    pub fn run_tick(&self, room_id: String) -> RuntimeTick {
        self.on_owner(move |inner| inner.run_tick(&room_id))
    }

    /// Tick cadence is kernel tickFrame, not a caller for-loop.
    #[must_use]
    pub fn schedule_room_tick(&self, room_id: String, _delay_ms: u64) -> RuntimeTick {
        self.run_tick(room_id)
    }

    /// Client self-lookup via Runtime, joined with the host session id.
    #[must_use]
    pub fn try_self_lookup(&self, connection_id: String) -> Option<ConnectionBinding> {
        self.on_owner(move |inner| inner.try_self_lookup(&connection_id))
    }

    /// Binding or panic if missing.
    ///
    /// # Panics
    ///
    /// Panics when the connection is not bound.
    #[must_use]
    pub fn must_self(&self, connection_id: &str) -> ConnectionBinding {
        self.try_self_lookup(connection_id.to_owned())
            .unwrap_or_else(|| panic!("connection is not bound: {connection_id}"))
    }

    /// Resolve a NetEntityId in a room via Runtime.
    #[must_use]
    pub fn try_resolve_by_net_entity_id(
        &self,
        room_id: String,
        net_entity_id: String,
    ) -> Option<EntityResolution> {
        let net_entity_id = normalize_net_entity_id(&net_entity_id);
        self.on_owner(move |inner| inner.try_resolve_by_net_entity_id(&room_id, &net_entity_id))
    }

    /// Chat.input frames admitted from Room WS and not yet applied by a tick.
    #[must_use]
    pub fn pending_wire_chat_inputs(&self) -> usize {
        self.on_owner(move |inner| usize::try_from(inner.wire_chat_pending).unwrap_or(usize::MAX))
    }

    /// Count live Room WS observers for a connection (harness wait).
    #[must_use]
    pub fn wire_observer_count(&self, connection_id: String) -> usize {
        self.on_owner(move |inner| {
            inner
                .sessions
                .get(&connection_id)
                .map(|session| session.egresses.len())
                .unwrap_or(0)
                + inner
                    .pending_egress
                    .get(&connection_id)
                    .map(Vec::len)
                    .unwrap_or(0)
        })
    }

    /// C-2 attribute query forwarded to Runtime.
    #[must_use]
    pub fn query_attribute(&self, request: AttributeQueryRequest) -> QueryResult {
        self.on_owner(move |inner| inner.query_attribute(&request))
    }

    /// Runtime persist bytes. Restore must not create Active bindings.
    #[must_use]
    pub fn capture_persist_snapshot(&self, room_id: String) -> PersistRecord {
        self.on_owner(move |inner| inner.runtime.persist(&room_id))
    }

    /// Restores persist-only fields. Does not Admit or create sessions.
    pub fn restore_persist_snapshot(&self, room_id: String, snapshot: PersistRecord) {
        self.on_owner(move |inner| {
            let _ = inner.runtime.restore(&room_id, &snapshot.bytes);
        });
    }

    /// Live entity census from Runtime ListBindings.
    #[must_use]
    pub fn census(&self, room_id: String) -> RoomCensus {
        self.on_owner(move |inner| inner.census(&room_id))
    }

    /// Live admit rows for host-audit census.
    #[must_use]
    pub fn list_admits(&self, room_id: String) -> Vec<AdmitTrace> {
        self.on_owner(move |inner| inner.list_admits(&room_id))
    }

    /// Owner thread id (tests).
    #[must_use]
    pub fn owner_thread_id(&self) -> ThreadId {
        self.owner_id
    }
}

impl Inner {
    fn admit(&mut self, room_id: &str, connection_id: &str, credential: &str) -> RoomAdmitResult {
        match verify_admission(
            credential,
            self.admission_key_id,
            &self.admission_public,
            self.unix_seconds,
        ) {
            Ok(payload) => self.admit_verified(room_id, connection_id, &payload),
            Err(code) => RoomAdmitResult::reject(&code),
        }
    }

    fn admit_verified(
        &mut self,
        room_id: &str,
        connection_id: &str,
        payload: &AdmissionPayload,
    ) -> RoomAdmitResult {
        if room_id.is_empty()
            || connection_id.is_empty()
            || payload.account_id.is_empty()
            || payload.login_name.is_empty()
        {
            return RoomAdmitResult::reject("invalid_request");
        }
        if is_bot_namespace(&payload.login_name) && !payload.bot_tool_context {
            return RoomAdmitResult::reject("bot_namespace_admission_forbidden");
        }
        if self.sessions.contains_key(connection_id) {
            return RoomAdmitResult::reject("invalid_request");
        }
        let kind = super::runtime::entity_type_of(&payload.login_name, payload.bot_tool_context);
        let admitted = self
            .runtime
            .admit(connection_id, &payload.account_id, room_id, kind);
        if admitted.code.as_deref() == Some("account_already_online") {
            return self.takeover(room_id, connection_id, payload, admitted);
        }
        if admitted.accepted {
            return self.commit_session(connection_id, payload, admitted, false, false);
        }
        if admitted.code.as_deref() == Some("cross_room_reference") {
            return RoomAdmitResult::reject("invalid_request");
        }
        let rebound = self.runtime.rebind(
            connection_id,
            &payload.account_id,
            room_id,
            RebindMode::Reconnect,
        );
        if rebound.accepted {
            self.cancel_expire_for(
                rebound
                    .binding
                    .as_ref()
                    .map(|row| row.net_entity_id.as_str()),
            );
            return self.commit_session(connection_id, payload, rebound, true, false);
        }
        RoomAdmitResult::reject(admitted.code.as_deref().unwrap_or("invalid_request"))
    }

    fn takeover(
        &mut self,
        room_id: &str,
        connection_id: &str,
        payload: &AdmissionPayload,
        already: RuntimeAdmit,
    ) -> RoomAdmitResult {
        let Some(existing) = already.binding.clone() else {
            self.log_event(
                "admit",
                json_map(&[
                    ("reason", json!("account_already_online_missing_binding")),
                    ("accepted", json!(false)),
                ]),
            );
            return RoomAdmitResult::reject("account_already_online");
        };
        if existing.room_id != room_id {
            return RoomAdmitResult::reject("invalid_request");
        }
        let old_id = self
            .sessions
            .iter()
            .find(|(_, session)| session.net_entity_id == existing.net_entity_id)
            .map(|(id, _)| id.clone());
        let rebound = self.runtime.rebind(
            connection_id,
            &payload.account_id,
            room_id,
            RebindMode::Takeover,
        );
        if !rebound.accepted {
            self.log_event(
                "admit",
                json_map(&[
                    ("reason", json!("rebind_failed")),
                    ("accepted", json!(false)),
                    ("netEntityId", json!(existing.net_entity_id)),
                ]),
            );
            return RoomAdmitResult::reject(rebound.code.as_deref().unwrap_or("invalid_request"));
        }
        let Some(binding) = rebound.binding.clone() else {
            return RoomAdmitResult::reject("invalid_request");
        };
        let new_generation = binding.connection_generation;
        if let Some(old_id) = old_id {
            if let Some(old) = self.sessions.remove(&old_id) {
                let frame = connection_superseded_json(&binding.net_entity_id, new_generation);
                for egress in &old.egresses {
                    let _ = egress.try_send_text(frame.clone());
                    let _ = egress.close();
                }
                self.log_event(
                    "superseded",
                    json_map(&[
                        ("netEntityId", json!(binding.net_entity_id.clone())),
                        ("reason", json!("account_already_online")),
                    ]),
                );
            }
        }
        self.log_event(
            "rebind",
            json_map(&[
                ("netEntityId", json!(binding.net_entity_id.clone())),
                ("previousNetEntityId", json!(existing.net_entity_id)),
            ]),
        );
        self.commit_session(connection_id, payload, rebound, false, true)
    }

    fn commit_session(
        &mut self,
        connection_id: &str,
        payload: &AdmissionPayload,
        admitted: RuntimeAdmit,
        reconnected: bool,
        takeover: bool,
    ) -> RoomAdmitResult {
        let Some(runtime_binding) = admitted.binding else {
            return RoomAdmitResult::reject("invalid_request");
        };
        if self
            .runtime
            .attach_member(&runtime_binding.room_id, connection_id)
            .is_err()
        {
            return RoomAdmitResult::reject("runtime_failure");
        }
        let session_id = session_id_for(&payload.login_name, reconnected || takeover);
        let egresses = self
            .pending_egress
            .remove(connection_id)
            .unwrap_or_default();
        let session = Session {
            connection_id: connection_id.to_owned(),
            session_id: session_id.clone(),
            account_id: payload.account_id.clone(),
            login_name: payload.login_name.clone(),
            room_id: runtime_binding.room_id.clone(),
            net_entity_id: runtime_binding.net_entity_id.clone(),
            entity_type: runtime_binding.entity_type,
            generation: runtime_binding.connection_generation,
            egresses,
        };
        let binding = ConnectionBinding::from_runtime(runtime_binding, session_id);
        let snapshot =
            self.runtime
                .build_full_snapshot(&binding.room_id, self.last_applied_tick, 0);
        if snapshot.is_empty() {
            let _ = self.runtime.disconnect(connection_id);
            self.log_event(
                "admit",
                json_map(&[
                    ("reason", json!("runtime_failure")),
                    ("accepted", json!(false)),
                    ("netEntityId", json!(binding.net_entity_id)),
                ]),
            );
            return RoomAdmitResult::reject("runtime_failure");
        }
        self.sessions.insert(connection_id.to_owned(), session);
        self.send_snapshot_bytes(connection_id, &snapshot);
        self.log_event(
            "admit",
            json_map(&[
                ("accepted", json!(true)),
                ("netEntityId", json!(binding.net_entity_id.clone())),
                ("entityType", json!(binding.entity_type.as_str())),
            ]),
        );
        RoomAdmitResult::ok(binding, reconnected, takeover)
    }

    fn disconnect(&mut self, connection_id: &str) -> bool {
        let Some(session) = self.sessions.remove(connection_id) else {
            return false;
        };
        for egress in &session.egresses {
            let _ = egress.close();
        }
        let _ = self.runtime.disconnect(connection_id);
        let due = self.clock.now_ms().saturating_add(self.reconnect_window_ms);
        if let Ok(handle) =
            self.kernel
                .schedule_one_shot(TimerMode::WallClock, due, DISPATCH_EXPIRE)
        {
            self.expire_watch
                .insert(handle, session.net_entity_id.clone());
        }
        true
    }

    fn cancel_expire_for(&mut self, net_entity_id: Option<&str>) {
        let Some(net_entity_id) = net_entity_id else {
            return;
        };
        let handles: Vec<KernelHandle> = self
            .expire_watch
            .iter()
            .filter(|(_, id)| *id == net_entity_id)
            .map(|(handle, _)| *handle)
            .collect();
        for handle in handles {
            let _ = self.kernel.cancel(handle);
            self.expire_watch.remove(&handle);
        }
    }

    fn drive_wall(&mut self) {
        let now = self.clock.now_ms();
        let Ok(fired) = self.kernel.pump_wall_clock(now) else {
            return;
        };
        for event in fired {
            if event.dispatch_id != DISPATCH_EXPIRE {
                continue;
            }
            if let Some(net_entity_id) = self.expire_watch.remove(&event.handle) {
                let _ = self.runtime.expire(&net_entity_id);
                self.log_event(
                    "expire",
                    json_map(&[
                        ("netEntityId", json!(net_entity_id)),
                        ("reason", json!("kernel")),
                        ("source", json!("native-kernel/wallClock")),
                    ]),
                );
            }
        }
    }

    fn admit_chat_input(&mut self, connection_id: &str, envelope: &InputCommand) -> ChatOperation {
        let Some(session) = self.sessions.get(connection_id) else {
            return ChatOperation::rejected("disconnected");
        };
        let room_id = session.room_id.clone();
        let generation = session.generation;
        self.runtime
            .admit_input_command(&room_id, connection_id, generation, &envelope.to_json())
    }

    fn run_tick(&mut self, room_id: &str) -> RuntimeTick {
        if self.wire_chat_pending > MAX_CHAT_INPUTS_PER_TICK as u64 {
            return RuntimeTick::failed("runtime_failure");
        }
        self.kernel_frame = self.kernel_frame.saturating_add(1);
        let Ok(fired) = self.kernel.advance_tick_frame(self.kernel_frame) else {
            return RuntimeTick::failed("runtime_failure");
        };
        if !fired.iter().any(|row| row.dispatch_id == DISPATCH_TICK) {
            return RuntimeTick::failed("runtime_failure");
        }
        let tick = self.runtime.run_tick(room_id, 0);
        if !tick.ok {
            return tick;
        }
        self.last_applied_tick = tick.applied_tick;
        self.wire_chat_pending = 0;
        let frames = self
            .runtime
            .build_delta(room_id, tick.applied_tick, tick.revision);
        self.broadcast(room_id, &frames);
        self.log_event(
            "tick",
            json_map(&[
                ("appliedTick", json!(tick.applied_tick)),
                ("tickSource", json!("native-kernel/tickFrame")),
            ]),
        );
        tick
    }

    fn broadcast(&mut self, room_id: &str, frames: &[Vec<u8>]) {
        let texts: Option<Vec<String>> = frames.iter().map(|frame| utf8_frame(frame)).collect();
        let Some(texts) = texts else {
            self.log_event("event", json_map(&[("reason", json!("invalid_utf8"))]));
            return;
        };
        let mut drop_ids = Vec::new();
        for (connection_id, session) in &mut self.sessions {
            if session.room_id != room_id {
                continue;
            }
            let mut backpressure = false;
            session.egresses.retain(|egress| {
                for text in &texts {
                    match egress.try_send_text(text.clone()) {
                        Ok(()) => {}
                        Err(true) => {
                            backpressure = true;
                            let _ = egress.close();
                            return false;
                        }
                        Err(false) => return false,
                    }
                }
                true
            });
            if backpressure {
                drop_ids.push(connection_id.clone());
            }
        }
        for connection_id in drop_ids {
            self.log_event(
                "event",
                json_map(&[
                    ("reason", json!("backpressure")),
                    ("connectionId", json!(connection_id)),
                ]),
            );
            let _ = self.disconnect(&connection_id);
        }
    }

    fn send_snapshot_bytes(&mut self, connection_id: &str, bytes: &[u8]) {
        let Some(text) = utf8_frame(bytes) else {
            self.log_event("snapshot", json_map(&[("reason", json!("invalid_utf8"))]));
            return;
        };
        let Some(session) = self.sessions.get(connection_id) else {
            return;
        };
        for egress in &session.egresses {
            let _ = egress.try_send_text(text.clone());
        }
        self.log_event(
            "snapshot",
            json_map(&[
                ("netEntityId", json!(session.net_entity_id.clone())),
                ("appliedTick", json!(self.last_applied_tick)),
            ]),
        );
    }

    fn send_full_snapshot_to(&mut self, connection_id: &str, egress: &WireSender) {
        let Some(session) = self.sessions.get(connection_id) else {
            return;
        };
        let room_id = session.room_id.clone();
        let bytes = self
            .runtime
            .build_full_snapshot(&room_id, self.last_applied_tick, 0);
        if bytes.is_empty() {
            return;
        }
        if let Some(text) = utf8_frame(&bytes) {
            let _ = egress.try_send_text(text);
        }
    }

    fn on_wire(&mut self, event: WireEvent) {
        match event {
            WireEvent::Attached {
                connection_id,
                egress,
            } => {
                if self.sessions.contains_key(&connection_id) {
                    if let Some(session) = self.sessions.get_mut(&connection_id) {
                        session.egresses.push(egress.clone());
                    }
                    self.send_full_snapshot_to(&connection_id, &egress);
                } else {
                    self.pending_egress
                        .entry(connection_id)
                        .or_default()
                        .push(egress);
                }
            }
            WireEvent::Input {
                connection_id,
                text,
            } => {
                if let Ok(envelope) = parse_input_command_json(&text) {
                    let admitted = self.admit_chat_input(&connection_id, &envelope);
                    if admitted.kind == ChatOpKind::Admitted {
                        self.wire_chat_pending = self.wire_chat_pending.saturating_add(1);
                    }
                }
            }
            WireEvent::Closed { .. } => {
                // One socket close must not drop other c-browser observers (Playwright + harness).
            }
            WireEvent::WriteFailed { connection_id } => {
                self.log_event(
                    "event",
                    json_map(&[
                        ("reason", json!("write_failed")),
                        ("connectionId", json!(connection_id)),
                    ]),
                );
            }
        }
    }

    fn try_self_lookup(&mut self, connection_id: &str) -> Option<ConnectionBinding> {
        let runtime = self.runtime.self_lookup(connection_id)?;
        let session = self.sessions.get(connection_id)?;
        Some(ConnectionBinding::from_runtime(
            runtime,
            session.session_id.clone(),
        ))
    }

    fn try_resolve_by_net_entity_id(
        &mut self,
        room_id: &str,
        net_entity_id: &str,
    ) -> Option<EntityResolution> {
        let id = normalize_net_entity_id(net_entity_id);
        let runtime = self.runtime.resolve_by_net_entity_id(room_id, &id)?;
        Some(EntityResolution {
            net_entity_id: runtime.net_entity_id,
            room_id: runtime.room_id,
            entity_type: runtime.entity_type,
            account_id: runtime.account_id,
        })
    }

    fn query_attribute(&mut self, request: &AttributeQueryRequest) -> QueryResult {
        self.runtime.query_attribute(&RuntimeQuery {
            caller_scope: request.caller_scope,
            room_id: request.room_id.clone(),
            net_entity_id: normalize_net_entity_id(&request.net_entity_id),
            attribute_id: request.attribute_id.clone(),
            connection_generation: request.connection_generation,
        })
    }

    fn census(&mut self, room_id: &str) -> RoomCensus {
        let mut rows = self.runtime.list_bindings(room_id);
        rows.sort_by(|left, right| left.net_entity_id.cmp(&right.net_entity_id));
        let mut bots = 0;
        let mut players = 0;
        let mut ids = Vec::new();
        let mut kinds = Vec::new();
        for row in rows {
            ids.push(row.net_entity_id);
            kinds.push(row.entity_type);
            match row.entity_type {
                BoundEntityKind::Bot => bots += 1,
                BoundEntityKind::Player => players += 1,
            }
        }
        RoomCensus {
            bot_count: bots,
            player_count: players,
            total: bots + players,
            net_entity_ids: ids,
            entity_types: kinds,
        }
    }

    fn list_admits(&mut self, room_id: &str) -> Vec<AdmitTrace> {
        let mut rows: Vec<AdmitTrace> = self
            .sessions
            .values()
            .filter(|session| session.room_id == room_id)
            .map(|session| AdmitTrace {
                connection_id: session.connection_id.clone(),
                session_id: session.session_id.clone(),
                net_entity_id: session.net_entity_id.clone(),
                entity_type: session.entity_type,
                account_id: session.account_id.clone(),
                login_name: session.login_name.clone(),
            })
            .collect();
        rows.sort_by(|left, right| left.net_entity_id.cmp(&right.net_entity_id));
        rows
    }

    fn log_event(&self, kind: &str, extra: Map<String, Value>) {
        self.log.emit(kind, self.last_applied_tick, extra);
    }
}

fn json_map(pairs: &[(&str, Value)]) -> Map<String, Value> {
    let mut map = Map::new();
    for (key, value) in pairs {
        map.insert((*key).to_owned(), value.clone());
    }
    map
}

fn utf8_frame(bytes: &[u8]) -> Option<String> {
    String::from_utf8(bytes.to_vec()).ok()
}

fn parse_input_command_json(text: &str) -> Result<InputCommand, ()> {
    let value: serde_json::Value = serde_json::from_str(text).map_err(|_| ())?;
    if value.get("messageType").and_then(serde_json::Value::as_str) != Some("InputCommand") {
        return Err(());
    }
    let commands = value
        .get("commands")
        .and_then(serde_json::Value::as_array)
        .ok_or(())?;
    let mut out = Vec::new();
    for block in commands {
        out.push(super::envelope::CommandBlock {
            mapping_id: block
                .get("mappingId")
                .and_then(serde_json::Value::as_str)
                .ok_or(())?
                .to_owned(),
            payload: block
                .get("payload")
                .and_then(serde_json::Value::as_str)
                .ok_or(())?
                .to_owned(),
            payload_sha256: block
                .get("payloadSha256")
                .and_then(serde_json::Value::as_str)
                .ok_or(())?
                .to_owned(),
        });
    }
    Ok(InputCommand {
        message_type: "InputCommand".to_owned(),
        commands: out,
    })
}
