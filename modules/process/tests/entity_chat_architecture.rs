//! Architecture bans for R-00374: consume-only Rust host.

use std::fs;
use std::path::{Path, PathBuf};

fn process_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
}

fn rust_fn_src<'a>(text: &'a str, marker: &str) -> &'a str {
    let start = text
        .find(marker)
        .unwrap_or_else(|| panic!("missing `{marker}`"));
    let after = &text[start..];
    let brace = after
        .find('{')
        .unwrap_or_else(|| panic!("no body for `{marker}`"));
    let mut depth = 0;
    for (index, ch) in after.char_indices().skip(brace) {
        match ch {
            '{' => depth += 1,
            '}' => {
                depth -= 1;
                if depth == 0 {
                    return &after[..=index];
                }
            }
            _ => {}
        }
    }
    panic!("unclosed `{marker}`");
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
        text.contains("CreateFromSnapshot")
            || text.contains("Restore(")
            || text.contains("ReadOnlyMemory"),
        "restore must use WorldManager.CreateFromSnapshot / ServerBootstrap.Restore"
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
        text.contains("ResolveByNetEntityId"),
        "Resolve must forward Runtime ResolveByNetEntityId"
    );
    assert!(
        !text.contains("ListedBinding("),
        "ListBindings must not overlay Runtime tombstoned/cross_room conclusions"
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
fn drain_and_apply_pending_must_not_block_past_a_deadline() {
    let suite =
        fs::read_to_string(process_root().join("src/entity_chat/suite.rs")).expect("suite.rs");
    let wire = fs::read_to_string(process_root().join("src/entity_chat/wire.rs")).expect("wire.rs");
    let drain = rust_fn_src(&suite, "fn drain_chat_event_deltas");
    let apply = rust_fn_src(&suite, "fn apply_pending_chat_ticks");
    let wait = rust_fn_src(&suite, "fn wait_for_observed_chat_events");
    assert!(
        drain.contains("try_recv_text") || drain.contains("WouldBlock"),
        "drain_chat_event_deltas must non-block or WouldBlock-stop; blocking recv_text is the live9 hang"
    );
    assert!(
        !drain.contains(".recv_text()"),
        "drain must not call blocking recv_text while waiting for 101 chat.event frames"
    );
    assert!(
        drain.contains("deadline") || wait.contains("deadline"),
        "observer drain/wait must stop at a deadline, not hang until dispatcher kill"
    );
    assert!(
        wire.contains("try_recv_text")
            && (wire.contains("set_nonblocking") || wire.contains("WouldBlock")),
        "RoomClient must expose a non-blocking recv so hold sockets cannot pin drain"
    );
    assert!(
        apply.contains("!tick.ok"),
        "apply_pending_chat_ticks must stop when tick.ok is false (65+ Runtime _faulted)"
    );
    assert!(
        apply.contains("pending_chats > MAX_CHAT_INPUTS_PER_TICK"),
        "apply_pending must not RunTick more than MAX_CHAT_INPUTS_PER_TICK chat.inputs"
    );
}

#[test]
fn max_chat_inputs_per_tick_stays_sixty_four() {
    let text = fs::read_to_string(process_root().join("src/entity_chat/mod.rs")).expect("mod.rs");
    assert!(
        text.contains("MAX_CHAT_INPUTS_PER_TICK: usize = 64"),
        "must not raise Runtime MaxChangeEntries / MAX_CHAT_INPUTS_PER_TICK"
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
        bots.contains("Lumio.Client.Bot.Host") && bots.contains("dotnet"),
        "suite must spawn Lumio.Client.Bot.Host as a child process"
    );
    assert!(
        bots.contains("--log-dir") || bots.contains("log_dir"),
        "spawned Bot.Host evidence must come from its log directory"
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
fn bot_host_must_not_exit_or_dispose_sockets_before_room_observes_chat_events() {
    let bots = fs::read_to_string(process_root().join("src/entity_chat/bots.rs")).expect("bots.rs");
    let suite =
        fs::read_to_string(process_root().join("src/entity_chat/suite.rs")).expect("suite.rs");
    let host = fs::read_to_string(process_root().join("src/entity_chat/host.rs")).expect("host.rs");
    let production_bots = bots
        .split("#[cfg(test)]")
        .next()
        .expect("production bots.rs");

    assert!(
        (production_bots.contains("release_path") || production_bots.contains("releasePath"))
            && (production_bots.contains("fn release")
                || production_bots.contains("fn release_mut")),
        "bots.rs must keep Lumio.Client.Bot.Host alive and expose an explicit suite release"
    );
    assert!(
        !suite.contains("sent.saturating_sub"),
        "suite must not schedule ticks from sent.txt; that races host receive"
    );
    assert!(
        host.contains("pending_wire_chat_inputs") && suite.contains("pending_wire_chat_inputs"),
        "tick budget must follow Room wire observation of chat.input, not Bot.Host sent.txt"
    );
    assert!(
        suite.contains("release(") && suite.contains("drain_chat_event_deltas"),
        "suite must hold the fleet until Room observed chat.event, then release"
    );
}

#[test]
fn generated_hook_build_isolates_from_parent_directory_build_props() {
    let bots = fs::read_to_string(process_root().join("src/entity_chat/bots.rs")).expect("bots.rs");
    let production = bots
        .split("#[cfg(test)]")
        .next()
        .expect("production bots.rs");
    assert!(
        !production.contains("write_hook_isolation_files")
            && !production.contains("BotHook.csproj")
            && !production.contains("Lumio.EntityChat.BotStartupHook")
            && !production.contains("TreatWarningsAsErrors"),
        "generated Bot.Host hook csproj / isolation props must be deleted"
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

fn collect_cs_files(dir: &Path, out: &mut Vec<PathBuf>) {
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };
    for entry in entries.filter_map(Result::ok) {
        let path = entry.path();
        if path.is_dir() {
            collect_cs_files(&path, out);
            continue;
        }
        if path
            .extension()
            .and_then(|ext| ext.to_str())
            .is_some_and(|ext| ext.eq_ignore_ascii_case("cs"))
        {
            out.push(path);
        }
    }
}

fn banned_startup_hooks_token() -> &'static str {
    concat!("DOTNET_STARTUP", "_HOOKS")
}

fn banned_bot_hook_dir_token() -> &'static str {
    concat!("bot_startup", "_hook")
}

fn banned_load_library_token() -> &'static str {
    concat!("LoadLibrary", "W")
}

fn scan_banned_tokens(dir: &Path, banned: &[&str], hits: &mut Vec<String>) {
    let mut files = Vec::new();
    collect_text_files(dir, &mut files);
    for path in files {
        let Ok(text) = fs::read_to_string(&path) else {
            continue;
        };
        for token in banned {
            if text.contains(token) {
                hits.push(format!("{}:{token}", path.display()));
            }
        }
    }
}

#[test]
fn process_src_has_no_csharp_files_or_injected_hook_dir() {
    let src = process_root().join("src");
    let mut csharp = Vec::new();
    collect_cs_files(&src, &mut csharp);
    assert!(
        csharp.is_empty(),
        "modules/process/src must not contain .cs files: {csharp:?}"
    );
    let hook_dir = process_root()
        .join("src/entity_chat")
        .join(banned_bot_hook_dir_token());
    assert!(
        !hook_dir.exists(),
        "startup hook directory must be deleted: {}",
        hook_dir.display()
    );
}

#[test]
fn process_entity_chat_has_no_startup_hook_injection_tokens() {
    let banned = [banned_startup_hooks_token(), banned_bot_hook_dir_token()];
    let mut hits = Vec::new();
    scan_banned_tokens(&process_root().join("src"), &banned, &mut hits);
    scan_banned_tokens(&process_root().join("tests"), &banned, &mut hits);
    assert!(
        hits.is_empty(),
        "startup-hook injection tokens still present: {hits:?}"
    );
}

#[test]
fn entity_chat_sources_have_no_self_written_loadlibraryw() {
    let banned = [banned_load_library_token()];
    let mut hits = Vec::new();
    scan_banned_tokens(&process_root().join("src/entity_chat"), &banned, &mut hits);
    scan_banned_tokens(&process_root().join("tests"), &banned, &mut hits);
    assert!(
        hits.is_empty(),
        "self-written ABI loader token remains in entity-chat sources: {hits:?}"
    );
}

#[test]
fn rust_second_oracle_verify_rust_evidence_is_removed() {
    let path = process_root().join("tests/verify_rust_evidence.mjs");
    assert!(
        !path.exists(),
        "second oracle {} must be deleted",
        path.display()
    );
}

#[test]
fn bots_rs_spawns_bot_host_with_contract_args_and_reads_log_dir() {
    let bots = fs::read_to_string(process_root().join("src/entity_chat/bots.rs")).expect("bots.rs");
    let production = bots
        .split("#[cfg(test)]")
        .next()
        .expect("production bots.rs");
    assert!(
        production.contains("Lumio.Client.Bot.Host"),
        "bots.rs must spawn Lumio.Client.Bot.Host"
    );
    assert!(
        production.contains("LumioClientRoot"),
        "Bot.Host path must be discovered via LumioClientRoot"
    );
    for flag in [
        "--server",
        "--account-from",
        "--account-to",
        "--engine-native",
        "--log-dir",
    ] {
        assert!(
            production.contains(flag),
            "bots.rs must pass {flag} to Lumio.Client.Bot.Host"
        );
    }
    assert!(
        !production.contains(banned_startup_hooks_token())
            && !production.contains("LUMIO_BOT_FLEET_SPEC")
            && !production.contains("write_hook_isolation_files")
            && !production.contains("BotHook.csproj"),
        "bots.rs must not inject a startup hook or generate hook csproj"
    );
}

fn host_runtime_clock_src() -> String {
    fs::read_to_string(
        process_root()
            .parent()
            .expect("modules")
            .join("host-runtime/src/clock.rs"),
    )
    .expect("clock.rs")
}

fn host_entry_src() -> String {
    fs::read_to_string(
        process_root()
            .parent()
            .expect("modules")
            .parent()
            .expect("repo")
            .join("entity-chat-host/src/Lumio.Server.EntityChat.HostEntry/HostEntry.cs"),
    )
    .expect("HostEntry.cs")
}

fn production_entity_chat_rs() -> Vec<(PathBuf, String)> {
    let mut files = Vec::new();
    collect_text_files(&process_root().join("src/entity_chat"), &mut files);
    files
        .into_iter()
        .filter(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .is_none_or(|name| name != "bots.rs")
        })
        .filter_map(|path| fs::read_to_string(&path).ok().map(|text| (path, text)))
        .collect()
}

#[test]
fn production_system_clock_has_no_advance_ms_backdoor() {
    let clock = host_runtime_clock_src();
    let trait_body = rust_fn_src(&clock, "pub trait HostClock");
    assert!(
        !trait_body.contains("advance_ms"),
        "HostClock must not declare advance_ms; production clocks have no test backdoor"
    );
    let system = rust_fn_src(&clock, "impl HostClock for SystemMonotonicClock");
    assert!(
        !system.contains("advance_ms"),
        "SystemMonotonicClock must not implement advance_ms"
    );
}

#[test]
fn host_crate_has_no_account_keyed_maps() {
    let mut hits = Vec::new();
    for (path, text) in production_entity_chat_rs() {
        for (index, line) in text.lines().enumerate() {
            let trimmed = line.trim();
            if trimmed.starts_with("//") || trimmed.starts_with("///") {
                continue;
            }
            let account_map = (trimmed.contains("HashMap") || trimmed.contains("BTreeMap"))
                && (trimmed.contains("account") || trimmed.contains("Account"));
            if account_map || trimmed.contains("account_sessions") {
                hits.push(format!("{}:{}:{trimmed}", path.display(), index + 1));
            }
        }
    }
    assert!(
        hits.is_empty(),
        "host crate must not keep an account-keyed HashMap/BTreeMap: {hits:?}"
    );
}

#[test]
fn forward_path_has_no_from_utf8_lossy() {
    let host = fs::read_to_string(process_root().join("src/entity_chat/host.rs")).expect("host.rs");
    let wire = fs::read_to_string(process_root().join("src/entity_chat/wire.rs")).expect("wire.rs");
    assert!(
        !host.contains("from_utf8_lossy"),
        "host.rs must not lossy-convert Runtime frames"
    );
    assert!(
        !wire.contains("from_utf8_lossy"),
        "wire.rs must not lossy-convert Runtime frames"
    );
}

#[test]
fn host_does_not_increment_its_own_tick_id() {
    let host = fs::read_to_string(process_root().join("src/entity_chat/host.rs")).expect("host.rs");
    assert!(
        !host.contains("self.tick_id = self.tick_id.saturating_add(1)"),
        "Tick number must come from Runtime, not a host counter"
    );
    assert!(
        !host.contains("tick_id: u64"),
        "host session state must not own a logical tick_id"
    );
}

#[test]
fn host_entry_holds_world_manager_not_private_world_field() {
    let text = host_entry_src();
    assert!(
        !text.contains("GetField(\"_world\"") && !text.contains("\"_world\""),
        "HostEntry must not reflect ChatCommandRuntime._world"
    );
    assert!(
        text.contains("ServerBootstrap") && text.contains("WorldManager"),
        "HostEntry must boot Runtime WorldManager via ServerBootstrap"
    );
    assert!(
        text.contains("CreateFromSnapshot") || text.contains("Restore("),
        "HostEntry restore must use WorldManager.CreateFromSnapshot"
    );
}

#[test]
fn host_entry_does_not_overlay_list_bindings() {
    let text = host_entry_src();
    assert!(
        !text.contains("private static Dictionary<string, object?>? ListedBinding"),
        "delete the ListBindings fallback that covers tombstoned/cross_room"
    );
}

#[test]
fn owner_loop_pumps_wall_clock() {
    let host = fs::read_to_string(process_root().join("src/entity_chat/host.rs")).expect("host.rs");
    assert!(
        host.contains("recv_timeout") && host.contains("drive_wall"),
        "owner loop must pump_wall_clock on a timeout, not only when harness posts work"
    );
}

#[test]
fn ci_cargo_entity_chat_includes_ubuntu() {
    let yml = fs::read_to_string(
        process_root()
            .parent()
            .expect("modules")
            .parent()
            .expect("repo")
            .join(".github/workflows/repository-policy.yml"),
    )
    .expect("workflow");
    let start = yml
        .find("  cargo-entity-chat:\n")
        .expect("cargo-entity-chat job");
    let rest = &yml[start + 1..];
    let end = rest.find("\n  cargo-").unwrap_or(rest.len());
    let job = &rest[..end];
    assert!(
        job.contains("ubuntu-latest"),
        "Cargo entity-chat job must compile on ubuntu-latest, got {job}"
    );
}
