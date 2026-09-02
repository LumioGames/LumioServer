//! Slice-scoped entity-chat Rust host: clock, owner thread, bounded queues,
//! Account Server admission verify, Room world-slot, `CoreCLR` Runtime consume.
#![allow(
    clippy::chunks_exact_to_as_chunks,
    clippy::doc_markdown,
    clippy::double_must_use,
    clippy::manual_let_else,
    clippy::map_unwrap_or,
    clippy::missing_errors_doc,
    clippy::missing_panics_doc,
    clippy::must_use_candidate,
    clippy::needless_as_bytes,
    clippy::needless_pass_by_value,
    clippy::similar_names,
    clippy::single_match,
    clippy::struct_field_names,
    clippy::too_many_lines,
    clippy::while_let_loop
)]

mod account;
mod admission;
mod browser;
mod clr;
mod crypto;
mod discover;
mod envelope;
mod host;
mod runtime;
mod suite;
mod wire;

pub use account::{AccountLoginResult, AccountServerProcess};
pub use admission::{
    generate_keys, issue_admission_credential, issue_bot_tool_credential, verify_admission,
    AdmissionPayload, Ed25519KeyPair,
};
pub use clr::{ClrGameplay, ClrGameplayConfig};
pub use discover::{discover, ReplayArtifacts};
pub use envelope::{CommandBlock, InputCommand, CHAT_INPUT_MAPPING, MESSAGE_TYPE};
pub use host::{
    AdmitTrace, AttributeQueryRequest, ConnectionBinding, EntityChatHost, EntityResolution,
    RoomAdmitResult, RoomCensus, DISPATCH_EXPIRE, DISPATCH_TICK,
};
pub use runtime::{
    AttributeQueryOutcome, AttributeQueryScope, BoundEntityKind, ChatOpKind, ChatOperation,
    PersistRecord, QueryResult, RebindMode, RuntimeAdmit, RuntimeBinding, RuntimeQuery,
    RuntimeSurface, RuntimeTick,
};
pub use suite::{run_round, run_round_blocking, run_two_rounds, SuiteOptions, SuiteReport};
pub use wire::{RoomClient, RoomListener};

pub const MAIN_ROOM: &str = "room-main";
pub const ISO_ROOM: &str = "room-iso";
pub const BROWSER_NAME: &str = "Browser01";
pub const TEST_PASSWORD: &str = "123456";
pub const ADMISSION_KEY_ID: u8 = 1;
pub const RECONNECT_WINDOW_MS: u64 = 300_000;
pub const INGRESS_QUEUE_PER_CONNECTION: usize = 64;
/// Runtime `ChatIngressWorld` default `MaxChangeEntries` is 128; each chat.input
/// commits two ChatComponent fields, so one `RunTick` can take at most 64 chats.
pub const MAX_CHAT_INPUTS_PER_TICK: usize = 64;
pub const BOT_COUNT: u32 = 100;

/// Formats `Bot01`…`Bot100`.
#[must_use]
pub fn bot_name(index: u32) -> String {
    format!("Bot{index:02}")
}
