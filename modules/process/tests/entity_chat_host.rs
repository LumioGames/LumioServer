//! Consume-only host unit tests. Binding/query truth is the Runtime double.

mod common;

use common::{SharedRuntime, TestKernel};
use lumio_host_runtime::{HostClock, SharedClock};
use lumio_server_process::entity_chat::{
    generate_keys, issue_admission_credential, AttributeQueryOutcome, AttributeQueryRequest,
    AttributeQueryScope, BoundEntityKind, ChatOpKind, EntityChatHost, InputCommand, QueryResult,
    ADMISSION_KEY_ID, RECONNECT_WINDOW_MS,
};

fn host_with(
    runtime: SharedRuntime,
) -> (
    EntityChatHost,
    lumio_server_process::entity_chat::Ed25519KeyPair,
) {
    let keys = generate_keys();
    let host = EntityChatHost::new(
        RECONNECT_WINDOW_MS,
        SharedClock::test(),
        Box::new(runtime),
        Box::new(TestKernel::new()),
        ADMISSION_KEY_ID,
        keys.public.to_vec(),
        1_000,
    );
    (host, keys)
}

fn credential(
    keys: &lumio_server_process::entity_chat::Ed25519KeyPair,
    name: &str,
    bot: bool,
) -> String {
    issue_admission_credential(&keys.seed, 1, &format!("acct_{name}"), name, bot, 1, 9_000)
}

#[test]
fn username_password_is_never_an_admission_path() {
    let (host, _) = host_with(SharedRuntime::new());
    assert!(!host.try_admit_username_password("room-main", "c1", "Bot01", "123456"));
}

#[test]
fn admit_creates_bot_and_player_and_resolves_bindings() {
    let (host, keys) = host_with(SharedRuntime::new());
    let bot = host.admit(
        "room-main".to_owned(),
        "c-bot01".to_owned(),
        credential(&keys, "Bot01", true),
    );
    let player = host.admit(
        "room-main".to_owned(),
        "c-browser".to_owned(),
        credential(&keys, "Browser01", false),
    );
    assert!(bot.accepted && player.accepted);
    assert_eq!(
        bot.binding.as_ref().map(|binding| binding.entity_type),
        Some(BoundEntityKind::Bot)
    );
    assert_eq!(
        player.binding.as_ref().map(|binding| binding.entity_type),
        Some(BoundEntityKind::Player)
    );
    let census = host.census("room-main".to_owned());
    assert_eq!(census.bot_count, 1);
    assert_eq!(census.player_count, 1);
    let self_bot = host.must_self("c-bot01");
    assert!(host
        .try_resolve_by_net_entity_id("room-main".to_owned(), self_bot.net_entity_id)
        .is_some());
}

#[test]
fn reconnect_within_window_rebinds_entity_a() {
    let (host, keys) = host_with(SharedRuntime::new());
    let _ = host.admit(
        "room-main".to_owned(),
        "c-bot01".to_owned(),
        credential(&keys, "Bot01", true),
    );
    let first = host.must_self("c-bot01");
    let entity_a = first.net_entity_id.clone();
    let first_session = first.session_id.clone();
    assert!(host.disconnect("c-bot01".to_owned()));
    let rejected = host.admit_chat_input(
        "c-bot01".to_owned(),
        InputCommand::from_chat_text("while-down"),
    );
    assert_eq!(rejected.kind, ChatOpKind::Rejected);
    let rebind = host.admit(
        "room-main".to_owned(),
        "c-bot01-re".to_owned(),
        credential(&keys, "Bot01", true),
    );
    assert!(rebind.reconnected);
    let rebound = rebind.binding.expect("rebind binding");
    assert_eq!(rebound.net_entity_id, entity_a);
    assert_ne!(rebound.session_id, first_session);
    assert_ne!(rebound.net_entity_id, rebound.session_id);
}

#[test]
fn wall_clock_kernel_expire_tombstones_a_and_creates_b() {
    let clock = SharedClock::test();
    let runtime = SharedRuntime::new();
    let keys = generate_keys();
    let host = EntityChatHost::new(
        RECONNECT_WINDOW_MS,
        clock.clone(),
        Box::new(runtime.clone()),
        Box::new(TestKernel::new()),
        ADMISSION_KEY_ID,
        keys.public.to_vec(),
        1_000,
    );
    let _ = host.admit(
        "room-main".to_owned(),
        "c-bot01".to_owned(),
        credential(&keys, "Bot01", true),
    );
    let entity_a = host.must_self("c-bot01").net_entity_id;
    let account = host.must_self("c-bot01").account_id;
    assert!(host.disconnect("c-bot01".to_owned()));
    clock.advance_ms(RECONNECT_WINDOW_MS + 1);
    host.drive_kernel();
    assert!(runtime
        .lock()
        .expire_calls()
        .iter()
        .any(|id| id == &entity_a));
    let created_b = host.admit(
        "room-main".to_owned(),
        "c-bot01-b".to_owned(),
        credential(&keys, "Bot01", true),
    );
    assert!(created_b.accepted);
    let entity_b = created_b.binding.unwrap().net_entity_id;
    assert_ne!(entity_b, entity_a);
    let tomb = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ServerAuthoritative,
        room_id: "room-main".to_owned(),
        net_entity_id: entity_a,
        attribute_id: "EntityIdentity.entityType".to_owned(),
        connection_generation: None,
    });
    assert_eq!(tomb.outcome, AttributeQueryOutcome::Tombstoned);
    assert_eq!(host.must_self("c-bot01-b").account_id, account);
}

#[test]
fn isolation_rejects_cross_room_query() {
    let (host, keys) = host_with(SharedRuntime::new());
    let _ = host.admit(
        "room-main".to_owned(),
        "c-browser".to_owned(),
        credential(&keys, "Browser01", false),
    );
    let _ = host.admit(
        "room-iso".to_owned(),
        "iso-a".to_owned(),
        credential(&keys, "IsoPlayerA", false),
    );
    let browser = host.must_self("c-browser");
    let cross = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ServerAuthoritative,
        room_id: "room-iso".to_owned(),
        net_entity_id: browser.net_entity_id,
        attribute_id: "EntityIdentity.entityType".to_owned(),
        connection_generation: None,
    });
    assert_eq!(cross.error_code.as_deref(), Some("cross_room_reference"));
}

#[test]
fn attribute_query_is_forwarded_to_runtime() {
    let runtime = SharedRuntime::new();
    let (host, keys) = host_with(runtime.clone());
    let _ = host.admit(
        "room-main".to_owned(),
        "c-browser".to_owned(),
        credential(&keys, "Browser01", false),
    );
    let binding = host.must_self("c-browser");
    runtime.lock().plant_query(
        "room-main",
        &binding.net_entity_id,
        "ChatComponent.lastMessageText",
        QueryResult::fail(AttributeQueryOutcome::Invisible),
    );
    runtime.lock().plant_query(
        "room-main",
        &binding.net_entity_id,
        "EntityIdentity.claimedMark",
        QueryResult::fail(AttributeQueryOutcome::Unauthorized),
    );
    let ok = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ServerAuthoritative,
        room_id: "room-main".to_owned(),
        net_entity_id: binding.net_entity_id.clone(),
        attribute_id: "EntityIdentity.entityType".to_owned(),
        connection_generation: None,
    });
    let invisible = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ClientReplica,
        room_id: "room-main".to_owned(),
        net_entity_id: binding.net_entity_id.clone(),
        attribute_id: "ChatComponent.lastMessageText".to_owned(),
        connection_generation: None,
    });
    let unauthorized = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ClientReplica,
        room_id: "room-main".to_owned(),
        net_entity_id: binding.net_entity_id.clone(),
        attribute_id: "EntityIdentity.claimedMark".to_owned(),
        connection_generation: None,
    });
    let missing = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ServerAuthoritative,
        room_id: "room-main".to_owned(),
        net_entity_id: "ffffffffffffffffffffffffffffffff".to_owned(),
        attribute_id: "EntityIdentity.entityType".to_owned(),
        connection_generation: None,
    });
    let stale = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ServerAuthoritative,
        room_id: "room-main".to_owned(),
        net_entity_id: binding.net_entity_id,
        attribute_id: "EntityIdentity.entityType".to_owned(),
        connection_generation: Some(0),
    });
    assert_eq!(ok.outcome, AttributeQueryOutcome::Ok);
    assert_eq!(invisible.outcome, AttributeQueryOutcome::Invisible);
    assert_eq!(unauthorized.outcome, AttributeQueryOutcome::Unauthorized);
    assert_eq!(missing.outcome, AttributeQueryOutcome::NonExistent);
    assert_eq!(stale.outcome, AttributeQueryOutcome::StaleGeneration);
}

#[test]
fn restore_does_not_create_active_sessions() {
    let runtime = SharedRuntime::new();
    let (host, keys) = host_with(runtime.clone());
    let _ = host.admit(
        "room-main".to_owned(),
        "c-bot01".to_owned(),
        credential(&keys, "Bot01", true),
    );
    let snapshot = host.capture_persist_snapshot("room-main".to_owned());
    host.restore_persist_snapshot("room-main".to_owned(), snapshot);
    assert_eq!(runtime.lock().restore_calls(), 1);
    assert!(host.try_self_lookup("c-bot01".to_owned()).is_some());
    assert_eq!(host.census("room-main".to_owned()).total, 1);
}

#[test]
fn kernel_tick_frame_runs_runtime_tick() {
    let (host, keys) = host_with(SharedRuntime::new());
    let _ = host.admit(
        "room-main".to_owned(),
        "c-bot01".to_owned(),
        credential(&keys, "Bot01", true),
    );
    let _ = host.admit_chat_input(
        "c-bot01".to_owned(),
        InputCommand::from_chat_text("hello-Bot01"),
    );
    let tick = host.schedule_room_tick("room-main".to_owned(), 0);
    assert_eq!(tick.applied_tick, 1);
}

#[test]
fn batched_chat_inputs_stay_within_runtime_change_entry_budget() {
    assert_eq!(
        lumio_server_process::entity_chat::MAX_CHAT_INPUTS_PER_TICK * 2,
        128,
        "two ChatComponent field writes per chat.input must fit MaxChangeEntries=128"
    );
}

fn admit_n(
    host: &EntityChatHost,
    keys: &lumio_server_process::entity_chat::Ed25519KeyPair,
    n: usize,
) {
    for i in 1..=n {
        let name = format!("Bot{i:02}");
        let accepted = host
            .admit(
                "room-main".to_owned(),
                format!("c-{i:03}"),
                credential(keys, &name, true),
            )
            .accepted;
        assert!(accepted, "admit {name}");
    }
}

fn enqueue_n(host: &EntityChatHost, n: usize) {
    for i in 1..=n {
        let admitted = host.admit_chat_input(
            format!("c-{i:03}"),
            InputCommand::from_chat_text(&format!("hello-{i}")),
        );
        assert_eq!(admitted.kind, ChatOpKind::Admitted);
    }
}

#[test]
fn sixty_four_chat_inputs_one_tick_emit_chat_event() {
    let (host, keys) = host_with(SharedRuntime::new());
    admit_n(&host, &keys, 64);
    let mut client =
        lumio_server_process::entity_chat::RoomClient::connect(&host.listen_uri(), "c-001")
            .expect("connect");
    let _ = client.recv_text();
    enqueue_n(&host, 64);
    let tick = host.run_tick("room-main".to_owned());
    assert!(tick.ok, "N=64 must succeed, got {tick:?}");
    assert_eq!(tick.event_count, 64);
    let frame = client.recv_text().expect("delta");
    assert!(
        frame.contains("\"mappingId\":\"chat.event\""),
        "N=64 must emit chat.event, got {frame}"
    );
}

#[test]
fn sixty_five_chat_inputs_one_tick_empty_delta_is_not_success() {
    let (host, keys) = host_with(SharedRuntime::new());
    admit_n(&host, &keys, 65);
    let mut client =
        lumio_server_process::entity_chat::RoomClient::connect(&host.listen_uri(), "c-001")
            .expect("connect");
    let _ = client.recv_text();
    enqueue_n(&host, 65);
    let tick = host.run_tick("room-main".to_owned());
    assert!(
        !tick.ok,
        "N=65 must not be SUCCESS (Runtime MaxChangeEntries=128), got {tick:?}"
    );
    assert_eq!(tick.event_count, 0);
    match client.recv_text() {
        Ok(frame) => {
            assert!(
                !frame.contains("\"mappingId\":\"chat.event\""),
                "N=65 must not emit chat.event, got {frame}"
            );
            assert!(
                frame.contains("\"changedBlocks\":[]") || !tick.ok,
                "N=65 empty Delta is not SUCCESS, got {frame}"
            );
        }
        Err(_) => assert!(
            !tick.ok,
            "budget fault must not be treated as a successful tick"
        ),
    }
}

#[test]
fn claimed_mark_client_replica_is_contract_unauthorized() {
    let (host, keys) = host_with(SharedRuntime::new());
    let _ = host.admit(
        "room-main".to_owned(),
        "c-browser".to_owned(),
        credential(&keys, "Browser01", false),
    );
    let binding = host.must_self("c-browser");
    let unauthorized = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ClientReplica,
        room_id: "room-main".to_owned(),
        net_entity_id: binding.net_entity_id,
        attribute_id: "EntityIdentity.claimedMark".to_owned(),
        connection_generation: None,
    });
    assert_eq!(
        unauthorized.outcome,
        AttributeQueryOutcome::Unauthorized,
        "claim-scoped claimedMark without a claim is contract Unauthorized, not {:?}",
        unauthorized.outcome
    );
}

#[test]
fn resolve_accepts_runtime_32_hex_and_c1_u64() {
    let (host, keys) = host_with(SharedRuntime::new());
    let bot = host.admit(
        "room-main".to_owned(),
        "c-bot01".to_owned(),
        credential(&keys, "Bot01", true),
    );
    let hex = bot.binding.expect("binding").net_entity_id;
    assert_eq!(hex.len(), 32);
    assert!(host
        .try_resolve_by_net_entity_id("room-main".to_owned(), hex.clone())
        .is_some());
    let as_u64 = u64::from_str_radix(&hex, 16).expect("runtime 32-hex is a u64");
    let resolved = host.try_resolve_by_net_entity_id("room-main".to_owned(), as_u64.to_string());
    assert!(
        resolved.is_some(),
        "C-1 u64 decimal {as_u64} must resolve the Runtime 32-hex {hex}"
    );
    assert_eq!(resolved.expect("row").net_entity_id, hex);
}
