//! NDJSON audit sink.
//!
//! Event kinds and their required fields mirror the wire contract's
//! `process.auditEventKinds` block exactly; every line carries `ts` (UTC epoch
//! milliseconds). Writes are append-only and each line is flushed to the OS on
//! emit so a hard kill cannot lose already-reported events; `flush` syncs the
//! file before process exit.

use std::fs::File;
use std::io::Write;
use std::path::Path;
use std::sync::Arc;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use serde_json::{json, Value};

/// Shared audit handle used by every server task.
pub type SharedAudit = Arc<Mutex<AuditLog>>;

/// Append-mode NDJSON audit log.
pub struct AuditLog {
    file: File,
}

/// All audit kinds emitted by the server (contract `process.auditEventKinds`).
pub const KINDS: [&str; 12] = [
    "server_listening",
    "session_open",
    "handshake_accepted",
    "handshake_rejected",
    "baseline_sent",
    "baseline_acked",
    "ingress_received",
    "ingress_rejected",
    "tick_committed",
    "delta_routed",
    "session_closed",
    "server_shutdown",
];

fn epoch_ms() -> u64 {
    u64::try_from(
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_millis())
            .unwrap_or_default(),
    )
    .unwrap_or_default()
}

impl AuditLog {
    /// Open (create if needed) the audit file in append mode.
    ///
    /// # Errors
    ///
    /// Propagates filesystem errors from opening the audit sink.
    pub fn open(path: &Path) -> std::io::Result<Self> {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let file = File::options().create(true).append(true).open(path)?;
        Ok(Self { file })
    }

    fn emit(&mut self, kind: &str, fields: Value) {
        let mut event = json!({ "ts": epoch_ms(), "kind": kind });
        if let (Value::Object(target), Value::Object(extra)) = (&mut event, fields) {
            for (name, value) in extra {
                target.insert(name, value);
            }
        }
        let mut line = event.to_string();
        line.push('\n');
        // Best effort: an audit write failure must not take the server down;
        // durability of already-written lines is preserved by append mode.
        let _ = self.file.write_all(line.as_bytes());
    }

    /// `server_listening` — required: port, pid, contractId.
    pub fn server_listening(&mut self, port: u16, pid: u32, contract_id: &str) {
        self.emit(
            "server_listening",
            json!({ "port": port, "pid": pid, "contractId": contract_id }),
        );
    }

    /// `session_open` — required: sessionId, remote.
    pub fn session_open(&mut self, session_id: &str, remote: &str) {
        self.emit(
            "session_open",
            json!({ "sessionId": session_id, "remote": remote }),
        );
    }

    /// `handshake_accepted` — required: sessionId, role, clientName.
    pub fn handshake_accepted(&mut self, session_id: &str, role: &str, client_name: &str) {
        self.emit(
            "handshake_accepted",
            json!({ "sessionId": session_id, "role": role, "clientName": client_name }),
        );
    }

    /// `handshake_rejected` — required: sessionId, code, detail.
    pub fn handshake_rejected(&mut self, session_id: &str, code: &str, detail: &str) {
        self.emit(
            "handshake_rejected",
            json!({ "sessionId": session_id, "code": code, "detail": detail }),
        );
    }

    /// `baseline_sent` — required: sessionId, revision, tickId.
    pub fn baseline_sent(&mut self, session_id: &str, revision: u64, tick_id: u64) {
        self.emit(
            "baseline_sent",
            json!({ "sessionId": session_id, "revision": revision, "tickId": tick_id }),
        );
    }

    /// `baseline_acked` — required: sessionId, revision.
    pub fn baseline_acked(&mut self, session_id: &str, revision: u64) {
        self.emit(
            "baseline_acked",
            json!({ "sessionId": session_id, "revision": revision }),
        );
    }

    /// `ingress_received` — required: sessionId, sender, sequence, payloadSha256.
    pub fn ingress_received(
        &mut self,
        session_id: &str,
        sender: &str,
        sequence: u64,
        payload_sha256: &str,
    ) {
        self.emit(
            "ingress_received",
            json!({
                "sessionId": session_id,
                "sender": sender,
                "sequence": sequence,
                "payloadSha256": payload_sha256
            }),
        );
    }

    /// `ingress_rejected` — required: sessionId, sender, sequence, code.
    pub fn ingress_rejected(&mut self, session_id: &str, sender: &str, sequence: u64, code: &str) {
        self.emit(
            "ingress_rejected",
            json!({
                "sessionId": session_id,
                "sender": sender,
                "sequence": sequence,
                "code": code
            }),
        );
    }

    /// `tick_committed` — required: tickId, revision, deltaCount, senders.
    pub fn tick_committed(
        &mut self,
        tick_id: u64,
        revision: u64,
        delta_count: usize,
        senders: &[String],
    ) {
        self.emit(
            "tick_committed",
            json!({
                "tickId": tick_id,
                "revision": revision,
                "deltaCount": delta_count,
                "senders": senders
            }),
        );
    }

    /// `delta_routed` — required: sessionId, sender, sequence, tickId, revision, payloadSha256.
    pub fn delta_routed(
        &mut self,
        session_id: &str,
        sender: &str,
        sequence: u64,
        tick_id: u64,
        revision: u64,
        payload_sha256: &str,
    ) {
        self.emit(
            "delta_routed",
            json!({
                "sessionId": session_id,
                "sender": sender,
                "sequence": sequence,
                "tickId": tick_id,
                "revision": revision,
                "payloadSha256": payload_sha256
            }),
        );
    }

    /// `session_closed` — required: sessionId, code.
    pub fn session_closed(&mut self, session_id: &str, code: &str) {
        self.emit(
            "session_closed",
            json!({ "sessionId": session_id, "code": code }),
        );
    }

    /// `server_shutdown` — required: reason, sessions.
    pub fn server_shutdown(&mut self, reason: &str, sessions: usize) {
        self.emit(
            "server_shutdown",
            json!({ "reason": reason, "sessions": sessions }),
        );
    }

    /// Sync the audit file to disk (shutdown gate before exit).
    pub fn flush(&mut self) {
        let _ = self.file.flush();
        let _ = self.file.sync_all();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Required fields per kind, transcribed from the contract's
    /// `process.auditEventKinds` block (plus the mandatory `ts`).
    fn required_fields(kind: &str) -> &'static [&'static str] {
        match kind {
            "server_listening" => &["port", "pid", "contractId"],
            "session_open" => &["sessionId", "remote"],
            "handshake_accepted" => &["sessionId", "role", "clientName"],
            "handshake_rejected" => &["sessionId", "code", "detail"],
            "baseline_sent" => &["sessionId", "revision", "tickId"],
            "baseline_acked" => &["sessionId", "revision"],
            "ingress_received" => &["sessionId", "sender", "sequence", "payloadSha256"],
            "ingress_rejected" => &["sessionId", "sender", "sequence", "code"],
            "tick_committed" => &["tickId", "revision", "deltaCount", "senders"],
            "delta_routed" => &[
                "sessionId",
                "sender",
                "sequence",
                "tickId",
                "revision",
                "payloadSha256",
            ],
            "session_closed" => &["sessionId", "code"],
            "server_shutdown" => &["reason", "sessions"],
            _ => &[],
        }
    }

    #[test]
    fn every_kind_emits_ts_and_its_required_fields() {
        let dir = tempfile::tempdir().expect("tempdir");
        let path = dir.path().join("audit.ndjson");
        let mut log = AuditLog::open(&path).expect("open audit");

        log.server_listening(1234, 42, "lumio.hello-wire.v1");
        log.session_open("s-1", "127.0.0.1:55");
        log.handshake_accepted("s-1", "browser", "browser-client");
        log.handshake_rejected("s-2", "role_taken", "browser already held");
        log.baseline_sent("s-1", 0, 0);
        log.baseline_acked("s-1", 0);
        log.ingress_received("s-1", "browser", 1, "aa");
        log.ingress_rejected("s-1", "browser", 1, "duplicate_sequence");
        log.tick_committed(1, 1, 1, &["browser".to_owned()]);
        log.delta_routed("s-2", "browser", 1, 1, 1, "aa");
        log.session_closed("s-1", "client_closed");
        log.server_shutdown("stdin shutdown", 2);
        log.flush();

        let text = std::fs::read_to_string(&path).expect("read audit");
        let lines: Vec<&str> = text.lines().collect();
        assert_eq!(lines.len(), KINDS.len());
        for (line, kind) in lines.iter().zip(KINDS) {
            let event: Value = serde_json::from_str(line).unwrap_or_else(|e| panic!("{kind}: {e}"));
            assert_eq!(event["kind"], kind, "line: {line}");
            assert!(event["ts"].is_u64(), "ts missing on {kind}");
            for field in required_fields(kind) {
                assert!(
                    event.get(*field).is_some_and(|v| !v.is_null()),
                    "{kind} missing {field}: {line}"
                );
            }
        }
    }

    #[test]
    fn audit_appends_across_opens() {
        let dir = tempfile::tempdir().expect("tempdir");
        let path = dir.path().join("audit.ndjson");
        {
            let mut log = AuditLog::open(&path).expect("open");
            log.server_shutdown("first", 0);
        }
        {
            let mut log = AuditLog::open(&path).expect("reopen");
            log.server_shutdown("second", 1);
        }
        let text = std::fs::read_to_string(&path).expect("read");
        assert_eq!(text.lines().count(), 2);
    }
}
