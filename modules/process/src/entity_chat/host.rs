//! Consume-only Room host: session table + Runtime forward + NativeCore timers + wire.

use std::collections::HashMap;
use std::thread;
use std::thread::ThreadId;

use lumio_host_runtime::{
    bounded_channel, spawn_supervised, HostClock, KernelHandle, KernelTimer, Sender, SharedClock,
    SupervisedTask, TimerMode,
};

use super::admission::{is_bot_namespace, verify_admission, AdmissionPayload};
use super::envelope::{connection_superseded_json, net_entity_id_to_u64, InputCommand};
use super::runtime::BoundEntityKind;
use super::runtime::{
    AttributeQueryScope, ChatOperation, PersistRecord, QueryResult, RebindMode, RuntimeAdmit,
    RuntimeBinding, RuntimeQuery, RuntimeSurface, RuntimeTick,
};
use super::wire::{RoomListener, WireEvent, WireSender};

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
    egress: Option<WireSender>,
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
    account_sessions: HashMap<String, String>,
    expire_watch: HashMap<KernelHandle, String>,
    pending_egress: HashMap<String, WireSender>,
    tick_id: u64,
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
                account_sessions: HashMap::new(),
                expire_watch: HashMap::new(),
                pending_egress: HashMap::new(),
                tick_id: 0,
            };
            if inner
                .kernel
                .schedule_repeating(TimerMode::TickFrame, 1, 1, DISPATCH_TICK)
                .is_err()
            {
                return;
            }
            loop {
                match rx.recv() {
                    Ok(OwnerWork::Run(work)) => work(&mut inner),
                    Ok(OwnerWork::Wire(event)) => inner.on_wire(event),
                    Err(_) => break,
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
        self.on_owner(move |inner| inner.try_resolve_by_net_entity_id(&room_id, &net_entity_id))
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
        if let Some(old_id) = self.account_sessions.get(&payload.account_id).cloned() {
            return self.takeover(room_id, connection_id, payload, kind, &old_id);
        }
        let admitted = self
            .runtime
            .admit(connection_id, &payload.account_id, room_id, kind);
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
        _kind: BoundEntityKind,
        old_id: &str,
    ) -> RoomAdmitResult {
        if let Some(old) = self.sessions.get(old_id) {
            if old.room_id != room_id {
                return RoomAdmitResult::reject("invalid_request");
            }
        }
        let rebound = self.runtime.rebind(
            connection_id,
            &payload.account_id,
            room_id,
            RebindMode::Takeover,
        );
        let Some(binding) = rebound.binding.clone() else {
            return RoomAdmitResult::reject(rebound.code.as_deref().unwrap_or("invalid_request"));
        };
        let new_generation = binding.connection_generation;
        let net_u64 = net_entity_id_to_u64(&binding.net_entity_id).unwrap_or(0);
        if let Some(old) = self.sessions.remove(old_id) {
            if let Some(egress) = &old.egress {
                let _ = egress.send_text(connection_superseded_json(net_u64, new_generation));
                let _ = egress.close();
            }
        }
        self.account_sessions.remove(&payload.account_id);
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
        let egress = self.pending_egress.remove(connection_id);
        let session = Session {
            connection_id: connection_id.to_owned(),
            session_id: session_id.clone(),
            account_id: payload.account_id.clone(),
            login_name: payload.login_name.clone(),
            room_id: runtime_binding.room_id.clone(),
            net_entity_id: runtime_binding.net_entity_id.clone(),
            entity_type: runtime_binding.entity_type,
            generation: runtime_binding.connection_generation,
            egress,
        };
        let binding = ConnectionBinding::from_runtime(runtime_binding, session_id);
        self.account_sessions
            .insert(payload.account_id.clone(), connection_id.to_owned());
        self.sessions.insert(connection_id.to_owned(), session);
        self.send_full_snapshot(connection_id);
        RoomAdmitResult::ok(binding, reconnected, takeover)
    }

    fn disconnect(&mut self, connection_id: &str) -> bool {
        let Some(session) = self.sessions.remove(connection_id) else {
            return false;
        };
        self.account_sessions.remove(&session.account_id);
        if let Some(egress) = &session.egress {
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
        self.tick_id = self.tick_id.saturating_add(1);
        let Ok(fired) = self.kernel.advance_tick_frame(self.tick_id) else {
            return RuntimeTick::failed("runtime_failure");
        };
        if !fired.iter().any(|row| row.dispatch_id == DISPATCH_TICK) {
            return RuntimeTick::failed("runtime_failure");
        }
        let tick = self.runtime.run_tick(room_id, self.tick_id);
        if !tick.ok {
            return tick;
        }
        let frames = self
            .runtime
            .build_delta(room_id, tick.applied_tick, tick.revision);
        self.broadcast(room_id, &frames);
        tick
    }

    fn broadcast(&self, room_id: &str, frames: &[Vec<u8>]) {
        for session in self.sessions.values() {
            if session.room_id != room_id {
                continue;
            }
            let Some(egress) = &session.egress else {
                continue;
            };
            for frame in frames {
                let text = String::from_utf8_lossy(frame).into_owned();
                let _ = egress.send_text(text);
            }
        }
    }

    fn send_full_snapshot(&mut self, connection_id: &str) {
        let Some(session) = self.sessions.get(connection_id) else {
            return;
        };
        let room_id = session.room_id.clone();
        let Some(egress) = session.egress.clone() else {
            return;
        };
        let bytes = self.runtime.build_full_snapshot(&room_id, self.tick_id, 0);
        if bytes.is_empty() {
            return;
        }
        let _ = egress.send_text(String::from_utf8_lossy(&bytes).into_owned());
    }

    fn on_wire(&mut self, event: WireEvent) {
        match event {
            WireEvent::Attached {
                connection_id,
                egress,
            } => {
                if let Some(session) = self.sessions.get_mut(&connection_id) {
                    session.egress = Some(egress);
                    self.send_full_snapshot(&connection_id);
                } else {
                    self.pending_egress.insert(connection_id, egress);
                }
            }
            WireEvent::Input {
                connection_id,
                text,
            } => {
                if let Ok(envelope) = parse_input_command_json(&text) {
                    let _ = self.admit_chat_input(&connection_id, &envelope);
                }
            }
            WireEvent::Closed { connection_id } => {
                if let Some(session) = self.sessions.get_mut(&connection_id) {
                    session.egress = None;
                }
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
        let runtime = self
            .runtime
            .resolve_by_net_entity_id(room_id, net_entity_id)?;
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
            net_entity_id: request.net_entity_id.clone(),
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
