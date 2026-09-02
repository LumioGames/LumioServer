//! R-00354 101-entity suite replay on the slice-scoped Rust host.
//! Each round is a child process because `CoreCLR` initializes once per process.

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::process::Command;

use serde_json::{json, Value};

fn run_round(bin: &Path, out_dir: &Path) -> Value {
    let _ = std::fs::remove_dir_all(out_dir);
    std::fs::create_dir_all(out_dir).expect("round dir");
    let status = Command::new(bin)
        .arg("--out")
        .arg(out_dir)
        .env(
            "DOTNET_ROOT",
            std::env::var("DOTNET_ROOT").unwrap_or_default(),
        )
        .status()
        .expect("spawn lumio-entity-chat-replay");
    let evidence_path = out_dir.join("evidence.json");
    let evidence = std::fs::read_to_string(&evidence_path)
        .ok()
        .and_then(|text| serde_json::from_str::<Value>(&text).ok())
        .unwrap_or_else(|| json!({ "ok": false, "blocked": "evidence.json missing" }));
    assert!(
        status.success() && evidence.get("ok") == Some(&Value::Bool(true)),
        "round failed status={status} evidence={}",
        evidence_path.display()
    );
    evidence
}

fn oracle_js() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("tests/verify_rust_evidence.mjs")
}

fn run_oracle(dir: &Path) {
    let output = Command::new("node")
        .arg(oracle_js())
        .arg("--dir")
        .arg(dir)
        .output()
        .expect("spawn rust evidence oracle");
    assert!(
        output.status.success(),
        "rust evidence oracle failed for {}: {}",
        dir.display(),
        String::from_utf8_lossy(&output.stdout)
    );
}

fn is_launcher_loop_index(id: &str) -> bool {
    id.parse::<u32>()
        .is_ok_and(|n| (1..=101).contains(&n) && id == n.to_string())
}

fn is_host_nent(id: &str) -> bool {
    let lower = id.to_ascii_lowercase();
    lower.starts_with("nent-") || lower.starts_with("nent_")
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
        if !is_host_nent(id) || is_launcher_loop_index(id) {
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
    let mut parts = entry.splitn(3, ':');
    let _nent = parts.next();
    match (parts.next(), parts.next()) {
        (Some(text), Some(seq)) => format!("{text}:{seq}"),
        _ => entry.to_owned(),
    }
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
        "BotEntity census must come from per-entity nent ids"
    );
    assert_eq!(
        players, 1,
        "PlayerEntity census must come from per-entity nent ids"
    );
    assert_eq!(
        ids.len(),
        101,
        "census total must be 101 distinct host NetEntityIds"
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
        !s5_traces.is_null() && s5_traces.as_object().is_some_and(|m| !m.is_empty()),
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
        is_host_nent(nent),
        "S8 netEntityId must be host nent_*, got {nent:?}"
    );
    assert_eq!(nent, prev, "S8 must rebind the same host NetEntityId");
    assert_ne!(nent, session, "S8 sessionId is not Entity A");
    assert!(!is_launcher_loop_index(nent));
}

#[test]
fn replays_the_r00354_suite_twice_on_the_rust_host() {
    let bin = env!("CARGO_BIN_EXE_lumio-entity-chat-replay");
    let out_dir = std::env::temp_dir().join("lumio-r-00359-entity-chat-evidence");
    let _ = std::fs::remove_dir_all(&out_dir);
    std::fs::create_dir_all(&out_dir).expect("out dir");

    let round1_dir = out_dir.join("round-1");
    let round2_dir = out_dir.join("round-2");
    let round1 = run_round(Path::new(bin), &round1_dir);
    let round2 = run_round(Path::new(bin), &round2_dir);

    assert_round_shape(&round1_dir, &round1);
    assert_round_shape(&round2_dir, &round2);

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

    run_oracle(&out_dir);
}
