//! CoreCLR host of Lumio.Game.ServerGameplay.ChatRoomWorld.

use std::path::PathBuf;

use serde_json::{json, Value};

use crate::runtime_bridge::{BridgeError, ClrBridge, ClrStart};
use crate::sdk_loader;

use super::gameplay::{ChatMessageEvent, ChatOperation, ChatTickResult, GameplayWorld};

/// Files needed to create the CoreCLR gameplay host.
#[derive(Debug, Clone)]
pub struct ClrGameplayConfig {
    pub engine_native: PathBuf,
    pub hostfxr: PathBuf,
    pub runtime_config: PathBuf,
    pub assembly: PathBuf,
    pub entry_type: String,
    pub entry_method: String,
    pub gameplay_assembly: PathBuf,
}

/// CoreCLR-backed [`GameplayWorld`].
pub struct ClrGameplay {
    bridge: ClrBridge,
    gameplay_assembly: String,
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
            gameplay_assembly: config.gameplay_assembly.to_string_lossy().into_owned(),
            booted: false,
        })
    }

    fn call(&mut self, request: Value) -> Result<Value, String> {
        if !self.booted {
            let boot = json!({
                "op": "boot",
                "gameplayAssembly": self.gameplay_assembly,
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
        serde_json::from_str(&body).map_err(|_| "gameplay response is not JSON".to_owned())
    }
}

fn bridge_err(error: BridgeError) -> String {
    match error {
        BridgeError::Rejected { code } => code.as_str().to_owned(),
        BridgeError::Failed { detail } => detail.to_owned(),
    }
}

impl GameplayWorld for ClrGameplay {
    fn create_room(&mut self, room_id: &str) {
        let _ = self.call(json!({ "op": "create_room", "roomId": room_id }));
    }

    fn create_entity(&mut self, room_id: &str, net_entity_id: u64) -> bool {
        self.call(json!({
            "op": "create_entity",
            "roomId": room_id,
            "netEntityId": net_entity_id
        }))
        .ok()
        .and_then(|value| value.get("ok").and_then(Value::as_bool))
        .unwrap_or(false)
    }

    fn destroy_entity(&mut self, room_id: &str, net_entity_id: u64) -> bool {
        self.call(json!({
            "op": "destroy_entity",
            "roomId": room_id,
            "netEntityId": net_entity_id
        }))
        .ok()
        .and_then(|value| value.get("ok").and_then(Value::as_bool))
        .unwrap_or(false)
    }

    fn admit_chat(&mut self, room_id: &str, sender: u64, text: &str) -> ChatOperation {
        match self.call(json!({
            "op": "admit_chat",
            "roomId": room_id,
            "senderNetEntityId": sender,
            "text": text
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

    fn run_tick(&mut self, room_id: &str) -> ChatTickResult {
        let Ok(value) = self.call(json!({ "op": "tick", "roomId": room_id })) else {
            return ChatTickResult {
                applied_tick: 0,
                events: Vec::new(),
            };
        };
        let applied_tick = value
            .get("appliedTick")
            .and_then(Value::as_u64)
            .unwrap_or(0);
        let events = value
            .get("events")
            .and_then(Value::as_array)
            .map(|rows| {
                rows.iter()
                    .filter_map(|row| {
                        Some(ChatMessageEvent {
                            message_id: row.get("messageId").and_then(Value::as_u64)?,
                            room_sequence: row.get("roomSequence").and_then(Value::as_u64)?,
                            sender_net_entity_id: row
                                .get("senderNetEntityId")
                                .and_then(Value::as_u64)?,
                            text: row.get("text").and_then(Value::as_str)?.to_owned(),
                            applied_tick: row.get("appliedTick").and_then(Value::as_u64)?,
                        })
                    })
                    .collect()
            })
            .unwrap_or_default();
        ChatTickResult {
            applied_tick,
            events,
        }
    }

    fn last_message(&mut self, room_id: &str, net_entity_id: u64) -> Option<(String, u64)> {
        let value = self
            .call(json!({
                "op": "get_component",
                "roomId": room_id,
                "netEntityId": net_entity_id
            }))
            .ok()?;
        if value.get("ok").and_then(Value::as_bool) != Some(true) {
            return None;
        }
        let text = value
            .get("lastMessageText")
            .and_then(Value::as_str)?
            .to_owned();
        let tick = value.get("lastMessageTick").and_then(Value::as_u64)?;
        Some((text, tick))
    }

    fn capture_persist(&mut self, room_id: &str) -> Vec<(u64, String, u64)> {
        let Ok(value) = self.call(json!({ "op": "persist", "roomId": room_id })) else {
            return Vec::new();
        };
        value
            .get("entities")
            .and_then(Value::as_array)
            .map(|rows| {
                rows.iter()
                    .filter_map(|row| {
                        Some((
                            row.get("netEntityId").and_then(Value::as_u64)?,
                            row.get("lastMessageText")
                                .and_then(Value::as_str)?
                                .to_owned(),
                            row.get("lastMessageTick").and_then(Value::as_u64)?,
                        ))
                    })
                    .collect()
            })
            .unwrap_or_default()
    }

    fn restore_last_message(
        &mut self,
        room_id: &str,
        net_entity_id: u64,
        text: &str,
        tick: u64,
    ) -> bool {
        self.call(json!({
            "op": "restore",
            "roomId": room_id,
            "netEntityId": net_entity_id,
            "text": text,
            "lastMessageTick": tick
        }))
        .ok()
        .and_then(|value| value.get("ok").and_then(Value::as_bool))
        .unwrap_or(false)
    }

    fn current_tick(&mut self, room_id: &str) -> u64 {
        self.call(json!({ "op": "current_tick", "roomId": room_id }))
            .ok()
            .and_then(|value| value.get("tick").and_then(Value::as_u64))
            .unwrap_or(0)
    }
}
