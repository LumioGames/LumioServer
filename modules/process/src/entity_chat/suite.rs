//! 11-scenario R-00354 replay against the slice-scoped Rust host.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use lumio_host_runtime::SharedClock;
use serde_json::{json, Value};

use super::account::{login_or_register, AccountServerProcess};
use super::admission::{generate_keys, issue_bot_tool_credential, verify_admission};
use super::clr::{ClrGameplay, ClrGameplayConfig};
use super::gameplay::{ChatOpKind, LocalGameplay};
use super::host::{
    AttributeQueryOutcome, AttributeQueryRequest, AttributeQueryScope, BoundEntityKind,
    EntityChatHost,
};
use super::{
    bot_name, ADMISSION_KEY_ID, BOT_COUNT, BROWSER_NAME, ISO_ROOM, MAIN_ROOM, RECONNECT_WINDOW_MS,
    TEST_PASSWORD,
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
    let gameplay: Box<dyn super::gameplay::GameplayWorld> = match &options.clr {
        Some(config) => match ClrGameplay::start(config) {
            Ok(world) => Box::new(world),
            Err(error) => {
                return write_blocked(out_dir, &format!("CoreCLR host failed: {error}"));
            }
        },
        None => {
            return write_blocked(
                out_dir,
                "CoreCLR gameplay assembly was not provided; refusing to fake 101 entities",
            );
        }
    };
    let host = EntityChatHost::new(
        RECONNECT_WINDOW_MS,
        SharedClock::system(),
        gameplay,
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
    let census_payload = census_payload(&full);
    let host_audit = host_audit(&host, &full, MAIN_ROOM);
    scenarios.insert(
        "3".to_owned(),
        json!({
            "ok": browser_ok && full.total == 101 && full.bot_count == 100 && full.player_count == 1,
            "total": full.total,
            "botCount": full.bot_count,
            "playerCount": full.player_count,
            "loginAccepted": browser_login.accepted,
            "loginError": browser_login.error_code,
            "verifyError": browser_verify,
            "admitError": browser_admit_code,
        }),
    );

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
        let evidence = json!({
            "ok": false,
            "blocked": blocked,
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
        net_entity_id: browser_binding.net_entity_id,
        attribute_id: "EntityIdentity.entityType".to_owned(),
        connection_generation: None,
    });
    let invisible = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ClientReplica,
        room_id: MAIN_ROOM.to_owned(),
        net_entity_id: browser_binding.net_entity_id,
        attribute_id: "ChatComponent.lastMessageText".to_owned(),
        connection_generation: None,
    });
    let unauthorized = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ClientReplica,
        room_id: MAIN_ROOM.to_owned(),
        net_entity_id: browser_binding.net_entity_id,
        attribute_id: "EntityIdentity.restrictedFlag".to_owned(),
        connection_generation: None,
    });
    let missing = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ServerAuthoritative,
        room_id: MAIN_ROOM.to_owned(),
        net_entity_id: 999_999,
        attribute_id: "EntityIdentity.entityType".to_owned(),
        connection_generation: None,
    });
    let stale = host.query_attribute(AttributeQueryRequest {
        caller_scope: AttributeQueryScope::ServerAuthoritative,
        room_id: MAIN_ROOM.to_owned(),
        net_entity_id: browser_binding.net_entity_id,
        attribute_id: "EntityIdentity.entityType".to_owned(),
        connection_generation: Some(0),
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
        }),
    );

    for (connection, name) in &connections {
        let _ = host.admit_chat_input(connection.clone(), format!("hello-{name}"));
    }
    let _ = host.admit_chat_input("c-browser".to_owned(), "hello-browser".to_owned());
    let tick = host.run_tick(MAIN_ROOM.to_owned());
    let window = host.client_chat_window("c-browser".to_owned());
    let chat_ok = window.len() == 101 && tick.applied_tick == 1;
    let event_order: Vec<String> = window
        .iter()
        .map(|ev| {
            format!(
                "{}:{}:{}",
                ev.sender_net_entity_id, ev.text, ev.room_sequence
            )
        })
        .collect();
    let applied_ticks: Vec<u64> = window.iter().map(|ev| ev.applied_tick).collect();
    scenarios.insert(
        "6".to_owned(),
        json!({
            "ok": chat_ok,
            "eventCount": window.len(),
            "appliedTick": tick.applied_tick,
        }),
    );

    let snapshot = host.capture_persist_snapshot(MAIN_ROOM.to_owned());
    let restored = EntityChatHost::new(
        RECONNECT_WINDOW_MS,
        SharedClock::system(),
        Box::new(LocalGameplay::new()),
        ADMISSION_KEY_ID,
        admission.public.to_vec(),
        now,
    );
    restored.restore_persist_snapshot(MAIN_ROOM.to_owned(), snapshot.clone());
    let history_max = snapshot
        .entities
        .iter()
        .map(|entity| entity.history_count)
        .max()
        .unwrap_or(0);
    let restored_window = restored.client_chat_window("c-browser".to_owned()).len();
    let persist_ok = restored.census(MAIN_ROOM.to_owned()).total == 101 && restored_window == 0;
    scenarios.insert(
        "7".to_owned(),
        json!({
            "ok": persist_ok && snapshot.entities.iter().all(|entity| entity.history_count == 0),
            "snapshotEntities": snapshot.entities.len(),
            "historyCountMax": history_max,
            "restoredWindow": restored_window,
        }),
    );

    let entity_a = host.must_self("c-bot100").net_entity_id;
    assert!(host.disconnect("c-bot100".to_owned()));
    let rejected = host.admit_chat_input("c-bot100".to_owned(), "while-down".to_owned());
    let _ = host.admit_chat_input("c-browser".to_owned(), "room-continues".to_owned());
    let _ = host.run_tick(MAIN_ROOM.to_owned());
    let re_login = login_or_register(&account.uri(), "Bot100", TEST_PASSWORD, Some(&bot_claim))
        .await
        .unwrap_or_else(|_| empty_login());
    let mut re_ok = false;
    if re_login.accepted {
        if let Some(credential) = re_login.admission_credential {
            if verify_admission(&credential, ADMISSION_KEY_ID, &admission.public, now).is_ok() {
                let rebind = host.admit(MAIN_ROOM.to_owned(), "c-bot100-re".to_owned(), credential);
                re_ok = rebind.reconnected
                    && rebind
                        .binding
                        .as_ref()
                        .is_some_and(|binding| binding.net_entity_id == entity_a)
                    && host.client_chat_window("c-bot100-re".to_owned()).is_empty()
                    && rejected.kind == ChatOpKind::Rejected;
            }
        }
    }
    scenarios.insert(
        "8".to_owned(),
        json!({ "ok": re_ok, "entityA": entity_a.to_string() }),
    );

    let entity_99 = host.must_self("c-bot99").net_entity_id;
    let account_99 = host.must_self("c-bot99").account_id;
    assert!(host.disconnect("c-bot99".to_owned()));
    host.advance_monotonic(RECONNECT_WINDOW_MS + 1_000);
    let expired = host.expire_due();
    let after_expiry = login_or_register(&account.uri(), "Bot99", TEST_PASSWORD, Some(&bot_claim))
        .await
        .unwrap_or_else(|_| empty_login());
    let mut expiry_ok = false;
    if after_expiry.accepted {
        if let Some(credential) = after_expiry.admission_credential {
            if verify_admission(&credential, ADMISSION_KEY_ID, &admission.public, now).is_ok() {
                let created_b =
                    host.admit(MAIN_ROOM.to_owned(), "c-bot99-b".to_owned(), credential);
                let tombstoned = host.query_attribute(AttributeQueryRequest {
                    caller_scope: AttributeQueryScope::ServerAuthoritative,
                    room_id: MAIN_ROOM.to_owned(),
                    net_entity_id: entity_99,
                    attribute_id: "EntityIdentity.entityType".to_owned(),
                    connection_generation: None,
                });
                expiry_ok = expired == 1
                    && created_b.accepted
                    && created_b.binding.as_ref().is_some_and(|binding| {
                        binding.net_entity_id != entity_99 && binding.account_id == account_99
                    })
                    && tombstoned.outcome == AttributeQueryOutcome::Tombstoned;
            }
        }
    }
    scenarios.insert(
        "9".to_owned(),
        json!({
            "ok": expiry_ok,
            "expired": expired,
            "entityA": entity_99.to_string(),
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
                let _ = host.admit_chat_input("iso-a".to_owned(), "iso-only".to_owned());
                let _ = host.run_tick(ISO_ROOM.to_owned());
                let cross = host.query_attribute(AttributeQueryRequest {
                    caller_scope: AttributeQueryScope::ServerAuthoritative,
                    room_id: ISO_ROOM.to_owned(),
                    net_entity_id: browser_binding.net_entity_id,
                    attribute_id: "EntityIdentity.entityType".to_owned(),
                    connection_generation: None,
                });
                let leaked = host
                    .client_chat_window("c-browser".to_owned())
                    .iter()
                    .any(|ev| ev.text == "iso-only");
                iso_ok = host.census(ISO_ROOM.to_owned()).total == 2
                    && host.client_chat_window("iso-b".to_owned()).len() == 1
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

    let scale_ok = full.total == 101 && chat_ok && event_order.len() == 101;
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
    let evidence = json!({
        "ok": all_ok,
        "blocked": blocked,
        "accountServer": account_meta(&options.account_server_dll, &account),
        "census": census_payload,
        "scenarios": scenarios,
        "browserWindow": window.iter().map(|ev| json!({
            "messageId": ev.message_id,
            "roomSequence": ev.room_sequence,
            "senderNetEntityId": ev.sender_net_entity_id.to_string(),
            "text": ev.text,
            "appliedTick": ev.applied_tick,
        })).collect::<Vec<_>>(),
    });
    write_evidence(out_dir, &evidence, &host_audit);
    evidence
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

fn census_payload(census: &super::RoomCensus) -> Value {
    json!({
        "botCount": census.bot_count,
        "playerCount": census.player_count,
        "total": census.total,
        "netEntityIds": census.net_entity_ids.iter().map(ToString::to_string).collect::<Vec<_>>(),
        "entityTypes": census.entity_types.iter().map(BoundEntityKind::as_str).collect::<Vec<_>>(),
    })
}

fn host_audit(host: &EntityChatHost, census: &super::RoomCensus, room_id: &str) -> String {
    let mut lines = Vec::new();
    for (i, id) in census.net_entity_ids.iter().enumerate() {
        let entity_type = census
            .entity_types
            .get(i)
            .map_or("bot", BoundEntityKind::as_str);
        let account_id = host
            .try_resolve_by_net_entity_id(room_id.to_owned(), *id)
            .map(|resolved| resolved.account_id)
            .unwrap_or_default();
        lines.push(
            json!({
                "kind": "entity_admitted",
                "roomId": room_id,
                "netEntityId": id.to_string(),
                "entityType": entity_type,
                "accountId": account_id,
            })
            .to_string(),
        );
    }
    lines.join("\n") + "\n"
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
