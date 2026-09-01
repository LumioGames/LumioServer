//! LumioBin + LumioSignatureV1 + Ed25519 helpers. No secrets are logged.

use ed25519_compact::{PublicKey, Seed, Signature};
use sha2::{Digest, Sha256};

pub const SEED_LEN: usize = 32;
pub const PUBLIC_KEY_LEN: usize = 32;
pub const SIGNATURE_LEN: usize = 64;
pub const NONCE_LEN: usize = 16;

pub const ADMISSION_TRUST_DOMAIN: &str = "account-admission";
pub const ADMISSION_PAYLOAD_TYPE: &str = "admission-credential-v1";
pub const ADMISSION_PAYLOAD_VERSION: u16 = 1;
pub const BOT_TOOL_TRUST_DOMAIN: &str = "bot-tool";
pub const BOT_TOOL_PAYLOAD_TYPE: &str = "bot-tool-credential-v1";
pub const BOT_TOOL_PAYLOAD_VERSION: u16 = 1;
pub const BOT_TOOL_SCOPE: &str = "bot-namespace";

/// 32-byte seed plus derived public key.
#[derive(Clone)]
pub struct KeyPair {
    pub seed: [u8; SEED_LEN],
    pub public: [u8; PUBLIC_KEY_LEN],
}

/// Fills `out` from the OS entropy source.
///
/// # Panics
///
/// Panics when the platform CSPRNG fails.
pub fn fill_random(out: &mut [u8]) {
    getrandom::fill(out).expect("OS entropy");
}

/// Generates an Ed25519 seed keypair.
#[must_use]
pub fn generate_keypair() -> KeyPair {
    let mut seed = [0_u8; SEED_LEN];
    loop {
        fill_random(&mut seed);
        if seed.iter().any(|byte| *byte != 0) {
            break;
        }
    }
    KeyPair {
        public: public_from_seed(&seed),
        seed,
    }
}

fn keypair_from_seed(seed: &[u8; SEED_LEN]) -> ed25519_compact::KeyPair {
    ed25519_compact::KeyPair::from_seed(Seed::new(*seed))
}

/// Derives the 32-byte public key from a 32-byte seed.
#[must_use]
pub fn public_from_seed(seed: &[u8; SEED_LEN]) -> [u8; PUBLIC_KEY_LEN] {
    let public = keypair_from_seed(seed).pk;
    let mut out = [0_u8; PUBLIC_KEY_LEN];
    out.copy_from_slice(public.as_ref());
    out
}

/// Signs `preimage` with the seed.
#[must_use]
pub fn sign(seed: &[u8; SEED_LEN], preimage: &[u8]) -> [u8; SIGNATURE_LEN] {
    let signature = keypair_from_seed(seed).sk.sign(preimage, None);
    let mut out = [0_u8; SIGNATURE_LEN];
    out.copy_from_slice(signature.as_ref());
    out
}

/// Verifies `signature` over `preimage`.
#[must_use]
pub fn verify(public: &[u8], preimage: &[u8], signature: &[u8]) -> bool {
    let Ok(pk) = PublicKey::from_slice(public) else {
        return false;
    };
    let Ok(sig) = Signature::from_slice(signature) else {
        return false;
    };
    pk.verify(preimage, &sig).is_ok()
}

/// LumioSignatureV1 preimage: prefix, trust domain, payload type, SHA-256 hex.
#[must_use]
pub fn signature_preimage(trust_domain: &str, payload_type: &str, payload: &[u8]) -> Vec<u8> {
    let digest = hex_lower(&Sha256::digest(payload));
    let mut preimage = Vec::with_capacity(
        "LumioSignatureV1".len()
            + 1
            + trust_domain.len()
            + 1
            + payload_type.len()
            + 1
            + digest.len(),
    );
    preimage.extend_from_slice(b"LumioSignatureV1");
    preimage.push(0);
    preimage.extend_from_slice(trust_domain.as_bytes());
    preimage.push(0);
    preimage.extend_from_slice(payload_type.as_bytes());
    preimage.push(0);
    preimage.extend_from_slice(digest.as_bytes());
    preimage
}

/// Signs a typed payload.
#[must_use]
pub fn sign_typed(
    seed: &[u8; SEED_LEN],
    trust_domain: &str,
    payload_type: &str,
    payload: &[u8],
) -> [u8; SIGNATURE_LEN] {
    sign(
        seed,
        &signature_preimage(trust_domain, payload_type, payload),
    )
}

/// Verifies a typed payload.
#[must_use]
pub fn verify_typed(
    public: &[u8],
    trust_domain: &str,
    payload_type: &str,
    payload: &[u8],
    signature: &[u8],
) -> bool {
    verify(
        public,
        &signature_preimage(trust_domain, payload_type, payload),
        signature,
    )
}

/// Lowercase hex.
#[must_use]
pub fn hex_lower(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        let _ = write!(out, "{byte:02x}");
    }
    out
}

/// Base64url without padding.
#[must_use]
pub fn base64url_encode(bytes: &[u8]) -> String {
    const TABLE: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    let mut out = String::new();
    let mut iter = bytes.chunks_exact(3);
    for chunk in iter.by_ref() {
        let n = (u32::from(chunk[0]) << 16) | (u32::from(chunk[1]) << 8) | u32::from(chunk[2]);
        out.push(TABLE[((n >> 18) & 63) as usize] as char);
        out.push(TABLE[((n >> 12) & 63) as usize] as char);
        out.push(TABLE[((n >> 6) & 63) as usize] as char);
        out.push(TABLE[(n & 63) as usize] as char);
    }
    let rem = iter.remainder();
    if rem.len() == 1 {
        let n = u32::from(rem[0]) << 16;
        out.push(TABLE[((n >> 18) & 63) as usize] as char);
        out.push(TABLE[((n >> 12) & 63) as usize] as char);
    } else if rem.len() == 2 {
        let n = (u32::from(rem[0]) << 16) | (u32::from(rem[1]) << 8);
        out.push(TABLE[((n >> 18) & 63) as usize] as char);
        out.push(TABLE[((n >> 12) & 63) as usize] as char);
        out.push(TABLE[((n >> 6) & 63) as usize] as char);
    }
    out
}

/// Decode base64url without requiring padding.
#[must_use]
pub fn base64url_decode(text: &str) -> Option<Vec<u8>> {
    if text.is_empty() {
        return Some(Vec::new());
    }
    let mut padded = text.replace('-', "+").replace('_', "/");
    while !padded.len().is_multiple_of(4) {
        padded.push('=');
    }
    let mut out = Vec::new();
    let bytes = padded.as_bytes();
    for chunk in bytes.chunks(4) {
        let a = b64_val(chunk[0])?;
        let b = b64_val(chunk[1])?;
        let c = if chunk[2] == b'=' {
            0
        } else {
            b64_val(chunk[2])?
        };
        let d = if chunk[3] == b'=' {
            0
        } else {
            b64_val(chunk[3])?
        };
        out.push((a << 2) | (b >> 4));
        if chunk[2] != b'=' {
            out.push((b << 4) | (c >> 2));
        }
        if chunk[3] != b'=' {
            out.push((c << 6) | d);
        }
    }
    Some(out)
}

fn b64_val(byte: u8) -> Option<u8> {
    match byte {
        b'A'..=b'Z' => Some(byte - b'A'),
        b'a'..=b'z' => Some(byte - b'a' + 26),
        b'0'..=b'9' => Some(byte - b'0' + 52),
        b'+' => Some(62),
        b'/' => Some(63),
        _ => None,
    }
}

/// Little-endian LumioBin writer.
#[derive(Default)]
pub struct BinWriter {
    bytes: Vec<u8>,
}

impl BinWriter {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    pub fn write_u8(&mut self, value: u8) {
        self.bytes.push(value);
    }

    pub fn write_u16(&mut self, value: u16) {
        self.bytes.extend_from_slice(&value.to_le_bytes());
    }

    pub fn write_u64(&mut self, value: u64) {
        self.bytes.extend_from_slice(&value.to_le_bytes());
    }

    pub fn write_ascii(&mut self, value: &str) {
        let payload = value.as_bytes();
        self.bytes
            .extend_from_slice(&u32::try_from(payload.len()).unwrap_or(0).to_le_bytes());
        self.bytes.extend_from_slice(payload);
    }

    pub fn write_fixed(&mut self, value: &[u8]) {
        self.bytes.extend_from_slice(value);
    }

    #[must_use]
    pub fn into_bytes(self) -> Vec<u8> {
        self.bytes
    }
}

/// Little-endian LumioBin reader.
pub struct BinReader<'a> {
    bytes: &'a [u8],
    offset: usize,
}

impl<'a> BinReader<'a> {
    #[must_use]
    pub fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, offset: 0 }
    }

    #[must_use]
    pub fn remaining(&self) -> usize {
        self.bytes.len().saturating_sub(self.offset)
    }

    pub fn read_u8(&mut self) -> Option<u8> {
        let value = *self.bytes.get(self.offset)?;
        self.offset += 1;
        Some(value)
    }

    pub fn read_u16(&mut self) -> Option<u16> {
        let slice = self.bytes.get(self.offset..self.offset + 2)?;
        self.offset += 2;
        Some(u16::from_le_bytes(slice.try_into().ok()?))
    }

    pub fn read_u64(&mut self) -> Option<u64> {
        let slice = self.bytes.get(self.offset..self.offset + 8)?;
        self.offset += 8;
        Some(u64::from_le_bytes(slice.try_into().ok()?))
    }

    pub fn read_ascii(&mut self) -> Option<String> {
        let len = self.read_u32()? as usize;
        let slice = self.bytes.get(self.offset..self.offset + len)?;
        if slice.iter().any(|byte| *byte > 0x7f) {
            return None;
        }
        self.offset += len;
        String::from_utf8(slice.to_vec()).ok()
    }

    pub fn read_fixed(&mut self, len: usize) -> Option<Vec<u8>> {
        let slice = self.bytes.get(self.offset..self.offset + len)?;
        self.offset += len;
        Some(slice.to_vec())
    }

    fn read_u32(&mut self) -> Option<u32> {
        let slice = self.bytes.get(self.offset..self.offset + 4)?;
        self.offset += 4;
        Some(u32::from_le_bytes(slice.try_into().ok()?))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn base64url_roundtrip_without_padding() {
        let data = b"\x01\x02\x03\x04\x05";
        let encoded = base64url_encode(data);
        assert!(!encoded.contains('='));
        assert_eq!(base64url_decode(&encoded).as_deref(), Some(data.as_slice()));
    }

    #[test]
    fn typed_signature_roundtrip() {
        let keys = generate_keypair();
        let payload = b"payload";
        let signature = sign_typed(
            &keys.seed,
            ADMISSION_TRUST_DOMAIN,
            ADMISSION_PAYLOAD_TYPE,
            payload,
        );
        assert!(verify_typed(
            &keys.public,
            ADMISSION_TRUST_DOMAIN,
            ADMISSION_PAYLOAD_TYPE,
            payload,
            &signature
        ));
        assert!(!verify_typed(
            &keys.public,
            ADMISSION_TRUST_DOMAIN,
            ADMISSION_PAYLOAD_TYPE,
            b"other",
            &signature
        ));
    }
}
