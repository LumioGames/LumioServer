//! Frozen `lumio.gameplay-envelope.v1` InputCommand (chat.input tenant).

use sha2::{Digest, Sha256};

use super::crypto::{hex_lower, BinWriter};

/// Wire messageType for this envelope.
pub const MESSAGE_TYPE: &str = "InputCommand";
/// Chat command mapping.
pub const CHAT_INPUT_MAPPING: &str = "chat.input";
const MAX_COMMANDS: usize = 16;

/// One CommandBlock.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CommandBlock {
    pub mapping_id: String,
    pub payload: String,
    pub payload_sha256: String,
}

/// Frozen InputCommand envelope.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct InputCommand {
    pub message_type: String,
    pub commands: Vec<CommandBlock>,
}

impl InputCommand {
    /// Encodes a single chat.input block with LumioBinV1 fieldOrder `[text]`.
    #[must_use]
    pub fn from_chat_text(text: &str) -> Self {
        let mut writer = BinWriter::new();
        writer.write_ascii(text);
        let payload = writer.into_bytes();
        Self {
            message_type: MESSAGE_TYPE.to_owned(),
            commands: vec![CommandBlock {
                mapping_id: CHAT_INPUT_MAPPING.to_owned(),
                payload: hex_lower(&payload),
                payload_sha256: hex_lower(&Sha256::digest(&payload)),
            }],
        }
    }

    /// Validates messageType, mapping, payload digest, and LumioBinV1 text.
    ///
    /// # Errors
    ///
    /// Returns a frozen envelope error code.
    pub fn try_decode_chat_text(&self) -> Result<String, &'static str> {
        if self.message_type != MESSAGE_TYPE {
            return Err("bad_envelope");
        }
        if self.commands.is_empty() {
            return Err("unknown_command_type");
        }
        if self.commands.len() > MAX_COMMANDS {
            return Err("bad_envelope");
        }

        let mut previous: Option<&str> = None;
        let mut decoded: Option<String> = None;
        for block in &self.commands {
            if block.mapping_id.is_empty() {
                return Err("unknown_command_type");
            }
            if let Some(prev) = previous {
                if prev >= block.mapping_id.as_str() {
                    return Err("block_order_violation");
                }
            }
            previous = Some(block.mapping_id.as_str());

            let Some(payload) = decode_hex(&block.payload) else {
                return Err("undecodable_payload");
            };
            if !is_lower_sha256(&block.payload_sha256)
                || hex_lower(&Sha256::digest(&payload)) != block.payload_sha256
            {
                return Err("bad_payload_hash");
            }
            if block.mapping_id != CHAT_INPUT_MAPPING {
                return Err("unknown_command_type");
            }
            if decoded.is_some() {
                return Err("bad_envelope");
            }
            decoded = Some(decode_utf8_prefixed(&payload).ok_or("undecodable_payload")?);
        }
        decoded.ok_or("unknown_command_type")
    }

    /// C-1 JSON object for Runtime `AdmitInputCommand`.
    #[must_use]
    pub fn to_json(&self) -> String {
        serde_json::json!({
            "messageType": self.message_type,
            "commands": self.commands.iter().map(|block| {
                serde_json::json!({
                    "mappingId": block.mapping_id,
                    "payload": block.payload,
                    "payloadSha256": block.payload_sha256,
                })
            }).collect::<Vec<_>>(),
        })
        .to_string()
    }
}

/// C-1 ConnectionSuperseded text frame.
#[must_use]
pub fn connection_superseded_json(net_entity_id: u64, new_generation: u64) -> String {
    serde_json::json!({
        "messageType": "ConnectionSuperseded",
        "reasonCode": "connection_superseded",
        "netEntityId": net_entity_id,
        "newConnectionGeneration": new_generation,
    })
    .to_string()
}

/// Runtime issues 32-char lowercase hex of a u64 sequence.
/// C-1 `NetEntityId` is the same u64 (decimal or shorter hex on some clients).
#[must_use]
pub fn normalize_net_entity_id(net_entity_id: &str) -> String {
    let lower = net_entity_id.trim().to_ascii_lowercase();
    if lower.len() == 32
        && lower
            .bytes()
            .all(|b| matches!(b, b'0'..=b'9' | b'a'..=b'f'))
    {
        return lower;
    }
    if !lower.is_empty() && lower.bytes().all(|b| b.is_ascii_digit()) {
        if let Ok(value) = lower.parse::<u64>() {
            return format!("{value:032x}");
        }
    }
    if let Ok(value) = u64::from_str_radix(&lower, 16) {
        return format!("{value:032x}");
    }
    lower
}

/// Runtime issues 32-char lowercase hex of a u64 sequence.
#[must_use]
pub fn net_entity_id_to_u64(net_entity_id: &str) -> Option<u64> {
    u64::from_str_radix(&normalize_net_entity_id(net_entity_id), 16).ok()
}

fn is_lower_sha256(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|b| matches!(b, b'0'..=b'9' | b'a'..=b'f'))
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

fn decode_utf8_prefixed(payload: &[u8]) -> Option<String> {
    if payload.len() < 4 {
        return None;
    }
    let declared = u32::from_le_bytes(payload[0..4].try_into().ok()?) as usize;
    let body = payload.get(4..)?;
    if declared != body.len() {
        return None;
    }
    String::from_utf8(body.to_vec()).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn gg_matches_frozen_hash_example() {
        let envelope = InputCommand::from_chat_text("gg");
        assert_eq!(envelope.message_type, MESSAGE_TYPE);
        assert_eq!(envelope.commands.len(), 1);
        assert_eq!(envelope.commands[0].mapping_id, CHAT_INPUT_MAPPING);
        assert_eq!(envelope.commands[0].payload, "020000006767");
        assert_eq!(
            envelope.commands[0].payload_sha256,
            "5dbd584f1718b8bcd0dab4abeea83169f4a990defab81a8316ed845798d92dab"
        );
        assert_eq!(envelope.try_decode_chat_text().expect("decode"), "gg");
    }

    #[test]
    fn bad_payload_hash_is_rejected() {
        let mut envelope = InputCommand::from_chat_text("hello-Bot01");
        envelope.commands[0].payload_sha256 =
            format!("ab{}", &envelope.commands[0].payload_sha256[2..]);
        assert_eq!(envelope.try_decode_chat_text(), Err("bad_payload_hash"));
    }

    #[test]
    fn unknown_mapping_is_rejected() {
        let mut envelope = InputCommand::from_chat_text("gg");
        envelope.commands[0].mapping_id = "chat.not-a-command".to_owned();
        assert_eq!(envelope.try_decode_chat_text(), Err("unknown_command_type"));
    }

    #[test]
    fn non_ascii_utf8_chat_input_roundtrips() {
        let envelope = InputCommand::from_chat_text("你好");
        assert_eq!(
            envelope.try_decode_chat_text().expect("utf8 decode"),
            "你好"
        );
    }
}
