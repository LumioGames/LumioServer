//! CoreCLR host of Runtime EntityBindingQuery + ChatCommandRuntime + Persist.

use std::path::PathBuf;

use serde_json::{json, Value};

use crate::runtime_bridge::{BridgeError, ClrBridge, ClrStart};
use crate::sdk_loader;

use super::runtime::BoundEntityKind;
use super::runtime::{
    ChatOperation, PersistRecord, QueryResult, RebindMode, RuntimeAdmit, RuntimeBinding,
    RuntimeQuery, RuntimeSurface, RuntimeTick,
};

/// Files needed to create the CoreCLR Runtime consume host.
#[derive(Debug, Clone)]
pub struct ClrGameplayConfig {
    pub engine_native: PathBuf,
    pub hostfxr: PathBuf,
    pub runtime_config: PathBuf,
    pub assembly: PathBuf,
    pub entry_type: String,
    pub entry_method: String,
    pub replication_assembly: PathBuf,
    pub ecs_assembly: PathBuf,
}

/// CoreCLR-backed [`RuntimeSurface`].
pub struct ClrGameplay {
    bridge: ClrBridge,
    replication_assembly: String,
    ecs_assembly: String,
    booted: bool,
}

impl ClrGameplay {
    /// Loads the native SDK and creates the process-wide CoreCLR host.
    ///
    /// # Errors
    ///
    /// Returns a human-readable failure when the SDK or CLR host cannot start.
    pub fn start(config: &ClrGameplayConfig) -> Result<Self, String> {
        let lease = sdk_loader::load(&config.engine_native).map_err(|error| error.to_string())?;
        let start = ClrStart {
            hostfxr: config.hostfxr.to_string_lossy().into_owned(),
            runtime_config: config.runtime_config.to_string_lossy().into_owned(),
            assembly: config.assembly.to_string_lossy().into_owned(),
            entry_type: config.entry_type.clone(),
            entry_method: config.entry_method.clone(),
        };
        let bridge = ClrBridge::start(lease, &start)?;
        Ok(Self {
            bridge,
            replication_assembly: config.replication_assembly.to_string_lossy().into_owned(),
            ecs_assembly: config.ecs_assembly.to_string_lossy().into_owned(),
            booted: false,
        })
    }

    fn call(&mut self, request: Value) -> Result<Value, String> {
        if !self.booted {
            let boot = json!({
                "op": "boot",
                "replicationAssembly": self.replication_assembly,
                "ecsAssembly": self.ecs_assembly,
            });
            let body = self
                .bridge
                .invoke_json(&boot.to_string())
                .map_err(bridge_err)?;
            let parsed: Value =
                serde_json::from_str(&body).map_err(|_| "boot response is not JSON".to_owned())?;
            if parsed.get("ok").and_then(Value::as_bool) != Some(true) {
                let detail = parsed
                    .get("code")
                    .and_then(Value::as_str)
                    .unwrap_or("boot_failed");
                return Err(detail.to_owned());
            }
            self.booted = true;
        }
        let body = self
            .bridge
            .invoke_json(&request.to_string())
            .map_err(bridge_err)?;
        serde_json::from_str(&body).map_err(|_| "runtime response is not JSON".to_owned())
    }
}

fn bridge_err(error: BridgeError) -> String {
    match error {
        BridgeError::Rejected { code } => code.as_str().to_owned(),
        BridgeError::Failed { detail } => detail.to_owned(),
    }
}

fn kind_from(value: Option<&str>) -> BoundEntityKind {
    match value {
        Some("bot") => BoundEntityKind::Bot,
        _ => BoundEntityKind::Player,
    }
}

fn binding_from(value: &Value) -> Option<RuntimeBinding> {
    Some(RuntimeBinding {
        account_id: value.get("accountId")?.as_str()?.to_owned(),
        room_id: value.get("roomId")?.as_str()?.to_owned(),
        net_entity_id: value.get("netEntityId")?.as_str()?.to_owned(),
        entity_type: kind_from(value.get("entityType").and_then(Value::as_str)),
        connection_generation: value.get("connectionGeneration")?.as_u64()?,
    })
}

fn admit_from(value: Value) -> RuntimeAdmit {
    if value.get("ok").and_then(Value::as_bool) == Some(true) {
        if let Some(binding) = value.get("binding").and_then(binding_from) {
            return RuntimeAdmit::ok(binding);
        }
    }
    RuntimeAdmit::reject(
        value
            .get("code")
            .and_then(Value::as_str)
            .unwrap_or("invalid_request"),
    )
}

impl RuntimeSurface for ClrGameplay {
    fn admit(
        &mut self,
        connection: &str,
        account_id: &str,
        room_id: &str,
        entity_type: BoundEntityKind,
    ) -> RuntimeAdmit {
        match self.call(json!({
            "op": "admit",
            "connection": connection,
            "accountId": account_id,
            "roomId": room_id,
            "entityType": entity_type.as_str(),
        })) {
            Ok(value) => admit_from(value),
            Err(_) => RuntimeAdmit::reject("runtime_failure"),
        }
    }

    fn disconnect(&mut self, connection: &str) -> Result<RuntimeBinding, String> {
        let value = self.call(json!({ "op": "disconnect", "connection": connection }))?;
        value
            .get("binding")
            .and_then(binding_from)
            .ok_or_else(|| "binding_not_found".to_owned())
    }

    fn rebind(
        &mut self,
        connection: &str,
        account_id: &str,
        room_id: &str,
        mode: RebindMode,
    ) -> RuntimeAdmit {
        let mode = match mode {
            RebindMode::Reconnect => "reconnect",
            RebindMode::Takeover => "takeover",
        };
        match self.call(json!({
            "op": "rebind",
            "connection": connection,
            "accountId": account_id,
            "roomId": room_id,
            "mode": mode,
        })) {
            Ok(value) => admit_from(value),
            Err(_) => RuntimeAdmit::reject("runtime_failure"),
        }
    }

    fn expire(&mut self, net_entity_id: &str) -> Result<(), String> {
        let value = self.call(json!({ "op": "expire", "netEntityId": net_entity_id }))?;
        if value.get("ok").and_then(Value::as_bool) == Some(true) {
            Ok(())
        } else {
            Err(value
                .get("code")
                .and_then(Value::as_str)
                .unwrap_or("invalid_request")
                .to_owned())
        }
    }

    fn self_lookup(&mut self, connection: &str) -> Option<RuntimeBinding> {
        self.call(json!({ "op": "self_lookup", "connection": connection }))
            .ok()
            .and_then(|value| value.get("binding").and_then(binding_from))
    }

    fn resolve_by_net_entity_id(
        &mut self,
        room_id: &str,
        net_entity_id: &str,
    ) -> Option<RuntimeBinding> {
        self.call(json!({
            "op": "resolve",
            "roomId": room_id,
            "netEntityId": net_entity_id
        }))
        .ok()
        .and_then(|value| value.get("binding").and_then(binding_from))
    }

    fn query_attribute(&mut self, request: &RuntimeQuery) -> QueryResult {
        match self.call(json!({
            "op": "query",
            "callerScope": request.caller_scope.as_runtime_str(),
            "roomId": request.room_id,
            "netEntityId": request.net_entity_id,
            "attributeId": request.attribute_id,
            "connectionGeneration": request.connection_generation,
        })) {
            Ok(value) => QueryResult::from_runtime(
                value
                    .get("outcome")
                    .and_then(Value::as_str)
                    .unwrap_or("request_error"),
                value.get("code").and_then(Value::as_str),
                value
                    .get("value")
                    .and_then(Value::as_str)
                    .map(str::to_owned),
            ),
            Err(_) => QueryResult::request_error("runtime_failure"),
        }
    }

    fn list_bindings(&mut self, room_id: &str) -> Vec<RuntimeBinding> {
        self.call(json!({ "op": "list_bindings", "roomId": room_id }))
            .ok()
            .and_then(|value| value.get("bindings").and_then(Value::as_array).cloned())
            .unwrap_or_default()
            .iter()
            .filter_map(binding_from)
            .collect()
    }

    fn attach_member(&mut self, room_id: &str, connection: &str) -> Result<(), String> {
        let value = self.call(json!({
            "op": "attach_member",
            "roomId": room_id,
            "connection": connection
        }))?;
        if value.get("ok").and_then(Value::as_bool) == Some(true) {
            Ok(())
        } else {
            Err("runtime_failure".to_owned())
        }
    }

    fn admit_input_command(
        &mut self,
        room_id: &str,
        connection: &str,
        generation: u64,
        envelope_json: &str,
    ) -> ChatOperation {
        match self.call(json!({
            "op": "admit_input",
            "roomId": room_id,
            "connection": connection,
            "connectionGeneration": generation,
            "envelope": envelope_json,
        })) {
            Ok(value) if value.get("ok").and_then(Value::as_bool) == Some(true) => {
                ChatOperation::admitted()
            }
            Ok(value) => ChatOperation::rejected(
                value
                    .get("code")
                    .and_then(Value::as_str)
                    .unwrap_or("invalid_request"),
            ),
            Err(_) => ChatOperation::rejected("runtime_failure"),
        }
    }

    fn run_tick(&mut self, room_id: &str, tick_id: u64) -> RuntimeTick {
        let Ok(value) = self.call(json!({ "op": "tick", "roomId": room_id, "tickId": tick_id }))
        else {
            return RuntimeTick {
                applied_tick: 0,
                revision: 0,
            };
        };
        RuntimeTick {
            applied_tick: value
                .get("appliedTick")
                .and_then(Value::as_u64)
                .unwrap_or(0),
            revision: value.get("revision").and_then(Value::as_u64).unwrap_or(0),
        }
    }

    fn build_full_snapshot(&mut self, room_id: &str, tick_id: u64, revision: u64) -> Vec<u8> {
        full_snapshot_bytes_from_runtime(
            self.call(json!({
                "op": "build_full_snapshot",
                "roomId": room_id,
                "tickId": tick_id,
                "revision": revision
            }))
            .ok(),
        )
    }

    fn build_delta(&mut self, room_id: &str, tick_id: u64, revision: u64) -> Vec<Vec<u8>> {
        self.call(json!({
            "op": "build_delta",
            "roomId": room_id,
            "tickId": tick_id,
            "revision": revision
        }))
        .ok()
        .and_then(|value| value.get("frames").and_then(Value::as_array).cloned())
        .unwrap_or_default()
        .iter()
        .filter_map(|row| row.as_str().map(|text| text.as_bytes().to_vec()))
        .collect()
    }

    fn persist(&mut self, room_id: &str) -> PersistRecord {
        let hex = self
            .call(json!({ "op": "persist", "roomId": room_id }))
            .ok()
            .and_then(|value| {
                value
                    .get("bytesHex")
                    .and_then(Value::as_str)
                    .map(str::to_owned)
            })
            .unwrap_or_default();
        PersistRecord {
            bytes: decode_hex(&hex).unwrap_or_default(),
        }
    }

    fn restore(&mut self, room_id: &str, bytes: &[u8]) -> Result<(), String> {
        let value = self.call(json!({
            "op": "restore",
            "roomId": room_id,
            "bytesHex": hex_lower(bytes),
        }))?;
        if value.get("ok").and_then(Value::as_bool) == Some(true) {
            Ok(())
        } else {
            Err("restore_failed".to_owned())
        }
    }
}

fn decode_hex(hex: &str) -> Option<Vec<u8>> {
    if !hex.len().is_multiple_of(2) {
        return None;
    }
    let mut out = Vec::with_capacity(hex.len() / 2);
    let bytes = hex.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        let hi = from_nibble(bytes[i])?;
        let lo = from_nibble(bytes[i + 1])?;
        out.push((hi << 4) | lo);
        i += 2;
    }
    Some(out)
}

fn from_nibble(b: u8) -> Option<u8> {
    match b {
        b'0'..=b'9' => Some(b - b'0'),
        b'a'..=b'f' => Some(b - b'a' + 10),
        _ => None,
    }
}

fn hex_lower(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut out = String::with_capacity(bytes.len() * 2);
    for &byte in bytes {
        out.push(char::from(HEX[(byte >> 4) as usize]));
        out.push(char::from(HEX[(byte & 0x0f) as usize]));
    }
    out
}

/// Maps a Runtime `build_full_snapshot` JSON envelope to wire bytes.
/// Missing/failed Runtime responses must not become a host-minted FullSnapshot.
pub(crate) fn full_snapshot_bytes_from_runtime(response: Option<Value>) -> Vec<u8> {
    response
        .and_then(|value| {
            value
                .get("json")
                .and_then(Value::as_str)
                .filter(|text| !text.is_empty())
                .map(|text| text.as_bytes().to_vec())
        })
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    const HOST_MINTED_EMPTY: &[u8] =
        br#"{"messageType":"FullSnapshot","tickId":0,"revision":0,"stateBlocks":[]}"#;

    #[test]
    fn runtime_failure_does_not_mint_empty_full_snapshot() {
        assert_eq!(full_snapshot_bytes_from_runtime(None), Vec::<u8>::new());
        assert_ne!(full_snapshot_bytes_from_runtime(None), HOST_MINTED_EMPTY);
        assert_eq!(
            full_snapshot_bytes_from_runtime(Some(json!({"ok": false, "code": "runtime_failure"}))),
            Vec::<u8>::new()
        );
        assert_ne!(
            full_snapshot_bytes_from_runtime(Some(json!({"ok": false, "code": "runtime_failure"}))),
            HOST_MINTED_EMPTY
        );
        assert_eq!(
            full_snapshot_bytes_from_runtime(Some(json!({"ok": true}))),
            Vec::<u8>::new()
        );
    }

    #[test]
    fn runtime_json_is_forwarded_unchanged() {
        let runtime = r#"{"messageType":"FullSnapshot","tickId":1,"revision":1,"stateBlocks":[{"mappingId":"entity.identity","payload":"aa","payloadSha256":"bb"}]}"#;
        let bytes = full_snapshot_bytes_from_runtime(Some(json!({ "ok": true, "json": runtime })));
        assert_eq!(bytes, runtime.as_bytes());
        assert!(String::from_utf8_lossy(&bytes).contains("\"mappingId\":\"entity.identity\""));
    }
}
