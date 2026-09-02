//! Architecture bans for R-00374: consume-only Rust host.

use std::fs;
use std::path::{Path, PathBuf};

fn process_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
}

fn collect_text_files(dir: &Path, out: &mut Vec<PathBuf>) {
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };
    for entry in entries.filter_map(Result::ok) {
        let path = entry.path();
        if path.is_dir() {
            collect_text_files(&path, out);
            continue;
        }
        if path
            .extension()
            .and_then(|ext| ext.to_str())
            .is_some_and(|ext| matches!(ext, "rs" | "cs" | "mjs" | "js" | "yml" | "md"))
        {
            out.push(path);
        }
    }
}

fn read_owned_sources() -> Vec<(PathBuf, String)> {
    let mut files = Vec::new();
    collect_text_files(&process_root().join("src/entity_chat"), &mut files);
    collect_text_files(&process_root().join("tests"), &mut files);
    collect_text_files(
        &process_root()
            .parent()
            .expect("modules")
            .parent()
            .expect("repo")
            .join("entity-chat-host/src"),
        &mut files,
    );
    files
        .into_iter()
        .filter(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .is_none_or(|name| name != "entity_chat_architecture.rs")
        })
        .filter_map(|path| {
            fs::read_to_string(&path)
                .ok()
                .map(|text| (path, text.replace('\\', "/")))
        })
        .collect()
}

#[test]
fn host_src_has_no_private_binding_issue_query_or_expire_due() {
    let host = fs::read_to_string(process_root().join("src/entity_chat/host.rs")).expect("host.rs");
    for banned in [
        "by_account",
        "read_attribute",
        "expire_due",
        "next_net_entity_id",
        "instance_key",
        "tombstones",
    ] {
        assert!(
            !host.contains(banned),
            "host.rs must not contain `{banned}`"
        );
    }
}

#[test]
fn process_src_grep_bans_are_empty() {
    let mut files = Vec::new();
    collect_text_files(&process_root().join("src"), &mut files);
    let mut hits = Vec::new();
    for path in files {
        let text = fs::read_to_string(&path).expect("read");
        for banned in [
            "by_account",
            "read_attribute",
            "expire_due",
            "next_net_entity_id",
        ] {
            if text.contains(banned) {
                hits.push(format!("{}:{banned}", path.display()));
            }
        }
    }
    assert!(
        hits.is_empty(),
        "banned host-owned symbols still present: {hits:?}"
    );
}

#[test]
fn host_entry_restore_persist_uses_readonly_memory() {
    let path = process_root()
        .parent()
        .expect("modules")
        .parent()
        .expect("repo")
        .join("entity-chat-host/src/Lumio.Server.EntityChat.HostEntry/HostEntry.cs");
    let text = fs::read_to_string(&path).expect("HostEntry.cs");
    assert!(
        text.contains("ReadOnlyMemory"),
        "RestorePersist is ReadOnlyMemory<byte> on the Runtime public surface"
    );
    assert!(
        !text.contains("RestorePersist\", new[] { world.GetType(), typeof(byte[]) }"),
        "must not invoke RestorePersist(EcsWorld, byte[]) — that overload does not exist"
    );
}

#[test]
fn suite_connection_superseded_received_must_not_copy_takeover() {
    let text =
        fs::read_to_string(process_root().join("src/entity_chat/suite.rs")).expect("suite.rs");
    assert!(
        !text.contains("\"connectionSupersededReceived\": takeover"),
        "connectionSupersededReceived must come from old-socket recv, not host takeover"
    );
    assert!(
        text.contains("RoomClient::connect") && text.contains("c-bot100"),
        "S8 must attach c-bot100 as a RoomClient before takeover"
    );
    assert!(
        text.contains("ConnectionSuperseded"),
        "S8 must recv messageType=ConnectionSuperseded on the old socket"
    );
}

#[test]
fn suite_attaches_c_browser_room_ws_before_chat_burst() {
    let text =
        fs::read_to_string(process_root().join("src/entity_chat/suite.rs")).expect("suite.rs");
    let browser = text
        .find("RoomClient::connect(&listen_uri, \"c-browser\")")
        .or_else(|| text.find("RoomClient::connect(&host.listen_uri(), \"c-browser\")"))
        .expect("suite must attach c-browser as a RoomClient");
    let burst = text
        .find("run_client_bot_fleet(")
        .or_else(|| text.find("spawn_client_bot_host("))
        .expect("chat burst must spawn Client Bot.Host, not a host-admit loop");
    assert!(
        browser < burst,
        "c-browser Room WS must be attached before Client Bot utterances"
    );
}

#[test]
fn suite_playwright_ran_requires_browser_room_observation() {
    let suite =
        fs::read_to_string(process_root().join("src/entity_chat/suite.rs")).expect("suite.rs");
    let browser =
        fs::read_to_string(process_root().join("src/entity_chat/browser.rs")).expect("browser.rs");
    assert!(
        browser.contains("room=") || suite.contains("room="),
        "Playwright page URL must include the Room listen URI so the browser joins before chats"
    );
    assert!(
        suite.contains("playwright_ran")
            && (suite.contains("received_from_network")
                || suite.contains("receivedFromNetwork")
                || suite.contains("playwright_ran()")),
        "S3 playwrightRan must come from Playwright Room observation, not account-login-only"
    );
    assert!(
        !suite.contains("\"playwrightRan\": true"),
        "must not hard-code playwrightRan true"
    );
}

#[test]
fn suite_unauthorized_query_uses_claimed_mark_not_undeclared_flag() {
    let text =
        fs::read_to_string(process_root().join("src/entity_chat/suite.rs")).expect("suite.rs");
    assert!(
        text.contains("EntityIdentity.claimedMark"),
        "S5 unauthorized must query Runtime claim-scoped EntityIdentity.claimedMark"
    );
    assert!(
        !text.contains("EntityIdentity.restrictedFlag"),
        "restrictedFlag is undeclared and maps to RequestError, not contract Unauthorized"
    );
}

#[test]
fn host_entry_resolve_forwards_ok_entity_as_binding() {
    let path = process_root()
        .parent()
        .expect("modules")
        .parent()
        .expect("repo")
        .join("entity-chat-host/src/Lumio.Server.EntityChat.HostEntry/HostEntry.cs");
    let text = fs::read_to_string(&path).expect("HostEntry.cs");
    assert!(
        text.contains("ListBindings") && text.contains("ResolveByNetEntityId"),
        "Resolve OkEntity has no Binding; HostEntry must attach the listed ConnectionBinding"
    );
    assert!(
        text.contains("x32") || text.contains("NormalizeNetEntityId"),
        "Resolve must accept C-1 u64 and Runtime 32-hex NetEntityId"
    );
}

#[test]
fn suite_schedules_kernel_tick_every_max_chat_inputs() {
    let text =
        fs::read_to_string(process_root().join("src/entity_chat/suite.rs")).expect("suite.rs");
    assert!(
        text.contains("pending_chats >= MAX_CHAT_INPUTS_PER_TICK"),
        "suite must insert schedule_room_tick every 64 admits, not dump 101 into one RunTick"
    );
    let ticks = text.matches("schedule_room_tick").count();
    assert!(
        ticks >= 2,
        "suite must schedule at least a batch tick and a remainder tick, got {ticks}"
    );
}

#[test]
fn suite_discovers_client_bot_host_via_env_or_sibling() {
    let bots = fs::read_to_string(process_root().join("src/entity_chat/bots.rs")).expect("bots.rs");
    let suite =
        fs::read_to_string(process_root().join("src/entity_chat/suite.rs")).expect("suite.rs");
    let blob = format!("{bots}\n{suite}");
    assert!(
        blob.contains("LUMIO_CLIENT_ROOT") && blob.contains("LUMIO_BOT_HOST"),
        "Client Bot.Host must be discovered via LUMIO_CLIENT_ROOT / LUMIO_BOT_HOST"
    );
    assert!(
        blob.contains("LumioClient") && blob.contains("Lumio.Client.Bot.Host"),
        "missing Bot.Host must fall back to a repo-relative sibling, never a hardcoded machine path"
    );
}

#[test]
fn suite_spawns_lumio_client_bot_host() {
    let bots = fs::read_to_string(process_root().join("src/entity_chat/bots.rs")).expect("bots.rs");
    assert!(
        bots.contains("Lumio.Client.Bot.Host")
            && (bots.contains("DOTNET_STARTUP_HOOKS") || bots.contains("dotnet")),
        "suite must spawn Lumio.Client.Bot.Host as a child process"
    );
    assert!(
        bots.contains("ClientTimerManager"),
        "spawned Bot.Host must drain ClientTimerManager, not a second timer"
    );
}

#[test]
fn suite_s6_tick_source_is_native_kernel_tick_frame() {
    let suite =
        fs::read_to_string(process_root().join("src/entity_chat/suite.rs")).expect("suite.rs");
    assert!(
        suite.contains("native-kernel/tickFrame"),
        "S6 tickSource must be native-kernel/tickFrame from Client Timer Manager"
    );
    assert!(
        !suite.contains("\"tickSource\": if timer_ok { \"kernel:tickFrame\""),
        "must not impersonate Client Timer Manager with host kernel:tickFrame"
    );
}

#[test]
fn suite_s6_utterance_ticks_come_from_client_timer_drain() {
    let suite =
        fs::read_to_string(process_root().join("src/entity_chat/suite.rs")).expect("suite.rs");
    let bots = fs::read_to_string(process_root().join("src/entity_chat/bots.rs")).expect("bots.rs");
    let blob = format!("{suite}\n{bots}");
    assert!(
        blob.contains("utteranceTicks") || blob.contains("utterance_ticks"),
        "S6 must record Client Timer Manager utteranceTicks"
    );
    assert!(
        !suite.contains("vec![5, 10, 15]")
            && !suite.contains("vec![5,10,15]")
            && !suite.contains("[5, 10, 15]")
            && !suite.contains("[5,10,15]"),
        "must not hard-code Client Timer ticks 5,10,15 in suite evidence"
    );
}

#[test]
fn suite_chat_burst_does_not_host_admit_bot_utterances() {
    let text =
        fs::read_to_string(process_root().join("src/entity_chat/suite.rs")).expect("suite.rs");
    assert!(
        !text.contains("admit_chat_input(connection.clone()"),
        "101 bot chats must come from Client Bot.Host over Room WS, not host.admit_chat_input"
    );
    assert!(
        text.contains("write_blocked") && text.contains("discover_bot_host"),
        "missing Client Bot.Host must BLOCKED rather than skip"
    );
}

#[test]
fn owned_sources_have_no_hardcoded_dev_machine_paths() {
    let mut hits = Vec::new();
    for (path, text) in read_owned_sources() {
        if text.contains("C:/Work") || text.contains("C:/Users") {
            hits.push(path.display().to_string());
        }
    }
    assert!(
        hits.is_empty(),
        "hardcoded C:/Work or C:/Users paths remain in {hits:?}"
    );
}
