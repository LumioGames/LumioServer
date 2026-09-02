//! Room wire: client-received `FullSnapshot` / Delta / `ConnectionSuperseded`.

mod common;

use common::{delta_frame, snapshot_with_state_blocks, SharedRuntime, TestKernel};
use lumio_host_runtime::SharedClock;
use lumio_server_process::entity_chat::{
    generate_keys, issue_admission_credential, EntityChatHost, InputCommand, RoomClient,
    ADMISSION_KEY_ID, RECONNECT_WINDOW_MS,
};

fn credential(
    keys: &lumio_server_process::entity_chat::Ed25519KeyPair,
    name: &str,
    bot: bool,
) -> String {
    issue_admission_credential(&keys.seed, 1, &format!("acct_{name}"), name, bot, 1, 9_000)
}

fn host_ready(
    runtime: SharedRuntime,
) -> (
    EntityChatHost,
    lumio_server_process::entity_chat::Ed25519KeyPair,
) {
    let keys = generate_keys();
    runtime.lock().plant_snapshot(&snapshot_with_state_blocks());
    runtime
        .lock()
        .plant_delta(vec![delta_frame(1), delta_frame(2)]);
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

#[test]
fn admit_sends_full_snapshot_with_state_blocks_to_the_client() {
    let (host, keys) = host_ready(SharedRuntime::new());
    let admit = host.admit(
        "room-main".to_owned(),
        "c-bot01".to_owned(),
        credential(&keys, "Bot01", true),
    );
    assert!(admit.accepted);
    let mut client = RoomClient::connect(&host.listen_uri(), "c-bot01").expect("connect");
    let frame = client.recv_text().expect("snapshot");
    assert!(
        frame.contains("\"messageType\":\"FullSnapshot\""),
        "client must receive FullSnapshot, got {frame}"
    );
    assert!(
        frame.contains("\"stateBlocks\""),
        "FullSnapshot must include stateBlocks, got {frame}"
    );
}

#[test]
fn tick_broadcasts_runtime_delta_bytes_in_order() {
    let (host, keys) = host_ready(SharedRuntime::new());
    let _ = host.admit(
        "room-main".to_owned(),
        "c-a".to_owned(),
        credential(&keys, "Bot01", true),
    );
    let _ = host.admit(
        "room-main".to_owned(),
        "c-b".to_owned(),
        credential(&keys, "Bot02", true),
    );
    let mut a = RoomClient::connect(&host.listen_uri(), "c-a").expect("a");
    let mut b = RoomClient::connect(&host.listen_uri(), "c-b").expect("b");
    let _ = a.recv_text();
    let _ = b.recv_text();
    let _ = host.admit_chat_input("c-a".to_owned(), InputCommand::from_chat_text("one"));
    let tick = host.run_tick("room-main".to_owned());
    assert_eq!(tick.applied_tick, 1);
    let first_a = a.recv_text().expect("a1");
    let second_a = a.recv_text().expect("a2");
    let first_b = b.recv_text().expect("b1");
    let second_b = b.recv_text().expect("b2");
    assert!(first_a.contains("\"messageType\":\"Delta\""));
    assert!(second_a.contains("\"messageType\":\"Delta\""));
    assert_eq!(first_a, first_b);
    assert_eq!(second_a, second_b);
    assert_ne!(first_a, second_a);
}

#[test]
fn takeover_sends_connection_superseded_before_close() {
    let (host, keys) = host_ready(SharedRuntime::new());
    let first = host.admit(
        "room-main".to_owned(),
        "c-old".to_owned(),
        credential(&keys, "Bot01", true),
    );
    assert!(first.accepted);
    let mut old = RoomClient::connect(&host.listen_uri(), "c-old").expect("old");
    let _ = old.recv_text();
    let takeover = host.admit(
        "room-main".to_owned(),
        "c-new".to_owned(),
        credential(&keys, "Bot01", true),
    );
    assert!(takeover.takeover);
    let notice = old.recv_text().expect("superseded");
    assert!(
        notice.contains("\"messageType\":\"ConnectionSuperseded\""),
        "old client must receive ConnectionSuperseded first, got {notice}"
    );
    assert!(notice.contains("\"reasonCode\":\"connection_superseded\""));
    assert!(
        old.is_closed_after(),
        "old socket must close after ConnectionSuperseded"
    );
    let mut new_client = RoomClient::connect(&host.listen_uri(), "c-new").expect("new");
    let snapshot = new_client.recv_text().expect("new snapshot");
    assert!(snapshot.contains("stateBlocks"));
}
