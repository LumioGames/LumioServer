//! Offline verify_admission against lumio.account-port.v1.

use super::crypto::{
    self, base64url_decode, base64url_encode, fill_random, generate_keypair, sign_typed,
    verify_typed, BinReader, BinWriter, ADMISSION_PAYLOAD_TYPE, ADMISSION_PAYLOAD_VERSION,
    ADMISSION_TRUST_DOMAIN, BOT_TOOL_PAYLOAD_TYPE, BOT_TOOL_PAYLOAD_VERSION, BOT_TOOL_SCOPE,
    BOT_TOOL_TRUST_DOMAIN, NONCE_LEN, SIGNATURE_LEN,
};

pub use crypto::KeyPair as Ed25519KeyPair;

/// Decoded admission payload. The wire credential is never stored here.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AdmissionPayload {
    pub key_id: u8,
    pub account_id: String,
    pub login_name: String,
    pub bot_tool_context: bool,
    pub issued_at: u64,
    pub expires_at: u64,
}

/// Generate a seed keypair.
#[must_use]
pub fn generate_keys() -> Ed25519KeyPair {
    generate_keypair()
}

/// True when `login_name` matches `Bot` plus decimal digits.
#[must_use]
pub fn is_bot_namespace(login_name: &str) -> bool {
    let rest = match login_name.strip_prefix("Bot") {
        Some(rest) if !rest.is_empty() => rest,
        _ => return false,
    };
    rest.bytes().all(|byte| byte.is_ascii_digit())
}

/// Player unless Bot namespace AND bot-tool context.
#[must_use]
pub fn classify_entity_kind(login_name: &str, bot_tool_context: bool) -> super::BoundEntityKind {
    if is_bot_namespace(login_name) && bot_tool_context {
        super::BoundEntityKind::Bot
    } else {
        super::BoundEntityKind::Player
    }
}

/// Verifies an opaque admission credential. Never logs the wire value.
#[must_use]
pub fn verify_admission(
    wire: &str,
    expected_key_id: u8,
    public_key: &[u8],
    unix_seconds: u64,
) -> Result<AdmissionPayload, String> {
    if wire.is_empty() {
        return Err("admission_credential_malformed".to_owned());
    }
    let framed =
        base64url_decode(wire).ok_or_else(|| "admission_credential_malformed".to_owned())?;
    if framed.len() <= SIGNATURE_LEN {
        return Err("admission_credential_malformed".to_owned());
    }
    let split = framed.len() - SIGNATURE_LEN;
    let payload_bytes = &framed[..split];
    let signature = &framed[split..];
    let payload = decode_admission(payload_bytes)
        .ok_or_else(|| "admission_credential_malformed".to_owned())?;
    if payload.key_id != expected_key_id
        || !verify_typed(
            public_key,
            ADMISSION_TRUST_DOMAIN,
            ADMISSION_PAYLOAD_TYPE,
            payload_bytes,
            signature,
        )
    {
        return Err("admission_credential_invalid_signature".to_owned());
    }
    if unix_seconds > payload.expires_at {
        return Err("admission_credential_expired".to_owned());
    }
    if is_bot_namespace(&payload.login_name) && !payload.bot_tool_context {
        return Err("bot_namespace_admission_forbidden".to_owned());
    }
    Ok(payload)
}

/// Issues a bot-tool credential for the suite launcher. Private seed stays in process.
#[must_use]
pub fn issue_bot_tool_credential(
    private_seed: &[u8; 32],
    issued_at: u64,
    expires_at: u64,
    tool_id: &str,
) -> String {
    let mut nonce = [0_u8; NONCE_LEN];
    fill_random(&mut nonce);
    let mut writer = BinWriter::new();
    writer.write_u16(BOT_TOOL_PAYLOAD_VERSION);
    writer.write_ascii(tool_id);
    writer.write_ascii(BOT_TOOL_SCOPE);
    writer.write_u64(issued_at);
    writer.write_u64(expires_at);
    writer.write_fixed(&nonce);
    let payload = writer.into_bytes();
    let signature = sign_typed(
        private_seed,
        BOT_TOOL_TRUST_DOMAIN,
        BOT_TOOL_PAYLOAD_TYPE,
        &payload,
    );
    let mut framed = payload;
    framed.extend_from_slice(&signature);
    base64url_encode(&framed)
}

/// Test helper: issues an admission credential with the same framing as Account Server.
#[must_use]
pub fn issue_admission_credential(
    private_seed: &[u8; 32],
    key_id: u8,
    account_id: &str,
    login_name: &str,
    bot_tool_context: bool,
    issued_at: u64,
    expires_at: u64,
) -> String {
    let mut nonce = [0_u8; NONCE_LEN];
    fill_random(&mut nonce);
    let mut writer = BinWriter::new();
    writer.write_u16(ADMISSION_PAYLOAD_VERSION);
    writer.write_u8(key_id);
    writer.write_ascii(account_id);
    writer.write_ascii(login_name);
    writer.write_u8(u8::from(bot_tool_context));
    writer.write_u64(issued_at);
    writer.write_u64(expires_at);
    writer.write_fixed(&nonce);
    let payload = writer.into_bytes();
    let signature = sign_typed(
        private_seed,
        ADMISSION_TRUST_DOMAIN,
        ADMISSION_PAYLOAD_TYPE,
        &payload,
    );
    let mut framed = payload;
    framed.extend_from_slice(&signature);
    base64url_encode(&framed)
}

fn decode_admission(bytes: &[u8]) -> Option<AdmissionPayload> {
    let mut reader = BinReader::new(bytes);
    if reader.read_u16()? != ADMISSION_PAYLOAD_VERSION {
        return None;
    }
    let key_id = reader.read_u8()?;
    let account_id = reader.read_ascii()?;
    let login_name = reader.read_ascii()?;
    let bot = reader.read_u8()?;
    if bot > 1 {
        return None;
    }
    let issued_at = reader.read_u64()?;
    let expires_at = reader.read_u64()?;
    let _nonce = reader.read_fixed(NONCE_LEN)?;
    if reader.remaining() != 0 {
        return None;
    }
    Some(AdmissionPayload {
        key_id,
        account_id,
        login_name,
        bot_tool_context: bot == 1,
        issued_at,
        expires_at,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn verify_accepts_a_fresh_signed_credential() {
        let keys = generate_keys();
        let wire = issue_admission_credential(
            &keys.seed,
            1,
            "acct_0123456789abcdef0123456789abcdef",
            "Bot01",
            true,
            1_000,
            2_000,
        );
        let payload = verify_admission(&wire, 1, &keys.public, 1_500).expect("valid");
        assert_eq!(payload.login_name, "Bot01");
        assert!(payload.bot_tool_context);
    }

    #[test]
    fn verify_rejects_wrong_password_material_as_bad_signature() {
        let keys = generate_keys();
        let other = generate_keys();
        let wire = issue_admission_credential(&keys.seed, 1, "acct_aa", "Player01", false, 1, 9);
        let error = verify_admission(&wire, 1, &other.public, 2).expect_err("wrong key");
        assert_eq!(error, "admission_credential_invalid_signature");
    }

    #[test]
    fn bot_namespace_without_tool_context_is_forbidden() {
        let keys = generate_keys();
        let wire = issue_admission_credential(&keys.seed, 1, "acct_aa", "Bot01", false, 1, 9);
        let error = verify_admission(&wire, 1, &keys.public, 2).expect_err("forbidden");
        assert_eq!(error, "bot_namespace_admission_forbidden");
    }

    #[test]
    fn expired_credential_is_rejected() {
        let keys = generate_keys();
        let wire = issue_admission_credential(&keys.seed, 1, "acct_aa", "Player01", false, 1, 5);
        let error = verify_admission(&wire, 1, &keys.public, 6).expect_err("expired");
        assert_eq!(error, "admission_credential_expired");
    }
}
