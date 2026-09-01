//! Runtime bridge: the only path between the server and authoritative state.
//!
//! The world loop talks to the runtime exclusively through [`RuntimeBridge`].
//! [`ClrBridge`] crosses the SDK FFI with the runtime's UTF-8 JSON op protocol
//! (`{"op":"enqueue"|"tick"|"snapshot"|"shutdown", ...}`) over
//! `clr_host_call`, growing the output buffer on `BufferTooSmall`. Deltas seen
//! by the network are only ever produced by `tick`/`snapshot` responses — the
//! server never echoes ingress.
//!
//! Op protocol facts (architecture repo `engine/abi/native-abi.json` and the
//! runtime entry `Lumio.GameRuntime.HelloEntry`): enqueue takes the command
//! fields flat on the request root; a domain rejection answers `rc=0` with
//! `{"ok":false,"code":"<wire code>"}`; a malformed request answers non-zero.

use serde_json::{json, Value};

use crate::sdk_loader::ClrHostHandle;
use crate::sdk_loader::SdkLease;
use crate::sdk_loader::SdkStatus;
use crate::wire::ErrorCode;

/// Initial output buffer for one op call (64 KiB, per the milestone brief).
const OUTPUT_BUFFER_BYTES: usize = 64 * 1024;
/// Upper bound for BufferTooSmall-driven growth (abuse guard).
const MAX_OUTPUT_BUFFER_BYTES: usize = 4 * 1024 * 1024;

/// Result of one authoritative tick.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TickOutcome {
    /// Tick counter after the tick (advances only on non-empty ticks).
    pub tick_id: u64,
    /// Revision after the tick.
    pub revision: u64,
    /// Runtime-shaped delta objects (no `messageType`; the world adds it on
    /// the wire envelope when routing).
    pub deltas: Vec<Value>,
}

/// Snapshot view parsed from a `snapshot` op response.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SnapshotView {
    /// Current tick counter.
    pub tick_id: u64,
    /// Current revision.
    pub revision: u64,
    /// Hello log records (wire `HelloRecord` objects).
    pub hello_log: Vec<Value>,
}

/// Why a bridge op failed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum BridgeError {
    /// The runtime rejected the op with a wire error code
    /// (`{"ok":false,"code":...}`); forward it to the client per contract.
    Rejected {
        /// Wire code the runtime chose.
        code: ErrorCode,
    },
    /// The op call itself failed (FFI status, malformed response): the
    /// contract maps this to `runtime_failure`.
    Failed {
        /// Machine detail for logs.
        detail: &'static str,
    },
}

impl BridgeError {
    /// Wire code to surface for this failure.
    #[must_use]
    pub const fn wire_code(&self) -> ErrorCode {
        match self {
            Self::Rejected { code } => *code,
            Self::Failed { .. } => ErrorCode::RuntimeFailure,
        }
    }
}

/// Authoritative runtime boundary owned by the world loop.
pub trait RuntimeBridge: Send {
    /// Queue one `InputCommand` envelope (full wire JSON).
    ///
    /// # Errors
    ///
    /// [`BridgeError::Rejected`] for domain rejections (the runtime's own
    /// duplicate/shape/hash checks), [`BridgeError::Failed`] for transport.
    fn enqueue(&mut self, command_json: &str) -> Result<(), BridgeError>;

    /// Run one tick at `now_ms`; returns committed deltas.
    ///
    /// # Errors
    ///
    /// See [`enqueue`](RuntimeBridge::enqueue).
    fn tick(&mut self, now_ms: u64) -> Result<TickOutcome, BridgeError>;

    /// Full snapshot response body (`{"ok":true,"tickId","revision","helloLog"}`).
    ///
    /// # Errors
    ///
    /// See [`enqueue`](RuntimeBridge::enqueue).
    fn snapshot(&mut self) -> Result<String, BridgeError>;

    /// Ask the runtime to quiesce.
    ///
    /// # Errors
    ///
    /// See [`enqueue`](RuntimeBridge::enqueue).
    fn shutdown(&mut self) -> Result<(), BridgeError>;
}

/// Start payload for the managed runtime (CLI flags, verbatim).
#[derive(Debug, Clone)]
pub struct ClrStart {
    /// `--hostfxr` path.
    pub hostfxr: String,
    /// `--runtime-config` path.
    pub runtime_config: String,
    /// `--assembly` path.
    pub assembly: String,
    /// `--entry-type` assembly-qualified type name.
    pub entry_type: String,
    /// `--entry-method` name.
    pub entry_method: String,
}

impl ClrStart {
    /// The ABI's composite entry spec: `'<type>;<method>'` (split at the
    /// last `;`).
    #[must_use]
    pub fn entry_spec(&self) -> String {
        format!("{};{}", self.entry_type, self.entry_method)
    }
}

/// Bridge over the real SDK/CoreCLR host.
///
/// Real-DLL exercise happens in the integration phase; the request and
/// response codecs are pure functions covered by unit tests. All FFI lives in
/// [`crate::sdk_loader`]; this type only sequences op calls.
pub struct ClrBridge {
    lease: SdkLease,
    handle: ClrHostHandle,
}

impl ClrBridge {
    /// Create the CLR host through the SDK root table.
    ///
    /// # Errors
    ///
    /// Human-readable failure from `create_clr_host`.
    pub fn start(lease: SdkLease, start: &ClrStart) -> Result<Self, String> {
        let handle = lease.create_clr_host(
            &start.hostfxr,
            &start.runtime_config,
            &start.assembly,
            &start.entry_spec(),
        )?;
        Ok(Self { lease, handle })
    }

    /// One JSON op call used by hello-wire and the entity-chat gameplay bridge.
    pub(crate) fn invoke_json(&mut self, request: &str) -> Result<String, BridgeError> {
        self.call(request)
    }

    /// One op call: send `request`, return the response body.
    fn call(&mut self, request: &str) -> Result<String, BridgeError> {
        let mut output = vec![0_u8; OUTPUT_BUFFER_BYTES];
        let result = self
            .lease
            .clr_host_call(self.handle, request.as_bytes(), &mut output)
            .map_err(|_| BridgeError::Failed {
                detail: "clr_host_call invocation failed",
            })?;
        let written = match SdkStatus::from_i32(result.status) {
            Some(SdkStatus::Success) => result.written,
            Some(SdkStatus::BufferTooSmall) => {
                let required =
                    usize::try_from(result.written).map_err(|_| BridgeError::Failed {
                        detail: "required size overflow",
                    })?;
                if required > MAX_OUTPUT_BUFFER_BYTES || required <= output.len() {
                    return Err(BridgeError::Failed {
                        detail: "op response exceeds the output buffer bound",
                    });
                }
                output.resize(required, 0);
                let retry = self
                    .lease
                    .clr_host_call(self.handle, request.as_bytes(), &mut output)
                    .map_err(|_| BridgeError::Failed {
                        detail: "clr_host_call retry failed",
                    })?;
                if SdkStatus::from_i32(retry.status) != Some(SdkStatus::Success) {
                    return Err(BridgeError::Failed {
                        detail: "op response still too large after growth",
                    });
                }
                retry.written
            }
            _ => {
                return Err(BridgeError::Failed {
                    detail: "clr_host_call status",
                });
            }
        };
        let written = usize::try_from(written)
            .unwrap_or(output.len())
            .min(output.len());
        String::from_utf8(output[..written].to_vec()).map_err(|_| BridgeError::Failed {
            detail: "op response is not UTF-8",
        })
    }
}

impl Drop for ClrBridge {
    fn drop(&mut self) {
        // Destroy before the lease frees the module; failures are surfaced by
        // the caller that already ran the shutdown op.
        let _ = self.lease.destroy_clr_host(self.handle);
    }
}

impl RuntimeBridge for ClrBridge {
    fn enqueue(&mut self, command_json: &str) -> Result<(), BridgeError> {
        let response = self.call(&enqueue_request(command_json))?;
        expect_ok(&response)
    }

    fn tick(&mut self, now_ms: u64) -> Result<TickOutcome, BridgeError> {
        let response = self.call(&tick_request(now_ms))?;
        parse_tick_response(&response)
    }

    fn snapshot(&mut self) -> Result<String, BridgeError> {
        self.call(&snapshot_request())
    }

    fn shutdown(&mut self) -> Result<(), BridgeError> {
        let response = self.call(&shutdown_request())?;
        expect_ok(&response)
    }
}

/// Build the `enqueue` op request: the command fields sit flat on the root
/// next to `op` (the runtime entry reads them there).
#[must_use]
pub fn enqueue_request(command_json: &str) -> String {
    let parsed: Value = serde_json::from_str(command_json).unwrap_or(Value::Null);
    let Value::Object(mut command) = parsed else {
        return json!({ "op": "enqueue" }).to_string();
    };
    command.insert("op".to_owned(), json!("enqueue"));
    Value::Object(command).to_string()
}

/// Build the `tick` op request.
#[must_use]
pub fn tick_request(now_ms: u64) -> String {
    json!({ "op": "tick", "nowMs": now_ms }).to_string()
}

/// Build the `snapshot` op request.
#[must_use]
pub fn snapshot_request() -> String {
    json!({ "op": "snapshot" }).to_string()
}

/// Build the `shutdown` op request.
#[must_use]
pub fn shutdown_request() -> String {
    json!({ "op": "shutdown" }).to_string()
}

fn expect_ok(response: &str) -> Result<(), BridgeError> {
    let value: Value = serde_json::from_str(response).map_err(|_| BridgeError::Failed {
        detail: "malformed op response",
    })?;
    match value.get("ok").and_then(Value::as_bool) {
        Some(true) => Ok(()),
        Some(false) => {
            let code = value
                .get("code")
                .and_then(Value::as_str)
                .and_then(ErrorCode::from_code)
                .unwrap_or(ErrorCode::RuntimeFailure);
            Err(BridgeError::Rejected { code })
        }
        None => Err(BridgeError::Failed {
            detail: "op response missing ok",
        }),
    }
}

/// Parse a `tick` op response.
///
/// # Errors
///
/// [`BridgeError::Failed`] when the response is malformed.
pub fn parse_tick_response(response: &str) -> Result<TickOutcome, BridgeError> {
    let failed = |detail: &'static str| BridgeError::Failed { detail };
    let value: Value =
        serde_json::from_str(response).map_err(|_| failed("malformed tick response"))?;
    if value.get("ok").and_then(Value::as_bool) != Some(true) {
        return Err(failed("tick response is not ok"));
    }
    let tick_id = value
        .get("tickId")
        .and_then(Value::as_u64)
        .ok_or_else(|| failed("tick response missing tickId"))?;
    let revision = value
        .get("revision")
        .and_then(Value::as_u64)
        .ok_or_else(|| failed("tick response missing revision"))?;
    let deltas = value
        .get("deltas")
        .and_then(Value::as_array)
        .cloned()
        .ok_or_else(|| failed("tick response missing deltas"))?;
    Ok(TickOutcome {
        tick_id,
        revision,
        deltas,
    })
}

/// Parse a `snapshot` op response body.
///
/// # Errors
///
/// [`BridgeError::Failed`] when the body is malformed.
pub fn parse_snapshot_response(response: &str) -> Result<SnapshotView, BridgeError> {
    let failed = |detail: &'static str| BridgeError::Failed { detail };
    let value: Value =
        serde_json::from_str(response).map_err(|_| failed("malformed snapshot response"))?;
    if value.get("ok").and_then(Value::as_bool) != Some(true) {
        return Err(failed("snapshot response is not ok"));
    }
    let tick_id = value
        .get("tickId")
        .and_then(Value::as_u64)
        .ok_or_else(|| failed("snapshot response missing tickId"))?;
    let revision = value
        .get("revision")
        .and_then(Value::as_u64)
        .ok_or_else(|| failed("snapshot response missing revision"))?;
    let hello_log = value
        .get("helloLog")
        .and_then(Value::as_array)
        .cloned()
        .ok_or_else(|| failed("snapshot response missing helloLog"))?;
    Ok(SnapshotView {
        tick_id,
        revision,
        hello_log,
    })
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use std::collections::VecDeque;
    use std::sync::{Arc, Condvar, Mutex};
    use std::time::Duration;

    #[test]
    fn entry_spec_joins_type_and_method() {
        let start = ClrStart {
            hostfxr: "h".to_owned(),
            runtime_config: "r".to_owned(),
            assembly: "a".to_owned(),
            entry_type: "T, A".to_owned(),
            entry_method: "lumio_hello_entry".to_owned(),
        };
        assert_eq!(start.entry_spec(), "T, A;lumio_hello_entry");
    }

    #[test]
    fn enqueue_request_flattens_the_command_fields() {
        let command = r#"{
            "messageType": "InputCommand",
            "sender": "browser",
            "sequence": 1,
            "kind": "hello",
            "payload": "Hello World",
            "payloadSha256": "ab",
            "sentAtMs": 7
        }"#;
        let value: Value = serde_json::from_str(&enqueue_request(command)).expect("valid json");
        assert_eq!(value["op"], "enqueue");
        assert_eq!(value["sender"], "browser");
        assert_eq!(value["sequence"], 1);
        assert_eq!(value["sentAtMs"], 7);
    }

    #[test]
    fn op_requests_carry_the_runtime_field_names() {
        assert_eq!(
            serde_json::from_str::<Value>(&tick_request(7)).unwrap()["nowMs"],
            7
        );
        assert_eq!(
            serde_json::from_str::<Value>(&snapshot_request()).unwrap()["op"],
            "snapshot"
        );
        assert_eq!(
            serde_json::from_str::<Value>(&shutdown_request()).unwrap()["op"],
            "shutdown"
        );
    }

    #[test]
    fn tick_response_parses_outcome_without_message_type() {
        let body = r#"{"ok":true,"tickId":3,"revision":4,"deltas":[{"sender":"bot","sequence":1,"kind":"hello","payload":"p","payloadSha256":"ab","tickId":3,"revision":4,"originSentAtMs":1,"committedAtMs":2,"commandSequence":1}]}"#;
        let outcome = parse_tick_response(body).expect("parse");
        assert_eq!(outcome.tick_id, 3);
        assert_eq!(outcome.revision, 4);
        assert_eq!(outcome.deltas.len(), 1);
        assert!(outcome.deltas[0].get("messageType").is_none());
        assert!(parse_tick_response(r#"{"tickId":1}"#).is_err());
        assert!(parse_tick_response(r#"{"ok":false,"code":"queue_full"}"#).is_err());
    }

    #[test]
    fn snapshot_response_parses_view() {
        let body = r#"{"ok":true,"tickId":2,"revision":5,"helloLog":[{"sender":"browser"}]}"#;
        let view = parse_snapshot_response(body).expect("parse");
        assert_eq!(view.tick_id, 2);
        assert_eq!(view.revision, 5);
        assert_eq!(view.hello_log.len(), 1);
        assert!(parse_snapshot_response("{}").is_err());
    }

    #[test]
    fn op_rejections_map_to_wire_codes() {
        for (body, code) in [
            (
                r#"{"ok":false,"code":"duplicate_sequence"}"#,
                ErrorCode::DuplicateSequence,
            ),
            (
                r#"{"ok":false,"code":"bad_payload_hash"}"#,
                ErrorCode::BadPayloadHash,
            ),
            (r#"{"ok":false,"code":"queue_full"}"#, ErrorCode::QueueFull),
            (
                r#"{"ok":false,"code":"unknown_role"}"#,
                ErrorCode::UnknownRole,
            ),
            (
                r#"{"ok":false,"code":"made_up"}"#,
                ErrorCode::RuntimeFailure,
            ),
            (r#"{"ok":false}"#, ErrorCode::RuntimeFailure),
        ] {
            assert_eq!(
                expect_ok(body),
                Err(BridgeError::Rejected { code }),
                "{body}"
            );
        }
        assert_eq!(expect_ok(r#"{"ok":true}"#), Ok(()));
        assert!(matches!(
            expect_ok("not json"),
            Err(BridgeError::Failed { .. })
        ));
    }

    /// Gate that can hold the (sync) world loop inside `tick` so a test can
    /// fill a per-session ingress queue. Scanner-safe alternative to a sleep.
    #[derive(Clone, Default)]
    pub(crate) struct StallGate {
        state: Arc<(Mutex<bool>, Condvar)>,
    }

    impl StallGate {
        pub(crate) fn wait(&self, max: Duration) {
            let (lock, condvar) = &*self.state;
            let Ok(mut open) = lock.lock() else {
                return;
            };
            let deadline = std::time::Instant::now() + max;
            while !*open {
                let now = std::time::Instant::now();
                if now >= deadline {
                    return;
                }
                let (guard, timeout) = condvar.wait_timeout(open, deadline - now).expect("lock");
                open = guard;
                if timeout.timed_out() && !*open {
                    return;
                }
            }
        }
    }

    /// In-test authoritative runtime: mirrors the real `HelloEntry` op protocol
    /// (runtime-shaped deltas without `messageType`) with injectable failures
    /// and an optional tick stall.
    pub(crate) struct TestBridge {
        hello_log: VecDeque<Value>,
        pending: VecDeque<Value>,
        revision: u64,
        tick_id: u64,
        capacity: usize,
        pub(crate) fail_enqueue: bool,
        pub(crate) fail_tick: bool,
        pub(crate) fail_snapshot: bool,
        pub(crate) stall: Option<StallGate>,
    }

    impl TestBridge {
        pub(crate) const fn new() -> Self {
            Self {
                hello_log: VecDeque::new(),
                pending: VecDeque::new(),
                revision: 0,
                tick_id: 0,
                capacity: 32,
                fail_enqueue: false,
                fail_tick: false,
                fail_snapshot: false,
                stall: None,
            }
        }

        pub(crate) fn with_stall(mut self, stall: StallGate) -> Self {
            self.stall = Some(stall);
            self
        }
    }

    impl Default for TestBridge {
        fn default() -> Self {
            Self::new()
        }
    }

    impl RuntimeBridge for TestBridge {
        fn enqueue(&mut self, command_json: &str) -> Result<(), BridgeError> {
            if self.fail_enqueue {
                return Err(BridgeError::Failed {
                    detail: "test-induced enqueue failure",
                });
            }
            let command: Value =
                serde_json::from_str(command_json).map_err(|_| BridgeError::Failed {
                    detail: "malformed command",
                })?;
            self.pending.push_back(command);
            Ok(())
        }

        fn tick(&mut self, now_ms: u64) -> Result<TickOutcome, BridgeError> {
            if let Some(stall) = &self.stall {
                stall.wait(Duration::from_millis(500));
            }
            if self.fail_tick {
                return Err(BridgeError::Failed {
                    detail: "test-induced tick failure",
                });
            }
            let mut deltas = Vec::new();
            while let Some(command) = self.pending.pop_front() {
                self.revision += 1;
                self.tick_id += 1;
                let delta = json!({
                    "tickId": self.tick_id,
                    "revision": self.revision,
                    "sender": command["sender"],
                    "sequence": command["sequence"],
                    "kind": "hello",
                    "payload": command["payload"],
                    "payloadSha256": command["payloadSha256"],
                    "originSentAtMs": command["sentAtMs"],
                    "committedAtMs": now_ms,
                    "commandSequence": command["sequence"]
                });
                self.hello_log.push_back(delta.clone());
                while self.hello_log.len() > self.capacity {
                    self.hello_log.pop_front();
                }
                deltas.push(delta);
            }
            Ok(TickOutcome {
                tick_id: self.tick_id,
                revision: self.revision,
                deltas,
            })
        }

        fn snapshot(&mut self) -> Result<String, BridgeError> {
            if self.fail_snapshot {
                return Err(BridgeError::Failed {
                    detail: "test-induced snapshot failure",
                });
            }
            Ok(json!({
                "ok": true,
                "tickId": self.tick_id,
                "revision": self.revision,
                "helloLog": self.hello_log.iter().collect::<Vec<_>>()
            })
            .to_string())
        }

        fn shutdown(&mut self) -> Result<(), BridgeError> {
            Ok(())
        }
    }

    #[test]
    fn test_bridge_commits_one_delta_per_command() {
        let mut bridge = TestBridge::new();
        let command = json!({
            "messageType": "InputCommand",
            "sender": "browser",
            "sequence": 1,
            "kind": "hello",
            "payload": "Hello World",
            "payloadSha256": "a591a6d40bf420404a011733cfb7b190d62c65bf0bcda32b57b277d9ad9f146e",
            "sentAtMs": 1000
        });
        bridge.enqueue(&command.to_string()).expect("enqueue");
        let outcome = bridge.tick(2000).expect("tick");
        assert_eq!(outcome.deltas.len(), 1);
        assert_eq!(outcome.tick_id, 1);
        assert_eq!(outcome.revision, 1);
        assert_eq!(outcome.deltas[0]["commandSequence"], 1);
        assert_eq!(outcome.deltas[0]["originSentAtMs"], 1000);
        assert_eq!(outcome.deltas[0]["committedAtMs"], 2000);

        // Empty ticks do not advance tick_id (contract determinism rule).
        let empty = bridge.tick(3000).expect("empty tick");
        assert!(empty.deltas.is_empty());
        assert_eq!(empty.tick_id, 1);
        assert_eq!(empty.revision, 1);

        let view = parse_snapshot_response(&bridge.snapshot().expect("snapshot")).expect("view");
        assert_eq!(view.hello_log.len(), 1);
        assert_eq!(view.revision, 1);
    }

    #[test]
    fn test_bridge_failure_injections() {
        let mut bridge = TestBridge::new();
        bridge.fail_enqueue = true;
        assert!(matches!(
            bridge.enqueue("{}"),
            Err(BridgeError::Failed { .. })
        ));
        bridge.fail_enqueue = false;
        bridge.fail_tick = true;
        assert!(matches!(bridge.tick(0), Err(BridgeError::Failed { .. })));
        bridge.fail_tick = false;
        bridge.fail_snapshot = true;
        assert!(matches!(bridge.snapshot(), Err(BridgeError::Failed { .. })));
    }
}
