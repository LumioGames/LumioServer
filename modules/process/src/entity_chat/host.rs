//! Room world-slot: admission, binding, reconnect, query, chat delivery.

use std::collections::HashMap;
use std::thread;
use std::thread::ThreadId;

use lumio_host_runtime::{
    bounded_channel, spawn_supervised, HostClock, SharedClock, SupervisedTask,
};

use super::admission::{
    classify_entity_kind, is_bot_namespace, verify_admission, AdmissionPayload,
};
use super::envelope::InputCommand;
use super::gameplay::{
    ChatMessageEvent, ChatOperation, ChatPersistEntity, ChatPersistSnapshot, ChatTickResult,
    GameplayWorld,
};

/// Player or Bot, classified from login name.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BoundEntityKind {
    Player,
    Bot,
}

impl BoundEntityKind {
    #[must_use]
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Player => "player",
            Self::Bot => "bot",
        }
    }
}

/// Binding five-tuple.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConnectionBinding {
    pub account_id: String,
    pub room_id: String,
    pub net_entity_id: u64,
    pub entity_type: BoundEntityKind,
    pub connection_generation: u64,
}

/// Server resolution of a live entity.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EntityResolution {
    pub net_entity_id: u64,
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
    pub net_entity_ids: Vec<u64>,
    pub entity_types: Vec<BoundEntityKind>,
}

/// Attribute query caller.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AttributeQueryScope {
    ServerAuthoritative,
    ClientReplica,
}

/// Five-outcome plus request-error query result.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AttributeQueryOutcome {
    Ok,
    RequestError,
    NonExistent,
    StaleGeneration,
    Invisible,
    Unauthorized,
    Tombstoned,
}

/// Attribute query request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AttributeQueryRequest {
    pub caller_scope: AttributeQueryScope,
    pub room_id: String,
    pub net_entity_id: u64,
    pub attribute_id: String,
    pub connection_generation: Option<u64>,
}

/// Attribute query result. Failures never alias another entity.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QueryResult {
    pub outcome: AttributeQueryOutcome,
    pub value: Option<String>,
    pub error_code: Option<String>,
    pub observed_tick: u64,
    pub observed_revision: u64,
}

impl QueryResult {
    fn ok(value: String, tick: u64, revision: u64) -> Self {
        Self {
            outcome: AttributeQueryOutcome::Ok,
            value: Some(value),
            error_code: None,
            observed_tick: tick,
            observed_revision: revision,
        }
    }

    fn fail(outcome: AttributeQueryOutcome) -> Self {
        Self {
            outcome,
            value: None,
            error_code: None,
            observed_tick: 0,
            observed_revision: 0,
        }
    }

    fn request_error(code: &str) -> Self {
        Self {
            outcome: AttributeQueryOutcome::RequestError,
            value: None,
            error_code: Some(code.to_owned()),
            observed_tick: 0,
            observed_revision: 0,
        }
    }
}

enum BindingPresence {
    Active,
    Disconnected,
}

struct LiveEntity {
    account_id: String,
    _login_name: String,
    room_id: String,
    net_entity_id: u64,
    entity_type: BoundEntityKind,
    generation: u64,
    presence: BindingPresence,
    connection_id: Option<String>,
    disconnected_at_ms: Option<u64>,
    window: Vec<ChatMessageEvent>,
}

impl LiveEntity {
    fn binding(&self) -> ConnectionBinding {
        ConnectionBinding {
            account_id: self.account_id.clone(),
            room_id: self.room_id.clone(),
            net_entity_id: self.net_entity_id,
            entity_type: self.entity_type,
            connection_generation: self.generation,
        }
    }
}

struct Tombstone {
    room_id: String,
    _account_id: String,
}

struct RoomState {
    revision: u64,
    entities: HashMap<u64, String>,
}

struct Inner {
    clock: SharedClock,
    reconnect_window_ms: u64,
    admission_key_id: u8,
    admission_public: Vec<u8>,
    unix_seconds: u64,
    gameplay: Box<dyn GameplayWorld>,
    rooms: HashMap<String, RoomState>,
    by_account: HashMap<String, LiveEntity>,
    by_connection: HashMap<String, String>,
    tombstones: HashMap<u64, Tombstone>,
    next_net_entity_id: u64,
}

enum OwnerWork {
    Run(Box<dyn FnOnce(&mut Inner) + Send>),
}

/// Slice-scoped Room host. All authoritative work runs on one owner thread.
pub struct EntityChatHost {
    owner_id: ThreadId,
    tx: lumio_host_runtime::Sender<OwnerWork>,
    _owner: SupervisedTask,
}

impl EntityChatHost {
    /// Builds a host that verifies credentials with `admission_public`.
    #[must_use]
    pub fn new(
        reconnect_window_ms: u64,
        clock: SharedClock,
        gameplay: Box<dyn GameplayWorld>,
        admission_key_id: u8,
        admission_public: Vec<u8>,
        unix_seconds: u64,
    ) -> Self {
        let (tx, rx) = bounded_channel(256);
        let (id_tx, id_rx) = bounded_channel(1);
        let owner = spawn_supervised("lumio-entity-chat-owner", move |_cancel| {
            let _ = id_tx.send(thread::current().id());
            let mut inner = Inner {
                clock,
                reconnect_window_ms,
                admission_key_id,
                admission_public,
                unix_seconds,
                gameplay,
                rooms: HashMap::new(),
                by_account: HashMap::new(),
                by_connection: HashMap::new(),
                tombstones: HashMap::new(),
                next_net_entity_id: 1,
            };
            loop {
                match rx.recv() {
                    Ok(OwnerWork::Run(work)) => work(&mut inner),
                    Err(_) => break,
                }
            }
        });
        let owner_id = id_rx.recv().expect("owner thread id");
        Self {
            owner_id,
            tx,
            _owner: owner,
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

    /// Disconnects a live connection and starts the reconnect window.
    #[must_use]
    pub fn disconnect(&self, connection_id: String) -> bool {
        self.on_owner(move |inner| inner.disconnect(&connection_id))
    }

    /// Advances the host monotonic clock.
    pub fn advance_monotonic(&self, delta_ms: u64) {
        self.on_owner(move |inner| inner.clock.advance_ms(delta_ms));
    }

    /// Destroys disconnected entities whose window has elapsed.
    #[must_use]
    pub fn expire_due(&self) -> usize {
        self.on_owner(Inner::expire_due)
    }

    /// Decodes a frozen InputCommand (chat.input) envelope, then queues ChatInput.
    #[must_use]
    pub fn admit_chat_input(&self, connection_id: String, envelope: InputCommand) -> ChatOperation {
        self.on_owner(move |inner| inner.admit_chat_input(&connection_id, &envelope))
    }

    /// Runs one fixed tick in `room_id`.
    #[must_use]
    pub fn run_tick(&self, room_id: String) -> ChatTickResult {
        self.on_owner(move |inner| inner.run_tick(&room_id))
    }

    /// Client self-lookup.
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

    /// Resolve a NetEntityId in a room.
    #[must_use]
    pub fn try_resolve_by_net_entity_id(
        &self,
        room_id: String,
        net_entity_id: u64,
    ) -> Option<EntityResolution> {
        self.on_owner(move |inner| inner.try_resolve_by_net_entity_id(&room_id, net_entity_id))
    }

    /// C-2 attribute query.
    #[must_use]
    pub fn query_attribute(&self, request: AttributeQueryRequest) -> QueryResult {
        self.on_owner(move |inner| inner.query_attribute(&request))
    }

    /// Persist-only last-message snapshot.
    #[must_use]
    pub fn capture_persist_snapshot(&self, room_id: String) -> ChatPersistSnapshot {
        self.on_owner(move |inner| inner.capture_persist_snapshot(&room_id))
    }

    /// Restores persist-only fields. Chat windows stay empty.
    pub fn restore_persist_snapshot(&self, room_id: String, snapshot: ChatPersistSnapshot) {
        self.on_owner(move |inner| inner.restore_persist_snapshot(&room_id, snapshot));
    }

    /// Live entity census. Tombstones are excluded.
    #[must_use]
    pub fn census(&self, room_id: String) -> RoomCensus {
        self.on_owner(move |inner| inner.census(&room_id))
    }

    /// Client-local chat window.
    #[must_use]
    pub fn client_chat_window(&self, connection_id: String) -> Vec<ChatMessageEvent> {
        self.on_owner(move |inner| inner.client_chat_window(&connection_id))
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
        let kind = classify_entity_kind(&payload.login_name, payload.bot_tool_context);
        self.expire_due();
        if self.by_connection.contains_key(connection_id) {
            return RoomAdmitResult::reject("invalid_request");
        }
        if let Some(existing) = self.by_account.get(&payload.account_id) {
            if existing.room_id != room_id {
                return RoomAdmitResult::reject("invalid_request");
            }
            let account = payload.account_id.clone();
            return match existing.presence {
                BindingPresence::Active => self.takeover(&account, connection_id),
                BindingPresence::Disconnected => self.rebind(&account, connection_id, true, false),
            };
        }
        self.gameplay.create_room(room_id);
        let net_entity_id = self.next_net_entity_id;
        self.next_net_entity_id += 1;
        if !self.gameplay.create_entity(room_id, net_entity_id) {
            return RoomAdmitResult::reject("invalid_request");
        }
        let live = LiveEntity {
            account_id: payload.account_id.clone(),
            _login_name: payload.login_name.clone(),
            room_id: room_id.to_owned(),
            net_entity_id,
            entity_type: kind,
            generation: 1,
            presence: BindingPresence::Active,
            connection_id: Some(connection_id.to_owned()),
            disconnected_at_ms: None,
            window: Vec::new(),
        };
        let binding = live.binding();
        self.rooms
            .entry(room_id.to_owned())
            .or_insert_with(|| RoomState {
                revision: 0,
                entities: HashMap::new(),
            })
            .entities
            .insert(net_entity_id, payload.account_id.clone());
        self.by_connection
            .insert(connection_id.to_owned(), payload.account_id.clone());
        self.by_account.insert(payload.account_id.clone(), live);
        RoomAdmitResult::ok(binding, false, false)
    }

    fn takeover(&mut self, account_id: &str, new_connection_id: &str) -> RoomAdmitResult {
        if let Some(existing) = self.by_account.get(account_id) {
            if let Some(old) = existing.connection_id.clone() {
                self.by_connection.remove(&old);
            }
        }
        self.rebind(account_id, new_connection_id, false, true)
    }

    fn rebind(
        &mut self,
        account_id: &str,
        new_connection_id: &str,
        reconnected: bool,
        takeover: bool,
    ) -> RoomAdmitResult {
        let Some(live) = self.by_account.get_mut(account_id) else {
            return RoomAdmitResult::reject("invalid_request");
        };
        live.connection_id = Some(new_connection_id.to_owned());
        live.presence = BindingPresence::Active;
        live.disconnected_at_ms = None;
        live.generation += 1;
        live.window.clear();
        let binding = live.binding();
        self.by_connection
            .insert(new_connection_id.to_owned(), account_id.to_owned());
        RoomAdmitResult::ok(binding, reconnected, takeover)
    }

    fn disconnect(&mut self, connection_id: &str) -> bool {
        self.expire_due();
        let Some(account_id) = self.by_connection.get(connection_id).cloned() else {
            return false;
        };
        let Some(live) = self.by_account.get_mut(&account_id) else {
            return false;
        };
        if !matches!(live.presence, BindingPresence::Active)
            || live.connection_id.as_deref() != Some(connection_id)
        {
            return false;
        }
        self.by_connection.remove(connection_id);
        live.presence = BindingPresence::Disconnected;
        live.connection_id = None;
        live.disconnected_at_ms = Some(self.clock.now_ms());
        true
    }

    fn expire_due(&mut self) -> usize {
        let now = self.clock.now_ms();
        let window = self.reconnect_window_ms;
        let due: Vec<String> = self
            .by_account
            .iter()
            .filter_map(|(account, live)| {
                if matches!(live.presence, BindingPresence::Disconnected) {
                    if let Some(at) = live.disconnected_at_ms {
                        if now.saturating_sub(at) >= window {
                            return Some(account.clone());
                        }
                    }
                }
                None
            })
            .collect();
        let count = due.len();
        for account in due {
            self.destroy_account(&account);
        }
        count
    }

    fn destroy_account(&mut self, account_id: &str) {
        let Some(live) = self.by_account.remove(account_id) else {
            return;
        };
        self.gameplay
            .destroy_entity(&live.room_id, live.net_entity_id);
        if let Some(room) = self.rooms.get_mut(&live.room_id) {
            room.entities.remove(&live.net_entity_id);
        }
        self.tombstones.insert(
            live.net_entity_id,
            Tombstone {
                room_id: live.room_id,
                _account_id: live.account_id,
            },
        );
        if let Some(connection) = live.connection_id {
            self.by_connection.remove(&connection);
        }
    }

    fn admit_chat_input(&mut self, connection_id: &str, envelope: &InputCommand) -> ChatOperation {
        let text = match envelope.try_decode_chat_text() {
            Ok(text) => text,
            Err(code) => return ChatOperation::rejected(code),
        };
        self.expire_due();
        let Some(account_id) = self.by_connection.get(connection_id) else {
            return ChatOperation::rejected("disconnected");
        };
        let Some(live) = self.by_account.get(account_id) else {
            return ChatOperation::rejected("disconnected");
        };
        if !matches!(live.presence, BindingPresence::Active) {
            return ChatOperation::rejected("disconnected");
        }
        let room_id = live.room_id.clone();
        let net_entity_id = live.net_entity_id;
        self.gameplay.admit_chat(&room_id, net_entity_id, &text)
    }

    fn run_tick(&mut self, room_id: &str) -> ChatTickResult {
        self.expire_due();
        if !self.rooms.contains_key(room_id) {
            return ChatTickResult {
                applied_tick: 0,
                events: Vec::new(),
            };
        }
        let tick = self.gameplay.run_tick(room_id);
        if let Some(room) = self.rooms.get_mut(room_id) {
            room.revision += 1;
        }
        let accounts: Vec<String> = self
            .rooms
            .get(room_id)
            .map(|room| room.entities.values().cloned().collect())
            .unwrap_or_default();
        for account in accounts {
            if let Some(live) = self.by_account.get_mut(&account) {
                if matches!(live.presence, BindingPresence::Active) && live.connection_id.is_some()
                {
                    live.window.extend(tick.events.iter().cloned());
                }
            }
        }
        tick
    }

    fn try_self_lookup(&mut self, connection_id: &str) -> Option<ConnectionBinding> {
        self.expire_due();
        let account = self.by_connection.get(connection_id)?;
        let live = self.by_account.get(account)?;
        if matches!(live.presence, BindingPresence::Active)
            && live.connection_id.as_deref() == Some(connection_id)
        {
            Some(live.binding())
        } else {
            None
        }
    }

    fn try_resolve_by_net_entity_id(
        &mut self,
        room_id: &str,
        net_entity_id: u64,
    ) -> Option<EntityResolution> {
        self.expire_due();
        let account = self.rooms.get(room_id)?.entities.get(&net_entity_id)?;
        let live = self.by_account.get(account)?;
        Some(EntityResolution {
            net_entity_id: live.net_entity_id,
            room_id: live.room_id.clone(),
            entity_type: live.entity_type,
            account_id: live.account_id.clone(),
        })
    }

    fn query_attribute(&mut self, request: &AttributeQueryRequest) -> QueryResult {
        self.expire_due();
        if classify_attribute_id(&request.attribute_id) {
            return QueryResult::request_error("storage_access_forbidden");
        }
        if !is_declared_attribute_grammar(&request.attribute_id) {
            return QueryResult::request_error("invalid_attribute_id");
        }
        if let Some(tomb) = self.tombstones.get(&request.net_entity_id) {
            if tomb.room_id != request.room_id {
                return QueryResult::request_error("cross_room_reference");
            }
            return QueryResult::fail(AttributeQueryOutcome::Tombstoned);
        }
        let Some(live) = self.find_entity(request.net_entity_id) else {
            return QueryResult::fail(AttributeQueryOutcome::NonExistent);
        };
        let live_room = live.room_id.clone();
        let live_generation = live.generation;
        if live_room != request.room_id {
            return QueryResult::request_error("cross_room_reference");
        }
        if let Some(generation) = request.connection_generation {
            if generation < live_generation {
                return QueryResult::fail(AttributeQueryOutcome::StaleGeneration);
            }
        }
        self.read_attribute(request)
    }

    fn find_entity(&self, net_entity_id: u64) -> Option<&LiveEntity> {
        self.by_account
            .values()
            .find(|live| live.net_entity_id == net_entity_id)
    }

    fn read_attribute(&mut self, request: &AttributeQueryRequest) -> QueryResult {
        let Some(live) = self.find_entity(request.net_entity_id) else {
            return QueryResult::fail(AttributeQueryOutcome::NonExistent);
        };
        let room_id = live.room_id.clone();
        let entity_type = live.entity_type;
        let account_id = live.account_id.clone();
        let disconnected = matches!(live.presence, BindingPresence::Disconnected);
        let net_entity_id = live.net_entity_id;
        let tick = self.gameplay.current_tick(&room_id);
        let revision = self.rooms.get(&room_id).map_or(0, |room| room.revision);
        let client = request.caller_scope == AttributeQueryScope::ClientReplica;
        match request.attribute_id.as_str() {
            "EntityIdentity.entityType" => {
                QueryResult::ok(entity_type.as_str().to_owned(), tick, revision)
            }
            "EntityIdentity.accountId" => {
                if client {
                    QueryResult::fail(AttributeQueryOutcome::Invisible)
                } else {
                    QueryResult::ok(account_id, tick, revision)
                }
            }
            "EntityIdentity.restrictedFlag" => {
                if client {
                    QueryResult::fail(AttributeQueryOutcome::Unauthorized)
                } else {
                    QueryResult::ok("0".to_owned(), tick, revision)
                }
            }
            "EntityPresence.disconnected" => QueryResult::ok(
                if disconnected {
                    "true".to_owned()
                } else {
                    "false".to_owned()
                },
                tick,
                revision,
            ),
            "ChatComponent.lastMessageText" | "ChatComponent.lastMessageTick" => {
                if client {
                    return QueryResult::fail(AttributeQueryOutcome::Invisible);
                }
                match self.gameplay.last_message(&room_id, net_entity_id) {
                    Some((text, last_tick)) => {
                        let value = if request.attribute_id.ends_with("Text") {
                            text
                        } else {
                            last_tick.to_string()
                        };
                        QueryResult::ok(value, tick, revision)
                    }
                    None => QueryResult::fail(AttributeQueryOutcome::NonExistent),
                }
            }
            _ => QueryResult::request_error("undeclared_attribute"),
        }
    }

    fn capture_persist_snapshot(&mut self, room_id: &str) -> ChatPersistSnapshot {
        let rows = self.gameplay.capture_persist(room_id);
        let mut entities = Vec::new();
        for (net_entity_id, text, tick) in rows {
            if let Some(live) = self.find_entity(net_entity_id) {
                entities.push(ChatPersistEntity {
                    net_entity_id,
                    account_id: live.account_id.clone(),
                    entity_type: live.entity_type,
                    last_message_text: text,
                    last_message_tick: tick,
                    history_count: 0,
                });
            }
        }
        ChatPersistSnapshot { entities }
    }

    fn restore_persist_snapshot(&mut self, room_id: &str, snapshot: ChatPersistSnapshot) {
        self.gameplay.create_room(room_id);
        for entity in snapshot.entities {
            let _ = self.gameplay.create_entity(room_id, entity.net_entity_id);
            let _ = self.gameplay.restore_last_message(
                room_id,
                entity.net_entity_id,
                &entity.last_message_text,
                entity.last_message_tick,
            );
            if entity.net_entity_id >= self.next_net_entity_id {
                self.next_net_entity_id = entity.net_entity_id + 1;
            }
            let live = LiveEntity {
                account_id: entity.account_id.clone(),
                _login_name: String::new(),
                room_id: room_id.to_owned(),
                net_entity_id: entity.net_entity_id,
                entity_type: entity.entity_type,
                generation: 1,
                presence: BindingPresence::Active,
                connection_id: None,
                disconnected_at_ms: None,
                window: Vec::new(),
            };
            self.rooms
                .entry(room_id.to_owned())
                .or_insert_with(|| RoomState {
                    revision: 0,
                    entities: HashMap::new(),
                })
                .entities
                .insert(entity.net_entity_id, entity.account_id.clone());
            if !entity.account_id.is_empty() {
                self.by_account.insert(entity.account_id, live);
            }
        }
    }

    fn census(&mut self, room_id: &str) -> RoomCensus {
        self.expire_due();
        let Some(room) = self.rooms.get(room_id) else {
            return RoomCensus {
                bot_count: 0,
                player_count: 0,
                total: 0,
                net_entity_ids: Vec::new(),
                entity_types: Vec::new(),
            };
        };
        let mut ids = Vec::new();
        let mut kinds = Vec::new();
        let mut bots = 0;
        let mut players = 0;
        let mut rows: Vec<(u64, BoundEntityKind)> = room
            .entities
            .iter()
            .filter_map(|(id, account)| {
                self.by_account
                    .get(account)
                    .map(|live| (*id, live.entity_type))
            })
            .collect();
        rows.sort_by_key(|(id, _)| *id);
        for (id, kind) in rows {
            ids.push(id);
            kinds.push(kind);
            match kind {
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

    fn client_chat_window(&self, connection_id: &str) -> Vec<ChatMessageEvent> {
        let Some(account) = self.by_connection.get(connection_id) else {
            return Vec::new();
        };
        self.by_account
            .get(account)
            .map(|live| live.window.clone())
            .unwrap_or_default()
    }
}

fn classify_attribute_id(attribute_id: &str) -> bool {
    attribute_id.contains('(')
        || attribute_id.starts_with("Storage.")
        || attribute_id.contains('/')
        || attribute_id.contains('\\')
}

fn is_declared_attribute_grammar(attribute_id: &str) -> bool {
    let Some(dot) = attribute_id.find('.') else {
        return false;
    };
    if dot == 0 || dot != attribute_id.rfind('.').unwrap_or(0) || dot == attribute_id.len() - 1 {
        return false;
    }
    let (head, tail) = attribute_id.split_at(dot);
    let tail = &tail[1..];
    let mut chars = head.chars();
    let Some(first) = chars.next() else {
        return false;
    };
    if !first.is_ascii_uppercase() {
        return false;
    }
    if !chars.all(is_attr_char) {
        return false;
    }
    let mut attr = tail.chars();
    let Some(first_attr) = attr.next() else {
        return false;
    };
    first_attr.is_ascii_lowercase() && attr.all(is_attr_char)
}

fn is_attr_char(c: char) -> bool {
    c.is_ascii_alphanumeric()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::entity_chat::admission::{generate_keys, issue_admission_credential};
    use crate::entity_chat::gameplay::{ChatOpKind, LocalGameplay};
    use crate::entity_chat::{InputCommand, ADMISSION_KEY_ID, RECONNECT_WINDOW_MS};

    fn host_with(keys: &crate::entity_chat::Ed25519KeyPair) -> EntityChatHost {
        EntityChatHost::new(
            RECONNECT_WINDOW_MS,
            SharedClock::test(),
            Box::new(LocalGameplay::new()),
            ADMISSION_KEY_ID,
            keys.public.to_vec(),
            1_000,
        )
    }

    fn credential(keys: &crate::entity_chat::Ed25519KeyPair, name: &str, bot: bool) -> String {
        issue_admission_credential(&keys.seed, 1, &format!("acct_{name}"), name, bot, 1, 9_000)
    }

    #[test]
    fn username_password_is_never_an_admission_path() {
        let keys = generate_keys();
        let host = host_with(&keys);
        assert!(!host.try_admit_username_password("room-main", "c1", "Bot01", "123456"));
    }

    #[test]
    fn admit_creates_bot_and_player_and_resolves_bindings() {
        let keys = generate_keys();
        let host = host_with(&keys);
        let bot = host.admit(
            "room-main".to_owned(),
            "c-bot01".to_owned(),
            credential(&keys, "Bot01", true),
        );
        let player = host.admit(
            "room-main".to_owned(),
            "c-browser".to_owned(),
            credential(&keys, "Browser01", false),
        );
        assert!(bot.accepted && player.accepted);
        assert_eq!(
            bot.binding.as_ref().map(|b| b.entity_type),
            Some(BoundEntityKind::Bot)
        );
        assert_eq!(
            player.binding.as_ref().map(|b| b.entity_type),
            Some(BoundEntityKind::Player)
        );
        let census = host.census("room-main".to_owned());
        assert_eq!(census.bot_count, 1);
        assert_eq!(census.player_count, 1);
        let self_bot = host.must_self("c-bot01");
        assert!(host
            .try_resolve_by_net_entity_id("room-main".to_owned(), self_bot.net_entity_id)
            .is_some());
    }

    #[test]
    fn reconnect_within_window_rebinds_entity_a() {
        let keys = generate_keys();
        let host = host_with(&keys);
        let _ = host.admit(
            "room-main".to_owned(),
            "c-bot01".to_owned(),
            credential(&keys, "Bot01", true),
        );
        let entity_a = host.must_self("c-bot01").net_entity_id;
        assert!(host.disconnect("c-bot01".to_owned()));
        let rejected = host.admit_chat_input(
            "c-bot01".to_owned(),
            InputCommand::from_chat_text("while-down"),
        );
        assert_eq!(rejected.kind, ChatOpKind::Rejected);
        let rebind = host.admit(
            "room-main".to_owned(),
            "c-bot01-re".to_owned(),
            credential(&keys, "Bot01", true),
        );
        assert!(rebind.reconnected);
        assert_eq!(rebind.binding.map(|b| b.net_entity_id), Some(entity_a));
        assert!(host.client_chat_window("c-bot01-re".to_owned()).is_empty());
    }

    #[test]
    fn expiry_tombstones_a_and_creates_b() {
        let keys = generate_keys();
        let host = host_with(&keys);
        let _ = host.admit(
            "room-main".to_owned(),
            "c-bot01".to_owned(),
            credential(&keys, "Bot01", true),
        );
        let entity_a = host.must_self("c-bot01").net_entity_id;
        let account = host.must_self("c-bot01").account_id;
        assert!(host.disconnect("c-bot01".to_owned()));
        host.advance_monotonic(RECONNECT_WINDOW_MS + 1);
        assert_eq!(host.expire_due(), 1);
        let created_b = host.admit(
            "room-main".to_owned(),
            "c-bot01-b".to_owned(),
            credential(&keys, "Bot01", true),
        );
        assert!(created_b.accepted);
        let entity_b = created_b.binding.unwrap().net_entity_id;
        assert_ne!(entity_b, entity_a);
        let tomb = host.query_attribute(AttributeQueryRequest {
            caller_scope: AttributeQueryScope::ServerAuthoritative,
            room_id: "room-main".to_owned(),
            net_entity_id: entity_a,
            attribute_id: "EntityIdentity.entityType".to_owned(),
            connection_generation: None,
        });
        assert_eq!(tomb.outcome, AttributeQueryOutcome::Tombstoned);
        assert_eq!(host.must_self("c-bot01-b").account_id, account);
    }

    #[test]
    fn isolation_rejects_cross_room_query() {
        let keys = generate_keys();
        let host = host_with(&keys);
        let _ = host.admit(
            "room-main".to_owned(),
            "c-browser".to_owned(),
            credential(&keys, "Browser01", false),
        );
        let _ = host.admit(
            "room-iso".to_owned(),
            "iso-a".to_owned(),
            credential(&keys, "IsoPlayerA", false),
        );
        let browser = host.must_self("c-browser");
        let cross = host.query_attribute(AttributeQueryRequest {
            caller_scope: AttributeQueryScope::ServerAuthoritative,
            room_id: "room-iso".to_owned(),
            net_entity_id: browser.net_entity_id,
            attribute_id: "EntityIdentity.entityType".to_owned(),
            connection_generation: None,
        });
        assert_eq!(cross.error_code.as_deref(), Some("cross_room_reference"));
    }

    #[test]
    fn attribute_query_five_outcomes() {
        let keys = generate_keys();
        let host = host_with(&keys);
        let _ = host.admit(
            "room-main".to_owned(),
            "c-browser".to_owned(),
            credential(&keys, "Browser01", false),
        );
        let binding = host.must_self("c-browser");
        let ok = host.query_attribute(AttributeQueryRequest {
            caller_scope: AttributeQueryScope::ServerAuthoritative,
            room_id: "room-main".to_owned(),
            net_entity_id: binding.net_entity_id,
            attribute_id: "EntityIdentity.entityType".to_owned(),
            connection_generation: None,
        });
        let invisible = host.query_attribute(AttributeQueryRequest {
            caller_scope: AttributeQueryScope::ClientReplica,
            room_id: "room-main".to_owned(),
            net_entity_id: binding.net_entity_id,
            attribute_id: "ChatComponent.lastMessageText".to_owned(),
            connection_generation: None,
        });
        let unauthorized = host.query_attribute(AttributeQueryRequest {
            caller_scope: AttributeQueryScope::ClientReplica,
            room_id: "room-main".to_owned(),
            net_entity_id: binding.net_entity_id,
            attribute_id: "EntityIdentity.restrictedFlag".to_owned(),
            connection_generation: None,
        });
        let missing = host.query_attribute(AttributeQueryRequest {
            caller_scope: AttributeQueryScope::ServerAuthoritative,
            room_id: "room-main".to_owned(),
            net_entity_id: 999_999,
            attribute_id: "EntityIdentity.entityType".to_owned(),
            connection_generation: None,
        });
        let stale = host.query_attribute(AttributeQueryRequest {
            caller_scope: AttributeQueryScope::ServerAuthoritative,
            room_id: "room-main".to_owned(),
            net_entity_id: binding.net_entity_id,
            attribute_id: "EntityIdentity.entityType".to_owned(),
            connection_generation: Some(0),
        });
        assert_eq!(ok.outcome, AttributeQueryOutcome::Ok);
        assert_eq!(invisible.outcome, AttributeQueryOutcome::Invisible);
        assert_eq!(unauthorized.outcome, AttributeQueryOutcome::Unauthorized);
        assert_eq!(missing.outcome, AttributeQueryOutcome::NonExistent);
        assert_eq!(stale.outcome, AttributeQueryOutcome::StaleGeneration);
    }
}
