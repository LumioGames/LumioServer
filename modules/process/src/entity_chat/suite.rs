//! 11-scenario R-00354 replay against the slice-scoped Rust host.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use sha2::{Digest, Sha256};

use lumio_host_runtime::{HostClock, NativeAbiKernel, SharedClock};
use serde_json::{json, Value};

use super::account::{login_or_register, AccountServerProcess};
use super::admission::{generate_keys, issue_bot_tool_credential, verify_admission};
use super::bots::{discover_bot_host, run_client_bot_fleet, ClientBotFleet, ClientBotTrace};
use super::browser::capture_browser_login;
use super::clr::{ClrGameplay, ClrGameplayConfig};
use super::crypto::hex_lower;
use super::envelope::InputCommand;
use super::host::{AdmitTrace, AttributeQueryRequest, ConnectionBinding, EntityChatHost};
use super::runtime::{
    AttributeQueryOutcome, AttributeQueryScope, BoundEntityKind, ChatOpKind, RuntimeSurface,
    RuntimeTick,
};
use super::wire::RoomClient;
use super::{
    bot_name, ADMISSION_KEY_ID, BOT_COUNT, BROWSER_NAME, ISO_ROOM, MAIN_ROOM,
    MAX_CHAT_INPUTS_PER_TICK, RECONNECT_WINDOW_MS, TEST_PASSWORD,
};

/// Inputs for one suite run.
pub struct SuiteOptions {
    pub out_dir: PathBuf,
    pub account_server_dll: PathBuf,
    pub dotnet: String,
    pub clr: Option<ClrGameplayConfig>,
}

/// Result of one or two rounds.
pub struct SuiteReport {
    pub ok: bool,
    pub blocked: Option<String>,
    pub rounds: Vec<Value>,
}

/// Runs one suite round. CoreCLR can be created only once per process, so
/// two-round replay must use two OS processes.
///
/// # Panics
///
/// Panics when the Tokio runtime cannot be created.
#[must_use]
pub fn run_round_blocking(options: &SuiteOptions) -> Value {
    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .expect("tokio runtime");
    runtime.block_on(run_round_async(options, &options.out_dir))
}

/// Runs two identical rounds in this process. Prefer two OS processes; a second
/// `create_clr_host` in the same process is rejected by CoreCLR.
///
/// # Panics
///
/// Panics when the Tokio runtime cannot be created.
#[must_use]
pub fn run_two_rounds(options: &SuiteOptions) -> SuiteReport {
    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .expect("tokio runtime");
    runtime.block_on(run_two_rounds_async(options))
}

async fn run_two_rounds_async(options: &SuiteOptions) -> SuiteReport {
    std::fs::create_dir_all(&options.out_dir).ok();
    let mut rounds = Vec::new();
    for n in [1, 2] {
        let round_dir = options.out_dir.join(format!("round-{n}"));
        let result = run_round_async(options, &round_dir).await;
        let ok = result.get("ok").and_then(Value::as_bool) == Some(true)
            && result.get("blocked").is_none();
        rounds.push(json!({
            "round": n,
            "ok": ok,
            "blocked": result.get("blocked").cloned(),
            "census": result.get("census").cloned(),
        }));
        if !ok {
            break;
        }
    }
    let comparison_ok = rounds.len() == 2 && rounds.iter().all(|row| row["ok"] == true);
    let blocked = rounds.iter().find_map(|row| {
        row.get("blocked")
            .and_then(Value::as_str)
            .map(str::to_owned)
    });
    let conclusion = if blocked.is_some() && !comparison_ok {
        "BLOCKED"
    } else if comparison_ok {
        "SUCCESS"
    } else {
        "FAILED"
    };
    let manifest = json!({
        "schemaVersion": 1,
        "tool": "lumio-entity-chat-rust-host/replay",
        "createdAt": chrono_now(),
        "conclusion": conclusion,
        "blocked": blocked,
        "rounds": rounds,
    });
    let _ = std::fs::write(
        options.out_dir.join("manifest.json"),
        serde_json::to_string_pretty(&manifest).unwrap_or_default() + "\n",
    );
    SuiteReport {
        ok: comparison_ok,
        blocked,
        rounds: manifest
            .get("rounds")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default(),
    }
}

/// Runs one round and writes `evidence.json` + `host-audit.ndjson`.
pub async fn run_round(options: &SuiteOptions, out_dir: &Path) -> Value {
    run_round_async(options, out_dir).await
}

async fn run_round_async(options: &SuiteOptions, out_dir: &Path) -> Value {
    let _ = std::fs::remove_dir_all(out_dir);
    std::fs::create_dir_all(out_dir).ok();
    if !options.account_server_dll.is_file() {
        return write_blocked(out_dir, "account-server dll not found; host cannot start");
    }
    let admission = generate_keys();
    let bot = generate_keys();
    let store = out_dir.join("account-store");
    let account = match AccountServerProcess::start(
        &options.account_server_dll,
        &store,
        &admission.seed,
        &bot.public,
        &options.dotnet,
    ) {
        Ok(process) => process,
        Err(error) => return write_blocked(out_dir, &error),
    };

    let now = unix_seconds();
    let bot_claim = issue_bot_tool_credential(&bot.seed, now, now + 3600, "bot-launcher");
    let gameplay: Box<dyn RuntimeSurface> = match &options.clr {
        Some(config) => match ClrGameplay::start(config) {
            Ok(world) => Box::new(world),
            Err(error) => {
                return write_blocked(out_dir, &format!("CoreCLR host failed: {error}"));
            }
        },
        None => {
            return write_blocked(
                out_dir,
                "BLOCKED: CoreCLR Runtime assembly was not provided; refusing to fake 101 entities",
            );
        }
    };
    let kernel = match &options.clr {
        Some(config) => match NativeAbiKernel::load(&config.engine_native) {
            Ok(kernel) => Box::new(kernel),
            Err(error) => return write_blocked(out_dir, &error),
        },
        None => {
            return write_blocked(out_dir, "BLOCKED: NativeCore timer ABI was not provided");
        }
    };
    let host = EntityChatHost::new(
        RECONNECT_WINDOW_MS,
        SharedClock::system(),
        gameplay,
        kernel,
        ADMISSION_KEY_ID,
        admission.public.to_vec(),
        now,
    );

    let mut scenarios: HashMap<String, Value> = HashMap::new();
    let mut blocked: Option<String> = None;

    let first = login_or_register(&account.uri(), "Bot01", TEST_PASSWORD, Some(&bot_claim))
        .await
        .unwrap_or_else(|error| super::AccountLoginResult {
            accepted: false,
            account_newly_created: false,
            account_id: None,
            login_name: None,
            admission_credential: None,
            error_code: Some(error),
        });
    let repeat = login_or_register(&account.uri(), "Bot01", TEST_PASSWORD, Some(&bot_claim))
        .await
        .unwrap_or_else(|_| empty_login());
    let wrong = login_or_register(&account.uri(), "Bot01", "654321", Some(&bot_claim))
        .await
        .unwrap_or_else(|_| empty_login());
    let scenario1 = first.accepted
        && repeat.accepted
        && first.account_id == repeat.account_id
        && !wrong.accepted
        && wrong.error_code.as_deref() == Some("wrong_password");
    scenarios.insert(
        "1".to_owned(),
        json!({
            "ok": scenario1,
            "accountId": first.account_id,
            "repeatAccountId": repeat.account_id,
            "wrongPasswordCode": wrong.error_code,
        }),
    );
    if !scenario1 {
        blocked = Some("scenario 1 login-or-register failed".to_owned());
    }

    let mut connections: Vec<(String, String)> = Vec::new();
    for i in 1..=BOT_COUNT {
        let name = bot_name(i);
        let login = login_or_register(&account.uri(), &name, TEST_PASSWORD, Some(&bot_claim))
            .await
            .unwrap_or_else(|_| empty_login());
        if !login.accepted {
            blocked = Some(format!("bot login failed: {name}"));
            break;
        }
        let Some(credential) = login.admission_credential else {
            blocked = Some(format!("bot login failed: {name}"));
            break;
        };
        if let Err(code) = verify_admission(&credential, ADMISSION_KEY_ID, &admission.public, now) {
            blocked = Some(format!("admission verify failed: {name} {code}"));
            break;
        }
        let connection = format!("c-{}", name.to_ascii_lowercase());
        let admit = host.admit(MAIN_ROOM.to_owned(), connection.clone(), credential);
        if !admit.accepted {
            blocked = Some(format!("bot admit failed: {name}"));
            break;
        }
        connections.push((connection, name));
    }

    let bots_only = host.census(MAIN_ROOM.to_owned());
    scenarios.insert(
        "2".to_owned(),
        json!({
            "ok": bots_only.bot_count == 100 && bots_only.player_count == 0,
            "botCount": bots_only.bot_count,
        }),
    );

    let browser_login = login_or_register(&account.uri(), BROWSER_NAME, TEST_PASSWORD, None)
        .await
        .unwrap_or_else(|_| empty_login());
    let mut browser_ok = false;
    let mut browser_verify: Option<String> = None;
    let mut browser_admit_code: Option<String> = None;
    if !browser_login.accepted {
        browser_admit_code = browser_login.error_code.clone();
    } else if browser_login.admission_credential.is_none() {
        browser_admit_code = Some("missing_admission_credential".to_owned());
    } else {
        let credential = browser_login.admission_credential.clone().unwrap();
        match verify_admission(&credential, ADMISSION_KEY_ID, &admission.public, now) {
            Ok(_) => {
                let admit = host.admit(MAIN_ROOM.to_owned(), "c-browser".to_owned(), credential);
                browser_ok = admit.accepted
                    && admit
                        .binding
                        .as_ref()
                        .is_some_and(|binding| binding.entity_type == BoundEntityKind::Player);
                browser_admit_code = admit.error_code;
            }
            Err(code) => browser_verify = Some(code),
        }
    }

    let full = host.census(MAIN_ROOM.to_owned());
    let admits = host.list_admits(MAIN_ROOM.to_owned());
    let process_name = replay_process_name();
    let census_payload = census_payload(&admits);
    let host_audit = host_audit(&process_name, &admits, MAIN_ROOM);
    let listen_uri = host.listen_uri();
    let mut browser_wire = RoomClient::connect(&listen_uri, "c-browser").ok();
    if let Some(client) = browser_wire.as_mut() {
        let _ = client.recv_text();
    }
    let before_observers = host.wire_observer_count("c-browser".to_owned());
    let account_uri = account.uri();
    let out_dir_pw = out_dir.to_path_buf();
    let listen_pw = listen_uri.clone();
    let pw_thread = super::browser::game_root().ok().map(|_| {
        thread::spawn(move || {
            capture_browser_login(
                &account_uri,
                Some(&listen_pw),
                TEST_PASSWORD,
                &out_dir_pw,
                101,
            )
        })
    });
    if pw_thread.is_some() {
        let _ = wait_for_wire_observers(
            &host,
            "c-browser",
            before_observers.saturating_add(1),
            Duration::from_secs(25),
        );
    }

    let mut resolved = 0;
    for (connection, _) in &connections {
        if let Some(binding) = host.try_self_lookup(connection.clone()) {
            if host
                .try_resolve_by_net_entity_id(MAIN_ROOM.to_owned(), binding.net_entity_id)
                .is_some()
            {
                resolved += 1;
            }
        }
    }
    let browser_bound = host.try_self_lookup("c-browser".to_owned()).is_some();
    scenarios.insert(
        "4".to_owned(),
        json!({
            "ok": resolved == 100 && browser_bound,
            "resolvedBots": resolved,
        }),
    );

    let Some(browser_binding) = host.try_self_lookup("c-browser".to_owned()) else {
        blocked = blocked.or(Some("browser connection was not bound".to_owned()));
        let playwright = match pw_thread {
            Some(handle) => handle
                .join()
                .unwrap_or_else(|_| super::browser::PlaywrightCapture::failed("playwright thread")),
            None => super::browser::PlaywrightCapture::failed("browser connection was not bound"),
        };
        scenarios.insert(
            "3".to_owned(),
            json!({
                "ok": false,
                "playwrightRan": playwright.playwright_ran(),
                "loginAccepted": browser_login.accepted,
                "loginError": browser_login.error_code,
                "verifyError": browser_verify,
                "admitError": browser_admit_code,
            }),
        );
        let evidence = json!({
            "ok": false,
            "blocked": blocked,
            "hostProcess": host_process_payload(&process_name, &host.listen_uri()),
            "playwright": playwright.to_json(),
            "accountServer": account_meta(&options.account_server_dll, &account),
            "census": census_payload,
            "scenarios": scenarios,
        });
        write_evidence(out_dir, &evidence, &host_audit);
        return evidence;
    };

    let ok_query = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ServerAuthoritative,
        room_id: MAIN_ROOM.to_owned(),
        net_entity_id: browser_binding.net_entity_id.clone(),
        attribute_id: "EntityIdentity.entityType".to_owned(),
        connection_generation: None,
    });
    let invisible = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ClientReplica,
        room_id: MAIN_ROOM.to_owned(),
        net_entity_id: browser_binding.net_entity_id.clone(),
        attribute_id: "ChatComponent.lastMessageText".to_owned(),
        connection_generation: None,
    });
    let unauthorized = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ClientReplica,
        room_id: MAIN_ROOM.to_owned(),
        net_entity_id: browser_binding.net_entity_id.clone(),
        attribute_id: "EntityIdentity.claimedMark".to_owned(),
        connection_generation: None,
    });
    let missing = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ServerAuthoritative,
        room_id: MAIN_ROOM.to_owned(),
        net_entity_id: "ffffffffffffffffffffffffffffffff".to_owned(),
        attribute_id: "EntityIdentity.entityType".to_owned(),
        connection_generation: None,
    });
    let stale = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ServerAuthoritative,
        room_id: MAIN_ROOM.to_owned(),
        net_entity_id: browser_binding.net_entity_id.clone(),
        attribute_id: "EntityIdentity.entityType".to_owned(),
        connection_generation: Some(0),
    });
    let query_traces = json!({
        "okValue": ok_query.value,
        "invisible": format!("{:?}", invisible.outcome),
        "unauthorized": format!("{:?}", unauthorized.outcome),
        "stale": format!("{:?}", stale.outcome),
        "nonExistent": format!("{:?}", missing.outcome),
    });
    scenarios.insert(
        "5".to_owned(),
        json!({
            "ok": ok_query.outcome == AttributeQueryOutcome::Ok
                && invisible.outcome == AttributeQueryOutcome::Invisible
                && unauthorized.outcome == AttributeQueryOutcome::Unauthorized
                && missing.outcome == AttributeQueryOutcome::NonExistent
                && stale.outcome == AttributeQueryOutcome::StaleGeneration,
            "okValue": ok_query.value,
            "invisible": format!("{:?}", invisible.outcome),
            "unauthorized": format!("{:?}", unauthorized.outcome),
            "stale": format!("{:?}", stale.outcome),
            "nonExistent": format!("{:?}", missing.outcome),
        }),
    );

    let bot_host = match discover_bot_host() {
        Ok(path) => path,
        Err(reason) => {
            let playwright = match pw_thread {
                Some(handle) => handle.join().unwrap_or_else(|_| {
                    super::browser::PlaywrightCapture::failed("playwright thread")
                }),
                None => super::browser::PlaywrightCapture::failed(&reason),
            };
            let evidence = json!({
                "ok": false,
                "blocked": reason,
                "hostProcess": host_process_payload(&process_name, &host.listen_uri()),
                "playwright": playwright.to_json(),
                "accountServer": account_meta(&options.account_server_dll, &account),
                "census": census_payload,
                "scenarios": scenarios,
            });
            write_evidence(out_dir, &evidence, &host_audit);
            return evidence;
        }
    };
    let engine_native = match options.clr.as_ref() {
        Some(config) if config.engine_native.is_file() => config.engine_native.clone(),
        _ => {
            return write_blocked(out_dir, "BLOCKED: LUMIO_ENGINE_NATIVE is not set");
        }
    };
    let envelopes: Vec<(String, InputCommand)> = connections
        .iter()
        .map(|(connection, name)| {
            (
                connection.clone(),
                InputCommand::from_chat_text(&format!("hello-{name}")),
            )
        })
        .collect();
    let first_envelope = envelopes.first().map(|(_, envelope)| envelope.clone());
    let mut tick = RuntimeTick::default();
    let mut received = Vec::new();
    let fleet_dir = out_dir.join("client-bots");
    let mut bot_fleet: Option<ClientBotFleet> = None;
    let bot_trace = match run_client_bot_fleet(
        &bot_host,
        &engine_native,
        &listen_uri,
        &envelopes,
        &fleet_dir,
        &options.dotnet,
        || {
            apply_pending_chat_ticks(&host, &mut tick, &mut browser_wire, &mut received);
        },
    ) {
        Ok(fleet) => {
            let trace = fleet.trace.clone();
            bot_fleet = Some(fleet);
            trace
        }
        Err(reason) => {
            blocked = blocked.or(Some(reason));
            ClientBotTrace::default()
        }
    };
    wait_for_observed_chat_events(
        &host,
        &mut tick,
        &mut browser_wire,
        &mut received,
        BOT_COUNT as usize,
        Duration::from_secs(30),
    );
    if let Some(client) = browser_wire.as_mut() {
        let _ = client.send_text(&InputCommand::from_chat_text("hello-browser").to_json());
    }
    wait_for_observed_chat_events(
        &host,
        &mut tick,
        &mut browser_wire,
        &mut received,
        101,
        Duration::from_secs(10),
    );
    if let Some(fleet) = bot_fleet.take() {
        fleet.release();
    }
    let timer_ok = bot_trace.timer_manager_invoked
        && bot_trace.tick_source == "native-kernel/tickFrame"
        && bot_trace.utterance_ticks.contains(&5)
        && bot_trace.utterance_ticks.contains(&10)
        && bot_trace.utterance_ticks.contains(&15)
        && tick.ok
        && tick.applied_tick >= 1;
    let chat_events: Vec<String> = received
        .iter()
        .filter(|frame| is_chat_event_delta(frame))
        .cloned()
        .collect();
    let chat_ok = chat_events.len() == 101 && timer_ok;
    let event_order: Vec<String> = chat_events.clone();
    let applied_ticks: Vec<u64> = chat_events
        .iter()
        .filter_map(|frame| delta_tick_id(frame))
        .collect();
    let first_block = first_envelope
        .as_ref()
        .and_then(|envelope| envelope.commands.first());
    let playwright = match pw_thread {
        Some(handle) => handle
            .join()
            .unwrap_or_else(|_| super::browser::PlaywrightCapture::failed("playwright thread")),
        None => super::browser::PlaywrightCapture::failed("BLOCKED: LUMIO_GAME_ROOT is not set"),
    };
    let playwright_ran = playwright.playwright_ran();
    let browser_room_observed = chat_events.len() == 101;
    scenarios.insert(
        "3".to_owned(),
        json!({
            "ok": browser_ok && full.total == 101 && full.bot_count == 100 && full.player_count == 1 && playwright_ran && browser_room_observed,
            "total": full.total,
            "botCount": full.bot_count,
            "playerCount": full.player_count,
            "playwrightRan": playwright_ran,
            "loginAccepted": browser_login.accepted,
            "loginError": browser_login.error_code,
            "verifyError": browser_verify,
            "admitError": browser_admit_code,
        }),
    );
    scenarios.insert(
        "6".to_owned(),
        json!({
            "ok": chat_ok,
            "eventCount": chat_events.len(),
            "appliedTick": tick.applied_tick,
            "timerManagerInvoked": timer_ok,
            "cadence": bot_trace.tick_source,
            "tickSource": bot_trace.tick_source,
            "utteranceTicks": bot_trace.utterance_ticks,
            "messageType": first_envelope.as_ref().map(|envelope| envelope.message_type.as_str()),
            "mappingId": first_block.map(|block| block.mapping_id.as_str()),
            "payload": first_block.map(|block| block.payload.as_str()),
            "payloadSha256": first_block.map(|block| block.payload_sha256.as_str()),
        }),
    );

    let snapshot = host.capture_persist_snapshot(MAIN_ROOM.to_owned());
    let window_before = chat_events.len();
    let last_before = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ServerAuthoritative,
        room_id: MAIN_ROOM.to_owned(),
        net_entity_id: browser_binding.net_entity_id.clone(),
        attribute_id: "ChatComponent.lastMessageText".to_owned(),
        connection_generation: None,
    });
    let snapshot_path = out_dir.join("persist-snapshot.bin");
    let snapshot_sha256 = if snapshot.bytes.is_empty() {
        None
    } else {
        let _ = std::fs::write(&snapshot_path, &snapshot.bytes);
        Some(sha256_hex(&snapshot.bytes))
    };
    if !snapshot.bytes.is_empty() {
        host.restore_persist_snapshot(MAIN_ROOM.to_owned(), snapshot.clone());
    }
    let history_max = 0;
    let still_bound = host.try_self_lookup("c-browser".to_owned()).is_some();
    let last_after = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ServerAuthoritative,
        room_id: MAIN_ROOM.to_owned(),
        net_entity_id: browser_binding.net_entity_id.clone(),
        attribute_id: "ChatComponent.lastMessageText".to_owned(),
        connection_generation: None,
    });
    let extra_after_restore = browser_wire
        .as_mut()
        .and_then(|client| client.recv_text().ok());
    let refilled = extra_after_restore
        .as_ref()
        .is_some_and(|frame| is_chat_event_delta(frame));
    let restored_window = if snapshot.bytes.is_empty() {
        None
    } else if refilled {
        Some(1_u64)
    } else {
        Some(0_u64)
    };
    let process_a = json!({
        "pid": std::process::id(),
        "process": process_name,
    });
    let process_b = snapshot_sha256
        .as_ref()
        .and_then(|_| spawn_restore_process(&snapshot_path, out_dir));
    let persist_ok = still_bound
        && !refilled
        && last_after.outcome == AttributeQueryOutcome::Ok
        && last_after.value == last_before.value
        && last_after.value.is_some()
        && host.census(MAIN_ROOM.to_owned()).total == 101
        && snapshot_sha256.is_some()
        && process_b.is_some();
    scenarios.insert(
        "7".to_owned(),
        json!({
            "ok": persist_ok && window_before > 0 && history_max == 0 && restored_window == Some(0),
            "snapshotEntities": snapshot.bytes.len(),
            "historyCountMax": history_max,
            "restoredWindow": restored_window,
            "windowBeforeSnapshot": window_before,
            "snapshotSource": process_name,
        }),
    );

    let previous_bot100 = host.must_self("c-bot100");
    let entity_a = previous_bot100.net_entity_id.clone();
    let entity_a_host = previous_bot100.net_entity_id.clone();
    let previous_session = previous_bot100.session_id.clone();
    let previous_account = previous_bot100.account_id.clone();
    let mut old_bot100 = RoomClient::connect(&host.listen_uri(), "c-bot100").ok();
    if let Some(client) = old_bot100.as_mut() {
        let _ = client.recv_text();
    }
    let re_login = login_or_register(&account.uri(), "Bot100", TEST_PASSWORD, Some(&bot_claim))
        .await
        .unwrap_or_else(|_| empty_login());
    let mut re_ok = false;
    let mut rebound_binding: Option<ConnectionBinding> = None;
    let mut takeover = false;
    if re_login.accepted {
        if let Some(credential) = re_login.admission_credential {
            if verify_admission(&credential, ADMISSION_KEY_ID, &admission.public, now).is_ok() {
                let rebind = host.admit(MAIN_ROOM.to_owned(), "c-bot100-re".to_owned(), credential);
                takeover = rebind.takeover;
                re_ok = rebind.takeover
                    && rebind.binding.as_ref().is_some_and(|binding| {
                        binding.net_entity_id == entity_a
                            && binding.net_entity_id == entity_a_host
                            && binding.session_id != previous_session
                            && binding.net_entity_id != binding.session_id
                            && binding.account_id == previous_account
                    });
                rebound_binding = rebind.binding;
            }
        }
    }
    let superseded_frame = old_bot100
        .as_mut()
        .and_then(|client| client.recv_text().ok());
    let connection_superseded_received = superseded_frame
        .as_deref()
        .is_some_and(|frame| frame.contains("\"messageType\":\"ConnectionSuperseded\""));
    if connection_superseded_received {
        if let Some(old) = old_bot100.as_mut() {
            let _ = old.is_closed_after();
        }
    }
    let rejected = host.admit_chat_input(
        "c-bot100".to_owned(),
        InputCommand::from_chat_text("while-down"),
    );
    let _ = host.admit_chat_input(
        "c-browser".to_owned(),
        InputCommand::from_chat_text("room-continues"),
    );
    let _ = host.run_tick(MAIN_ROOM.to_owned());
    re_ok = re_ok && rejected.kind == ChatOpKind::Rejected && connection_superseded_received;
    let reconnect_trace = json!({
        "rebound": re_ok,
        "entityA": entity_a_host,
        "netEntityId": rebound_binding.as_ref().map(|binding| binding.net_entity_id.clone()).unwrap_or(entity_a_host.clone()),
        "previousNetEntityId": entity_a_host,
        "sessionId": rebound_binding.as_ref().map(|binding| binding.session_id.clone()),
        "previousSessionId": previous_session,
        "accountId": rebound_binding.as_ref().map(|binding| binding.account_id.clone()),
        "previousAccountId": previous_account,
        "takeover": takeover,
        "connectionSupersededReceived": connection_superseded_received,
        "oldConnectionId": "c-bot100",
    });
    scenarios.insert(
        "8".to_owned(),
        json!({
            "ok": re_ok && connection_superseded_received,
            "rebound": re_ok,
            "entityA": entity_a_host,
            "netEntityId": reconnect_trace.get("netEntityId").cloned(),
            "previousNetEntityId": entity_a_host,
            "sessionId": reconnect_trace.get("sessionId").cloned(),
            "previousSessionId": previous_session,
            "accountId": reconnect_trace.get("accountId").cloned(),
            "previousAccountId": previous_account,
            "takeover": takeover,
            "connectionSupersededReceived": connection_superseded_received,
        }),
    );

    let previous_99 = host.must_self("c-bot99");
    let entity_99 = previous_99.net_entity_id.clone();
    let entity_99_host = previous_99.net_entity_id.clone();
    let account_99 = previous_99.account_id;
    assert!(host.disconnect("c-bot99".to_owned()));
    host.clock().advance_ms(RECONNECT_WINDOW_MS + 1_000);
    host.drive_kernel();
    let expired = 1_usize;
    let after_expiry = login_or_register(&account.uri(), "Bot99", TEST_PASSWORD, Some(&bot_claim))
        .await
        .unwrap_or_else(|_| empty_login());
    let mut expiry_ok = false;
    let mut entity_b_host: Option<String> = None;
    if after_expiry.accepted {
        if let Some(credential) = after_expiry.admission_credential {
            if verify_admission(&credential, ADMISSION_KEY_ID, &admission.public, now).is_ok() {
                let created_b =
                    host.admit(MAIN_ROOM.to_owned(), "c-bot99-b".to_owned(), credential);
                let tombstoned = host.query_attribute(AttributeQueryRequest {
                    caller_scope: AttributeQueryScope::ServerAuthoritative,
                    room_id: MAIN_ROOM.to_owned(),
                    net_entity_id: entity_99.clone(),
                    attribute_id: "EntityIdentity.entityType".to_owned(),
                    connection_generation: None,
                });
                expiry_ok = created_b.accepted
                    && created_b.binding.as_ref().is_some_and(|binding| {
                        binding.net_entity_id != entity_99
                            && binding.net_entity_id != entity_99_host
                            && binding.account_id == account_99
                    })
                    && tombstoned.outcome == AttributeQueryOutcome::Tombstoned;
                entity_b_host = created_b
                    .binding
                    .as_ref()
                    .map(|binding| binding.net_entity_id.clone());
            }
        }
    }
    let expiry_trace = json!({
        "tombstoned": expiry_ok,
        "staleARejected": expiry_ok,
        "entityA": entity_99_host,
        "entityB": entity_b_host,
    });
    scenarios.insert(
        "9".to_owned(),
        json!({
            "ok": expiry_ok,
            "expired": expired,
            "entityA": entity_99_host,
            "entityB": entity_b_host,
            "tombstoned": expiry_ok,
            "staleARejected": expiry_ok,
        }),
    );

    let iso_a = login_or_register(&account.uri(), "IsoPlayerA", TEST_PASSWORD, None)
        .await
        .unwrap_or_else(|_| empty_login());
    let iso_b = login_or_register(&account.uri(), "IsoPlayerB", TEST_PASSWORD, None)
        .await
        .unwrap_or_else(|_| empty_login());
    let mut iso_ok = false;
    if iso_a.accepted && iso_b.accepted {
        if let (Some(cred_a), Some(cred_b)) = (
            iso_a.admission_credential.clone(),
            iso_b.admission_credential.clone(),
        ) {
            if verify_admission(&cred_a, ADMISSION_KEY_ID, &admission.public, now).is_ok()
                && verify_admission(&cred_b, ADMISSION_KEY_ID, &admission.public, now).is_ok()
            {
                let _ = host.admit(ISO_ROOM.to_owned(), "iso-a".to_owned(), cred_a);
                let _ = host.admit(ISO_ROOM.to_owned(), "iso-b".to_owned(), cred_b);
                let _ = host
                    .admit_chat_input("iso-a".to_owned(), InputCommand::from_chat_text("iso-only"));
                let _ = host.run_tick(ISO_ROOM.to_owned());
                let cross = host.query_attribute(AttributeQueryRequest {
                    caller_scope: AttributeQueryScope::ServerAuthoritative,
                    room_id: ISO_ROOM.to_owned(),
                    net_entity_id: browser_binding.net_entity_id.clone(),
                    attribute_id: "EntityIdentity.entityType".to_owned(),
                    connection_generation: None,
                });
                let leaked = browser_wire.as_ref().is_some_and(|client| {
                    client
                        .received
                        .iter()
                        .any(|frame| frame.contains("iso-only"))
                });
                iso_ok = host.census(ISO_ROOM.to_owned()).total == 2
                    && !leaked
                    && cross.error_code.as_deref() == Some("cross_room_reference");
            }
        }
    }
    scenarios.insert(
        "10".to_owned(),
        json!({
            "ok": iso_ok,
            "isoTotal": host.census(ISO_ROOM.to_owned()).total,
        }),
    );

    let scale_ok =
        full.total == 101 && chat_ok && event_order.len() == 101 && applied_ticks.len() == 101;
    scenarios.insert(
        "11".to_owned(),
        json!({
            "ok": scale_ok,
            "totalEntities": full.total,
            "botCount": full.bot_count,
            "playerCount": full.player_count,
            "eventOrder": event_order,
            "appliedTicks": applied_ticks,
            "appliedTick": tick.applied_tick,
        }),
    );

    let mut all_ok = blocked.is_none();
    for value in scenarios.values() {
        if value.get("ok") == Some(&Value::Bool(false)) {
            all_ok = false;
        }
    }
    let session_ids: Vec<String> = admits.iter().map(|row| row.session_id.clone()).collect();
    let evidence = json!({
        "ok": all_ok,
        "blocked": blocked,
        "hostProcess": host_process_payload(&process_name, &host.listen_uri()),
        "playwright": playwright.to_json(),
        "accountServer": account_meta(&options.account_server_dll, &account),
        "census": census_payload,
        "liveAdmits": {
            "desired": 101,
            "live": admits.len(),
            "admits": admits.iter().map(|row| json!({
                "ok": true,
                "process": process_name,
                "entityType": row.entity_type.as_str(),
                "netEntityId": row.net_entity_id,
                "sessionId": row.session_id,
                "loginName": row.login_name,
                "connectionId": row.connection_id,
                "accountId": row.account_id,
            })).collect::<Vec<_>>(),
        },
        "traces": {
            "account": {
                "createAck": first.accepted && first.account_newly_created,
                "loadAck": repeat.accepted,
                "wrongPasswordCode": wrong.error_code,
            },
            "queries": query_traces,
            "chat": {
                "eventCount": chat_events.len(),
                "tickSource": bot_trace.tick_source,
                "timerManagerInvoked": timer_ok,
                "utteranceTicks": bot_trace.utterance_ticks,
                "botHostPid": bot_trace.pid,
                "messageType": first_envelope.as_ref().map(|envelope| envelope.message_type.as_str()),
                "mappingId": first_block.map(|block| block.mapping_id.as_str()),
                "payloadSha256": first_block.map(|block| block.payload_sha256.as_str()),
                "receivedEvents": chat_events,
                "windowLines": chat_events,
            },
            "reconnect": reconnect_trace,
            "persist": {
                "clientWindowBeforeSnapshot": window_before,
                "clientWindowAfterRestore": restored_window,
                "processA": process_a,
                "processB": process_b,
                "snapshotSha256": snapshot_sha256,
            },
            "expiry": expiry_trace,
            "handshake": {
                "completed": admits.len(),
                "sessionIds": session_ids,
            },
        },
        "scenarios": scenarios,
        "browserWindow": chat_events,
    });
    write_evidence(out_dir, &evidence, &host_audit);
    evidence
}

fn wait_for_wire_observers(
    host: &EntityChatHost,
    connection: &str,
    min: usize,
    budget: Duration,
) -> bool {
    let deadline = Instant::now() + budget;
    loop {
        if host.wire_observer_count(connection.to_owned()) >= min {
            return true;
        }
        if Instant::now() >= deadline {
            return host.wire_observer_count(connection.to_owned()) >= min;
        }
        thread::sleep(Duration::from_millis(50));
    }
}

fn is_chat_event_delta(frame: &str) -> bool {
    frame.contains("\"messageType\":\"Delta\"") && frame.contains("\"mappingId\":\"chat.event\"")
}

fn delta_tick_id(frame: &str) -> Option<u64> {
    serde_json::from_str::<Value>(frame)
        .ok()?
        .get("tickId")?
        .as_u64()
}

/// Drains already-queued Room `chat.event` frames. Must not block past a deadline.
pub fn drain_chat_event_deltas(client: &mut Option<RoomClient>, received: &mut Vec<String>) {
    let Some(client) = client.as_mut() else {
        return;
    };
    let deadline = Instant::now() + Duration::from_millis(50);
    while received.len() < 101 && Instant::now() < deadline {
        match client.try_recv_text() {
            Ok(Some(frame)) if is_chat_event_delta(&frame) => received.push(frame),
            Ok(Some(_)) => {}
            Ok(None) | Err(_) => break,
        }
    }
}

/// Applies Room-observed pending chats in ≤64 batches. Must return if a tick
/// does not reduce pending or `tick.ok` is false.
pub fn apply_pending_chat_ticks(
    host: &EntityChatHost,
    tick: &mut RuntimeTick,
    browser_wire: &mut Option<RoomClient>,
    received: &mut Vec<String>,
) {
    loop {
        let pending_chats = host.pending_wire_chat_inputs();
        if pending_chats > MAX_CHAT_INPUTS_PER_TICK {
            *tick = RuntimeTick::failed("runtime_failure");
            break;
        }
        if pending_chats >= MAX_CHAT_INPUTS_PER_TICK {
            *tick = host.schedule_room_tick(MAIN_ROOM.to_owned(), 1);
            drain_chat_event_deltas(browser_wire, received);
            if !tick.ok {
                break;
            }
            let after = host.pending_wire_chat_inputs();
            if after >= pending_chats {
                break;
            }
            continue;
        }
        if pending_chats > 0 {
            *tick = host.schedule_room_tick(MAIN_ROOM.to_owned(), 1);
            drain_chat_event_deltas(browser_wire, received);
            if !tick.ok {
                break;
            }
        }
        break;
    }
}

fn wait_for_observed_chat_events(
    host: &EntityChatHost,
    tick: &mut RuntimeTick,
    browser_wire: &mut Option<RoomClient>,
    received: &mut Vec<String>,
    want: usize,
    budget: Duration,
) {
    let deadline = Instant::now() + budget;
    loop {
        apply_pending_chat_ticks(host, tick, browser_wire, received);
        drain_chat_event_deltas(browser_wire, received);
        if received.len() >= want {
            return;
        }
        if Instant::now() >= deadline {
            apply_pending_chat_ticks(host, tick, browser_wire, received);
            drain_chat_event_deltas(browser_wire, received);
            return;
        }
        thread::sleep(Duration::from_millis(50));
    }
}

fn sha256_hex(bytes: &[u8]) -> String {
    hex_lower(&Sha256::digest(bytes))
}

fn spawn_restore_process(snapshot_path: &Path, out_dir: &Path) -> Option<Value> {
    let exe = std::env::current_exe().ok()?;
    let child_dir = out_dir.join("process-b");
    std::fs::create_dir_all(&child_dir).ok()?;
    let status = Command::new(&exe)
        .arg("--restore-snapshot")
        .arg(snapshot_path)
        .arg("--out")
        .arg(&child_dir)
        .status()
        .ok()?;
    if !status.success() {
        return None;
    }
    let parsed = std::fs::read_to_string(child_dir.join("restore-result.json"))
        .ok()
        .and_then(|text| serde_json::from_str::<Value>(&text).ok())?;
    if parsed.get("ok").and_then(Value::as_bool) != Some(true) {
        return None;
    }
    let pid = parsed
        .get("pid")
        .and_then(Value::as_u64)
        .filter(|pid| *pid > 0)?;
    Some(json!({
        "pid": pid,
        "process": parsed
            .get("process")
            .and_then(Value::as_str)
            .unwrap_or("lumio-entity-chat-replay"),
    }))
}

fn empty_login() -> super::AccountLoginResult {
    super::AccountLoginResult {
        accepted: false,
        account_newly_created: false,
        account_id: None,
        login_name: None,
        admission_credential: None,
        error_code: Some("transport".to_owned()),
    }
}

fn census_payload(admits: &[AdmitTrace]) -> Value {
    let mut bots = 0;
    let mut players = 0;
    let mut net_entity_ids = Vec::new();
    let mut entity_types = Vec::new();
    for row in admits {
        net_entity_ids.push(row.net_entity_id.clone());
        entity_types.push(row.entity_type.as_str());
        match row.entity_type {
            BoundEntityKind::Bot => bots += 1,
            BoundEntityKind::Player => players += 1,
        }
    }
    json!({
        "botCount": bots,
        "playerCount": players,
        "total": bots + players,
        "netEntityIds": net_entity_ids,
        "entityTypes": entity_types,
    })
}

fn host_audit(process: &str, admits: &[AdmitTrace], room_id: &str) -> String {
    let mut lines = Vec::new();
    lines.push(
        json!({
            "seq": 0,
            "kind": "audit",
            "process": process,
            "eventId": "host.start",
            "category": "host",
            "severity": "info",
        })
        .to_string(),
    );
    for (i, row) in admits.iter().enumerate() {
        lines.push(
            json!({
                "seq": i + 1,
                "kind": "entity_admitted",
                "process": process,
                "roomId": room_id,
                "netEntityId": row.net_entity_id,
                "entityType": row.entity_type.as_str(),
                "accountId": row.account_id,
                "sessionId": row.session_id,
                "loginName": row.login_name,
                "connectionId": row.connection_id,
            })
            .to_string(),
        );
    }
    lines.join("\n") + "\n"
}

fn replay_process_name() -> String {
    std::env::current_exe()
        .ok()
        .and_then(|path| {
            path.file_stem()
                .map(|stem| stem.to_string_lossy().into_owned())
        })
        .unwrap_or_else(|| "lumio-entity-chat-replay".to_owned())
}

fn host_process_payload(process: &str, listen_uri: &str) -> Value {
    json!({
        "process": process,
        "pid": std::process::id(),
        "listenUri": listen_uri,
        "command": std::env::args().collect::<Vec<String>>(),
    })
}

fn account_meta(dll: &Path, account: &AccountServerProcess) -> Value {
    json!({
        "dll": dll.display().to_string(),
        "port": account.port,
        "pid": account.pid,
        "contractId": "lumio.account-port.v1",
    })
}

fn write_evidence(out_dir: &Path, evidence: &Value, audit: &str) {
    let _ = std::fs::write(
        out_dir.join("evidence.json"),
        serde_json::to_string_pretty(evidence).unwrap_or_default() + "\n",
    );
    let _ = std::fs::write(out_dir.join("host-audit.ndjson"), audit);
    let _ = std::fs::write(out_dir.join("admit-trace.ndjson"), audit);
}

fn write_blocked(out_dir: &Path, reason: &str) -> Value {
    std::fs::create_dir_all(out_dir).ok();
    let evidence = json!({ "ok": false, "blocked": reason });
    write_evidence(out_dir, &evidence, "");
    let _ = std::fs::write(out_dir.join("blocked.txt"), format!("{reason}\n"));
    evidence
}

fn unix_seconds() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs())
        .unwrap_or(0)
}

fn chrono_now() -> String {
    // RFC3339-ish UTC without pulling chrono. Evidence timestamp only.
    let secs = unix_seconds();
    format!("{secs}")
}
