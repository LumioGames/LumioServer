//! R-00354 101-entity suite replay on the consume-only Rust host.
//! Each round is a child process because `CoreCLR` initializes once per process.

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::process::Command;

use serde_json::{json, Value};

fn game_root() -> Result<String, String> {
    std::env::var("LUMIO_GAME_ROOT").map_err(|_| "BLOCKED: LUMIO_GAME_ROOT is not set".to_owned())
}

fn run_round(bin: &Path, out_dir: &Path) -> Value {
    let _ = std::fs::remove_dir_all(out_dir);
    std::fs::create_dir_all(out_dir).expect("round dir");
    let mut command = Command::new(bin);
    command.arg("--out").arg(out_dir);
    if let Ok(dotnet) = std::env::var("DOTNET_ROOT") {
        command.env("DOTNET_ROOT", dotnet);
    }
    if let Ok(game) = std::env::var("LUMIO_GAME_ROOT") {
        command.env("LUMIO_GAME_ROOT", game);
    }
    let status = command.status().expect("spawn lumio-entity-chat-replay");
    let evidence_path = out_dir.join("evidence.json");
    let evidence = std::fs::read_to_string(&evidence_path)
        .ok()
        .and_then(|text| serde_json::from_str::<Value>(&text).ok())
        .unwrap_or_else(|| json!({ "ok": false, "blocked": "evidence.json missing" }));
    assert!(
        evidence.get("blocked").and_then(Value::as_str).is_none(),
        "BLOCKED round {} status={status} evidence={}",
        out_dir.display(),
        evidence
    );
    assert!(
        status.success() && evidence.get("ok") == Some(&Value::Bool(true)),
        "round failed status={status} evidence={}",
        evidence_path.display()
    );
    evidence
}

fn game_oracle_js() -> Result<PathBuf, String> {
    Ok(PathBuf::from(game_root()?).join("integration/entity-chat/verify-evidence.mjs"))
}

fn run_oracle(dir: &Path) {
    let oracle = game_oracle_js().expect("BLOCKED: Game oracle path");
    let output = Command::new("node")
        .arg(&oracle)
        .arg("--dir")
        .arg(dir)
        .output()
        .expect("spawn Game verify-evidence.mjs");
    assert!(
        output.status.success(),
        "Game verify-evidence.mjs failed for {}: {}",
        dir.display(),
        String::from_utf8_lossy(&output.stdout)
    );
}

fn assert_identical_suite_stamps(evidence: &Value) {
    let empty = json!({});
    let pw = evidence.get("playwright").unwrap_or(&empty);
    assert_eq!(
        pw.get("ran"),
        Some(&Value::Bool(true)),
        "S3 requires a real Playwright run"
    );
    let browser = pw.get("browser").and_then(Value::as_str).unwrap_or("");
    assert!(
        browser.to_ascii_lowercase().contains("chromium")
            || browser.to_ascii_lowercase().contains("firefox")
            || browser.to_ascii_lowercase().contains("webkit"),
        "S3 browser must match chromium|firefox|webkit, got {browser:?}"
    );
    assert_eq!(pw.get("receivedFromNetwork"), Some(&Value::Bool(true)));
    assert_ne!(pw.get("injected"), Some(&Value::Bool(true)));
    let s3 = evidence.pointer("/scenarios/3").unwrap_or(&empty);
    assert_eq!(s3.get("playwrightRan"), Some(&Value::Bool(true)));

    let s6 = evidence.pointer("/scenarios/6").unwrap_or(&empty);
    assert_eq!(s6.get("timerManagerInvoked"), Some(&Value::Bool(true)));
    let tick_source = s6
        .get("tickSource")
        .and_then(Value::as_str)
        .or_else(|| {
            evidence
                .pointer("/traces/chat/tickSource")
                .and_then(Value::as_str)
        })
        .unwrap_or("");
    let tick_l = tick_source.to_ascii_lowercase();
    assert!(
        !tick_l.contains("for-loop") && !tick_l.contains("forloop"),
        "S6 tickSource must not be a for-loop, got {tick_source:?}"
    );
    assert!(
        tick_l.contains("tickframe") || tick_l.contains("kernel") || tick_l.contains("native"),
        "S6 tickSource must be kernel tickFrame, got {tick_source:?}"
    );
    assert_eq!(
        s6.get("cadence").and_then(Value::as_str),
        Some("kernel:tickFrame")
    );

    let s7 = evidence.pointer("/scenarios/7").unwrap_or(&empty);
    let source = s7
        .get("snapshotSource")
        .and_then(Value::as_str)
        .unwrap_or("");
    assert!(
        source == "lumio-entity-chat-replay" || source.contains("lumio-entity-chat-replay"),
        "S7 snapshotSource must name the rust replay, got {source:?}"
    );
    assert_ne!(source, "live-rust-host");
    assert_ne!(source, "live-mvp-host");
    assert!(
        s7.get("windowBeforeSnapshot")
            .and_then(Value::as_u64)
            .unwrap_or(0)
            > 0
    );
    assert_eq!(s7.get("historyCountMax").and_then(Value::as_i64), Some(0));
    if s7.get("ok") == Some(&Value::Bool(true)) {
        assert_eq!(s7.get("restoredWindow").and_then(Value::as_u64), Some(0));
        let persist = evidence.pointer("/traces/persist").unwrap_or(&empty);
        let pid_a = persist
            .pointer("/processA/pid")
            .and_then(Value::as_u64)
            .unwrap_or(0);
        let pid_b = persist
            .pointer("/processB/pid")
            .and_then(Value::as_u64)
            .unwrap_or(0);
        assert!(
            pid_a > 0 && pid_b > 0 && pid_a != pid_b,
            "S7 ok requires process A persist then process B restore"
        );
        let sha = persist
            .get("snapshotSha256")
            .and_then(Value::as_str)
            .unwrap_or("");
        assert_eq!(sha.len(), 64, "S7 ok requires snapshot file sha256");
    }
}

fn is_launcher_loop_index(id: &str) -> bool {
    id.parse::<u32>()
        .is_ok_and(|n| (1..=101).contains(&n) && id == n.to_string())
}

fn is_runtime_net_entity_id(id: &str) -> bool {
    let lower = id.to_ascii_lowercase();
    if is_launcher_loop_index(&lower) {
        return false;
    }
    (lower.len() == 32 && lower.chars().all(|c| c.is_ascii_hexdigit()))
        || lower.starts_with("nent-")
        || lower.starts_with("nent_")
}

fn census_from_audit(audit: &str) -> (usize, usize, HashSet<String>) {
    let mut bots = 0;
    let mut players = 0;
    let mut ids = HashSet::new();
    for line in audit.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }
        let Ok(row) = serde_json::from_str::<Value>(trimmed) else {
            continue;
        };
        let kind = row
            .get("kind")
            .or_else(|| row.get("event"))
            .and_then(Value::as_str)
            .unwrap_or("");
        if kind != "entity_admitted" && kind != "binding_committed" {
            continue;
        }
        let Some(id) = row.get("netEntityId").and_then(Value::as_str) else {
            continue;
        };
        if !is_runtime_net_entity_id(id) || is_launcher_loop_index(id) {
            continue;
        }
        if !ids.insert(id.to_owned()) {
            continue;
        }
        match row.get("entityType").and_then(Value::as_str) {
            Some("bot") => bots += 1,
            Some("player") => players += 1,
            _ => {}
        }
    }
    (bots, players, ids)
}

fn event_order_key(entry: &str) -> String {
    entry.to_owned()
}

fn assert_round_shape(round_dir: &Path, evidence: &Value) {
    let process = evidence
        .pointer("/hostProcess/process")
        .and_then(Value::as_str)
        .unwrap_or("");
    assert_ne!(
        process, "lumio-mvp-host",
        "rust evidence must not impersonate lumio-mvp-host"
    );
    assert!(
        process.contains("lumio-entity-chat-replay"),
        "hostProcess.process must name the rust replay binary, got {process:?}"
    );
    let pid = evidence
        .pointer("/hostProcess/pid")
        .and_then(Value::as_u64)
        .unwrap_or(0);
    assert!(pid > 0, "hostProcess.pid must be a live pid");

    let audit = std::fs::read_to_string(round_dir.join("host-audit.ndjson")).expect("host-audit");
    let (bots, players, ids) = census_from_audit(&audit);
    assert_eq!(
        bots, 100,
        "BotEntity census must come from per-entity Runtime NetEntityIds"
    );
    assert_eq!(
        players, 1,
        "PlayerEntity census must come from per-entity Runtime NetEntityIds"
    );
    assert_eq!(
        ids.len(),
        101,
        "census total must be 101 distinct Runtime NetEntityIds"
    );

    let empty = json!({});
    let s5_traces = evidence
        .pointer("/traces/queries")
        .cloned()
        .unwrap_or(json!({}));
    let s5_blob = format!(
        "{}{}",
        s5_traces,
        evidence.pointer("/scenarios/5").unwrap_or(&empty)
    )
    .to_ascii_lowercase();
    assert!(
        !s5_traces.is_null() && s5_traces.as_object().is_some_and(|map| !map.is_empty()),
        "S5 traces.queries must be real query results, not empty"
    );
    for needed in ["unauthorized", "invisible", "stale"] {
        assert!(s5_blob.contains(needed), "S5 missing {needed}");
    }

    let s6 = evidence.pointer("/scenarios/6").unwrap_or(&empty);
    assert_eq!(
        s6.get("messageType").and_then(Value::as_str),
        Some("InputCommand")
    );
    assert_eq!(
        s6.get("mappingId").and_then(Value::as_str),
        Some("chat.input")
    );
    let sha = s6
        .get("payloadSha256")
        .and_then(Value::as_str)
        .unwrap_or("");
    assert_eq!(sha.len(), 64, "payloadSha256");
    assert!(sha.chars().all(|c| matches!(c, '0'..='9' | 'a'..='f')));

    let s8 = evidence.pointer("/scenarios/8").cloned().unwrap_or(empty);
    let reconnect = evidence
        .pointer("/traces/reconnect")
        .cloned()
        .unwrap_or_else(|| s8.clone());
    let nent = reconnect
        .get("netEntityId")
        .or_else(|| s8.get("netEntityId"))
        .and_then(Value::as_str)
        .unwrap_or("");
    let prev = reconnect
        .get("previousNetEntityId")
        .or_else(|| s8.get("previousNetEntityId"))
        .and_then(Value::as_str)
        .unwrap_or("");
    let session = reconnect
        .get("sessionId")
        .or_else(|| s8.get("sessionId"))
        .and_then(Value::as_str)
        .unwrap_or("");
    assert!(
        is_runtime_net_entity_id(nent),
        "S8 netEntityId must be Runtime-issued, got {nent:?}"
    );
    assert_eq!(nent, prev, "S8 must rebind the same Runtime NetEntityId");
    assert_ne!(nent, session, "S8 sessionId is not Entity A");
    assert!(!is_launcher_loop_index(nent));
}

fn produce_two_round_pack(bin: &Path, out_dir: &Path) {
    let _ = std::fs::remove_dir_all(out_dir);
    std::fs::create_dir_all(out_dir).expect("out dir");

    let round1_dir = out_dir.join("round-1");
    let round2_dir = out_dir.join("round-2");
    let round1 = run_round(bin, &round1_dir);
    let round2 = run_round(bin, &round2_dir);

    assert_round_shape(&round1_dir, &round1);
    assert_round_shape(&round2_dir, &round2);
    assert_identical_suite_stamps(&round1);
    assert_identical_suite_stamps(&round2);

    let order1: Vec<String> = round1["scenarios"]["11"]["eventOrder"]
        .as_array()
        .expect("eventOrder")
        .iter()
        .filter_map(Value::as_str)
        .map(event_order_key)
        .collect();
    let order2: Vec<String> = round2["scenarios"]["11"]["eventOrder"]
        .as_array()
        .expect("eventOrder")
        .iter()
        .filter_map(Value::as_str)
        .map(event_order_key)
        .collect();
    let ticks1 = &round1["scenarios"]["11"]["appliedTicks"];
    let ticks2 = &round2["scenarios"]["11"]["appliedTicks"];
    assert_eq!(order1, order2, "event order differs across runs");
    assert_eq!(ticks1, ticks2, "applied Tick evidence differs across runs");
    assert_eq!(order1.len(), 101);
    assert_eq!(round1["census"]["botCount"], 100);
    assert_eq!(round2["census"]["playerCount"], 1);

    run_oracle(out_dir);

    let manifest = json!({
        "schemaVersion": 1,
        "tool": "lumio-entity-chat-rust-host/replay",
        "conclusion": "SUCCESS",
        "blocked": null,
        "rounds": [
            { "round": 1, "ok": true, "census": round1["census"] },
            { "round": 2, "ok": true, "census": round2["census"] },
        ],
    });
    std::fs::write(
        out_dir.join("manifest.json"),
        serde_json::to_string_pretty(&manifest).expect("manifest") + "\n",
    )
    .expect("write manifest");
}

#[test]
fn replays_the_r00354_suite_twice_on_the_rust_host() {
    let _ = game_root().expect("BLOCKED: LUMIO_GAME_ROOT is not set");
    let bin = Path::new(env!("CARGO_BIN_EXE_lumio-entity-chat-replay"));
    produce_two_round_pack(
        bin,
        &std::env::temp_dir().join("lumio-r-00374-entity-chat-evidence"),
    );
    produce_two_round_pack(
        bin,
        &std::env::temp_dir().join("lumio-r-00374-entity-chat-evidence-b"),
    );
}
