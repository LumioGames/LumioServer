//! Runtime consume port. Binding, query, snapshot and persist live in Runtime.

use super::admission::classify_entity_kind;

/// Player or Bot, classified from login name.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BoundEntityKind {
    Player,
    Bot,
}

impl BoundEntityKind {
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Player => "player",
            Self::Bot => "bot",
        }
    }
}

/// Rebind mode matching Runtime `RebindMode`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RebindMode {
    Reconnect,
    Takeover,
}

/// Frozen binding five-tuple from Runtime. Session id is not a binding field.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RuntimeBinding {
    pub account_id: String,
    pub room_id: String,
    pub net_entity_id: String,
    pub entity_type: BoundEntityKind,
    pub connection_generation: u64,
}

/// Admit / rebind outcome forwarded from Runtime.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RuntimeAdmit {
    pub accepted: bool,
    pub code: Option<String>,
    pub binding: Option<RuntimeBinding>,
}

impl RuntimeAdmit {
    #[must_use]
    pub fn ok(binding: RuntimeBinding) -> Self {
        Self {
            accepted: true,
            code: None,
            binding: Some(binding),
        }
    }

    #[must_use]
    pub fn reject(code: &str) -> Self {
        Self {
            accepted: false,
            code: Some(code.to_owned()),
            binding: None,
        }
    }
}

/// Attribute query forwarded to Runtime.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RuntimeQuery {
    pub caller_scope: AttributeQueryScope,
    pub room_id: String,
    pub net_entity_id: String,
    pub attribute_id: String,
    pub connection_generation: Option<u64>,
}

/// Query caller.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AttributeQueryScope {
    ServerAuthoritative,
    ClientReplica,
}

impl AttributeQueryScope {
    #[must_use]
    pub fn as_runtime_str(self) -> &'static str {
        match self {
            Self::ServerAuthoritative => "server-authoritative",
            Self::ClientReplica => "client-replica",
        }
    }
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
    #[must_use]
    pub fn ok(value: String, tick: u64, revision: u64) -> Self {
        Self {
            outcome: AttributeQueryOutcome::Ok,
            value: Some(value),
            error_code: None,
            observed_tick: tick,
            observed_revision: revision,
        }
    }

    #[must_use]
    pub fn fail(outcome: AttributeQueryOutcome) -> Self {
        Self {
            outcome,
            value: None,
            error_code: None,
            observed_tick: 0,
            observed_revision: 0,
        }
    }

    #[must_use]
    pub fn request_error(code: &str) -> Self {
        Self {
            outcome: AttributeQueryOutcome::RequestError,
            value: None,
            error_code: Some(code.to_owned()),
            observed_tick: 0,
            observed_revision: 0,
        }
    }

    #[must_use]
    pub fn from_runtime(outcome: &str, code: Option<&str>, value: Option<String>) -> Self {
        match outcome {
            "ok" => Self::ok(value.unwrap_or_default(), 0, 0),
            "non_existent" => Self::fail(AttributeQueryOutcome::NonExistent),
            "stale_generation" => Self::fail(AttributeQueryOutcome::StaleGeneration),
            "invisible" => Self::fail(AttributeQueryOutcome::Invisible),
            "unauthorized" => Self::fail(AttributeQueryOutcome::Unauthorized),
            "tombstoned" => Self::fail(AttributeQueryOutcome::Tombstoned),
            "request_error" => Self::request_error(code.unwrap_or("invalid_request")),
            other => Self::request_error(other),
        }
    }
}

/// Tick result used only to know which tick/revision to request on the wire.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct RuntimeTick {
    pub applied_tick: u64,
    pub revision: u64,
    pub ok: bool,
    pub event_count: u64,
    pub code: Option<String>,
}

impl RuntimeTick {
    #[must_use]
    pub fn failed(code: &str) -> Self {
        Self {
            applied_tick: 0,
            revision: 0,
            ok: false,
            event_count: 0,
            code: Some(code.to_owned()),
        }
    }

    #[must_use]
    pub fn committed(applied_tick: u64, revision: u64, event_count: u64) -> Self {
        Self {
            applied_tick,
            revision,
            ok: true,
            event_count,
            code: None,
        }
    }
}

/// Chat admit/apply outcome.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatOperation {
    pub kind: ChatOpKind,
    pub error_code: Option<String>,
}

/// Chat operation kind matching ChatOperationKind.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChatOpKind {
    Admitted,
    Committed,
    Rejected,
    Fatal,
}

impl ChatOperation {
    #[must_use]
    pub fn admitted() -> Self {
        Self {
            kind: ChatOpKind::Admitted,
            error_code: None,
        }
    }

    #[must_use]
    pub fn rejected(code: &str) -> Self {
        Self {
            kind: ChatOpKind::Rejected,
            error_code: Some(code.to_owned()),
        }
    }
}

/// Opaque persist record from Runtime `CapturePersist`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PersistRecord {
    pub bytes: Vec<u8>,
}

/// Runtime public surface consumed by the host. The host does not implement it.
pub trait RuntimeSurface: Send {
    fn admit(
        &mut self,
        connection: &str,
        account_id: &str,
        room_id: &str,
        entity_type: BoundEntityKind,
    ) -> RuntimeAdmit;

    fn disconnect(&mut self, connection: &str) -> Result<RuntimeBinding, String>;

    fn rebind(
        &mut self,
        connection: &str,
        account_id: &str,
        room_id: &str,
        mode: RebindMode,
    ) -> RuntimeAdmit;

    fn expire(&mut self, net_entity_id: &str) -> Result<(), String>;

    fn self_lookup(&mut self, connection: &str) -> Option<RuntimeBinding>;

    fn resolve_by_net_entity_id(
        &mut self,
        room_id: &str,
        net_entity_id: &str,
    ) -> Option<RuntimeBinding>;

    fn query_attribute(&mut self, request: &RuntimeQuery) -> QueryResult;

    fn list_bindings(&mut self, room_id: &str) -> Vec<RuntimeBinding>;

    fn attach_member(&mut self, room_id: &str, connection: &str) -> Result<(), String>;

    fn admit_input_command(
        &mut self,
        room_id: &str,
        connection: &str,
        generation: u64,
        envelope_json: &str,
    ) -> ChatOperation;

    fn run_tick(&mut self, room_id: &str, tick_id: u64) -> RuntimeTick;

    fn build_full_snapshot(&mut self, room_id: &str, tick_id: u64, revision: u64) -> Vec<u8>;

    fn build_delta(&mut self, room_id: &str, tick_id: u64, revision: u64) -> Vec<Vec<u8>>;

    fn persist(&mut self, room_id: &str) -> PersistRecord;

    fn restore(&mut self, room_id: &str, bytes: &[u8]) -> Result<(), String>;
}

/// Maps login classification onto Runtime `entityType`.
#[must_use]
pub fn entity_type_of(login_name: &str, bot_tool_context: bool) -> BoundEntityKind {
    classify_entity_kind(login_name, bot_tool_context)
}
