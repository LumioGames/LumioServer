//! Hello-wire v1 contract surface.
//!
//! `hello-wire-v1.json` (architecture repo `engine/wire/`) is the single wire
//! truth for MS-00002. The server never hardcodes protocol limits: it loads the
//! contract file passed via `--wire-contract`, validates it, and drives every
//! bound (queue depth, session count, payload size, timeouts) from the parsed
//! values.

use std::fmt::{Display, Formatter};
use std::path::Path;

use serde::Deserialize;

/// The only contract id this server accepts (Handshake const field).
pub const EXPECTED_CONTRACT_ID: &str = "lumio.hello-wire.v1";
/// The only WebSocket subprotocol this server accepts.
pub const EXPECTED_SUBPROTOCOL: &str = "lumio-hello-v1";
/// Name of the build-info sidecar placed next to the native SDK binary.
pub const BUILD_INFO_SIDECAR: &str = "build-info.json";

/// Client roles defined by the contract (exactly `browser` and `bot`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Role {
    /// Browser role.
    Browser,
    /// Bot role.
    Bot,
}

impl Role {
    /// Wire representation.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Browser => "browser",
            Self::Bot => "bot",
        }
    }

    /// Parse the wire representation; anything else is an unknown role.
    #[must_use]
    pub fn parse(value: &str) -> Option<Self> {
        match value {
            "browser" => Some(Self::Browser),
            "bot" => Some(Self::Bot),
            _ => None,
        }
    }
}

impl Display for Role {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

/// Stable wire error codes (contract `errorCodes`, exhaustive).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ErrorCode {
    /// Malformed frame/JSON/message shape.
    BadEnvelope,
    /// payloadSha256 does not match the payload bytes.
    BadPayloadHash,
    /// sequence is not above the committed watermark.
    DuplicateSequence,
    /// `BaselineAck` references a revision other than the sent baseline.
    StaleRevision,
    /// sender is not a known role / does not match the session role.
    UnknownRole,
    /// Another session already holds the requested role.
    RoleTaken,
    /// More sessions than `limits.maxSessions`.
    SessionLimit,
    /// Per-session ingress queue is full.
    QueueFull,
    /// The runtime bridge failed.
    RuntimeFailure,
    /// Unknown messageType.
    UnknownMapping,
    /// Handshake contractId mismatch.
    UnsupportedContract,
    /// Session is not in an input-accepting state.
    SessionClosed,
}

impl ErrorCode {
    /// Wire representation.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::BadEnvelope => "bad_envelope",
            Self::BadPayloadHash => "bad_payload_hash",
            Self::DuplicateSequence => "duplicate_sequence",
            Self::StaleRevision => "stale_revision",
            Self::UnknownRole => "unknown_role",
            Self::RoleTaken => "role_taken",
            Self::SessionLimit => "session_limit",
            Self::QueueFull => "queue_full",
            Self::RuntimeFailure => "runtime_failure",
            Self::UnknownMapping => "unknown_mapping",
            Self::UnsupportedContract => "unsupported_contract",
            Self::SessionClosed => "session_closed",
        }
    }

    /// All codes declared by the contract, in contract order.
    #[must_use]
    pub const fn all() -> [Self; 12] {
        [
            Self::BadEnvelope,
            Self::BadPayloadHash,
            Self::DuplicateSequence,
            Self::StaleRevision,
            Self::UnknownRole,
            Self::RoleTaken,
            Self::SessionLimit,
            Self::QueueFull,
            Self::RuntimeFailure,
            Self::UnknownMapping,
            Self::UnsupportedContract,
            Self::SessionClosed,
        ]
    }

    /// Parse a wire code word; unknown words are `None`.
    #[must_use]
    pub fn from_code(value: &str) -> Option<Self> {
        Self::all().into_iter().find(|code| code.as_str() == value)
    }
}

impl Display for ErrorCode {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

/// Numeric limits block of the contract; the server enforces these values.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Limits {
    /// Maximum UTF-8 byte length of an `InputCommand` payload.
    pub max_payload_bytes: usize,
    /// Maximum concurrently admitted sessions.
    pub max_sessions: usize,
    /// Per-session bounded ingress queue capacity.
    pub ingress_queue_per_session: usize,
    /// Hello log retention in the runtime snapshot.
    pub hello_log_capacity: usize,
    /// Handshake must arrive within this budget after connect.
    pub handshake_timeout_ms: u64,
    /// `BaselineAck` must arrive within this budget after `FullSnapshot`.
    pub baseline_timeout_ms: u64,
    /// Whole-scenario budget (consumed by the integration launcher).
    pub scenario_timeout_ms: u64,
}

/// Parsed and validated hello-wire contract.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WireContract {
    /// Contract id (`contractId`).
    pub contract_id: String,
    /// WebSocket subprotocol.
    pub subprotocol: String,
    /// Maximum accepted WebSocket message size (`transport.maxFrameBytes`).
    pub max_frame_bytes: usize,
    /// Roles (`roles`), always `[browser, bot]` for this contract.
    pub roles: Vec<Role>,
    /// Limits block.
    pub limits: Limits,
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawContract {
    contract_id: Option<String>,
    transport: Option<RawTransport>,
    roles: Option<Vec<String>>,
    limits: Option<RawLimits>,
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawTransport {
    subprotocol: Option<String>,
    max_frame_bytes: Option<usize>,
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawLimits {
    max_payload_bytes: Option<usize>,
    max_sessions: Option<usize>,
    ingress_queue_per_session: Option<usize>,
    hello_log_capacity: Option<usize>,
    handshake_timeout_ms: Option<u64>,
    baseline_timeout_ms: Option<u64>,
    scenario_timeout_ms: Option<u64>,
}

/// Load and validate the wire contract file.
///
/// Every rejection names the offending field so startup logs (exit code 1)
/// point at the exact mismatch.
///
/// # Errors
///
/// Returns a human-readable failure when the file cannot be read, is not the
/// hello-wire v1 contract, or is missing required limits.
pub fn load(path: &Path) -> Result<WireContract, String> {
    let text = std::fs::read_to_string(path)
        .map_err(|error| format!("wire contract {}: {error}", path.display()))?;
    let raw: RawContract = serde_json::from_str(&text)
        .map_err(|error| format!("wire contract {}: {error}", path.display()))?;
    validate(path, &raw)
}

fn validate(path: &Path, raw: &RawContract) -> Result<WireContract, String> {
    let where_ = || format!("wire contract {}: ", path.display());

    let contract_id = raw
        .contract_id
        .clone()
        .ok_or_else(|| format!("{}missing contractId", where_()))?;
    if contract_id != EXPECTED_CONTRACT_ID {
        return Err(format!(
            "{}contractId `{contract_id}` does not match expected `{EXPECTED_CONTRACT_ID}`",
            where_()
        ));
    }

    let transport = raw.transport.as_ref();
    let subprotocol = transport
        .and_then(|t| t.subprotocol.clone())
        .ok_or_else(|| format!("{}missing transport.subprotocol", where_()))?;
    if subprotocol != EXPECTED_SUBPROTOCOL {
        return Err(format!(
            "{}transport.subprotocol `{subprotocol}` does not match expected `{EXPECTED_SUBPROTOCOL}`",
            where_()
        ));
    }
    let max_frame_bytes = transport
        .and_then(|t| t.max_frame_bytes)
        .ok_or_else(|| format!("{}missing transport.maxFrameBytes", where_()))?;
    if max_frame_bytes == 0 {
        return Err(format!(
            "{}transport.maxFrameBytes must be positive",
            where_()
        ));
    }

    let roles_raw = raw
        .roles
        .clone()
        .ok_or_else(|| format!("{}missing roles", where_()))?;
    let roles = roles_raw
        .iter()
        .map(|r| Role::parse(r).ok_or_else(|| format!("{}unknown role `{r}`", where_())))
        .collect::<Result<Vec<Role>, String>>()?;
    if roles != [Role::Browser, Role::Bot] {
        return Err(format!(
            "{}roles must be exactly [browser, bot], got {roles_raw:?}",
            where_()
        ));
    }

    let raw_limits = raw
        .limits
        .clone()
        .ok_or_else(|| format!("{}missing limits", where_()))?;
    let positive = |field: &str, value: Option<usize>| {
        value
            .filter(|v| *v > 0)
            .ok_or_else(|| format!("{}limits.{field} must be present and positive", where_()))
    };
    let present = |field: &str, value: Option<u64>| {
        value.ok_or_else(|| format!("{}limits.{field} must be present", where_()))
    };
    let limits = Limits {
        max_payload_bytes: positive("maxPayloadBytes", raw_limits.max_payload_bytes)?,
        max_sessions: positive("maxSessions", raw_limits.max_sessions)?,
        ingress_queue_per_session: positive(
            "ingressQueuePerSession",
            raw_limits.ingress_queue_per_session,
        )?,
        hello_log_capacity: positive("helloLogCapacity", raw_limits.hello_log_capacity)?,
        handshake_timeout_ms: present("handshakeTimeoutMs", raw_limits.handshake_timeout_ms)?,
        baseline_timeout_ms: present("baselineTimeoutMs", raw_limits.baseline_timeout_ms)?,
        scenario_timeout_ms: present("scenarioTimeoutMs", raw_limits.scenario_timeout_ms)?,
    };

    Ok(WireContract {
        contract_id,
        subprotocol,
        max_frame_bytes,
        roles,
        limits,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    fn write_contract(dir: &std::path::Path, name: &str, body: &str) -> std::path::PathBuf {
        let path = dir.join(name);
        let mut file = std::fs::File::create(&path).expect("create contract fixture");
        file.write_all(body.as_bytes()).expect("write fixture");
        path
    }

    fn valid_body() -> String {
        r#"{
  "contractId": "lumio.hello-wire.v1",
  "transport": { "kind": "websocket", "subprotocol": "lumio-hello-v1", "maxFrameBytes": 65536 },
  "roles": ["browser", "bot"],
  "limits": {
    "maxPayloadBytes": 4096, "maxSessions": 2, "ingressQueuePerSession": 64,
    "helloLogCapacity": 32, "handshakeTimeoutMs": 5000,
    "baselineTimeoutMs": 5000, "scenarioTimeoutMs": 30000
  }
}"#
        .to_owned()
    }

    #[test]
    fn loads_a_valid_contract() {
        let dir = tempfile::tempdir().expect("tempdir");
        let path = write_contract(dir.path(), "ok.json", &valid_body());
        let contract = load(&path).expect("valid contract loads");
        assert_eq!(contract.contract_id, EXPECTED_CONTRACT_ID);
        assert_eq!(contract.subprotocol, EXPECTED_SUBPROTOCOL);
        assert_eq!(contract.max_frame_bytes, 65_536);
        assert_eq!(contract.roles, vec![Role::Browser, Role::Bot]);
        assert_eq!(contract.limits.max_sessions, 2);
        assert_eq!(contract.limits.ingress_queue_per_session, 64);
    }

    #[test]
    fn rejects_wrong_contract_id() {
        let dir = tempfile::tempdir().expect("tempdir");
        let body = valid_body().replace("lumio.hello-wire.v1", "lumio.other.v9");
        let path = write_contract(dir.path(), "wrong-id.json", &body);
        let error = load(&path).expect_err("contract id mismatch must fail");
        assert!(error.contains("contractId"), "error was: {error}");
    }

    #[test]
    fn rejects_wrong_subprotocol() {
        let dir = tempfile::tempdir().expect("tempdir");
        let body = valid_body().replace("lumio-hello-v1", "lumio-other-v2");
        let path = write_contract(dir.path(), "wrong-sub.json", &body);
        let error = load(&path).expect_err("subprotocol mismatch must fail");
        assert!(error.contains("subprotocol"), "error was: {error}");
    }

    #[test]
    fn rejects_missing_limit() {
        let dir = tempfile::tempdir().expect("tempdir");
        let body = valid_body().replace("\"maxSessions\": 2,", "");
        let path = write_contract(dir.path(), "no-limit.json", &body);
        let error = load(&path).expect_err("missing limit must fail");
        assert!(error.contains("maxSessions"), "error was: {error}");
    }

    #[test]
    fn rejects_malformed_json() {
        let dir = tempfile::tempdir().expect("tempdir");
        let path = write_contract(dir.path(), "broken.json", "{ not json");
        assert!(load(&path).is_err());
    }

    #[test]
    fn rejects_missing_file() {
        let error = load(Path::new("Z:/definitely/missing/contract.json"))
            .expect_err("missing file must fail");
        assert!(error.contains("wire contract"), "error was: {error}");
    }

    #[test]
    fn error_codes_match_contract_vocabulary() {
        assert_eq!(ErrorCode::all().len(), 12);
        for code in ErrorCode::all() {
            assert!(code.as_str().len() > 3);
        }
        assert_eq!(ErrorCode::BadEnvelope.as_str(), "bad_envelope");
        assert_eq!(ErrorCode::QueueFull.as_str(), "queue_full");
        assert_eq!(
            ErrorCode::UnsupportedContract.as_str(),
            "unsupported_contract"
        );
    }
}
