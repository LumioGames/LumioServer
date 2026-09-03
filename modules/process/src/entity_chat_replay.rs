//! One-round entity-chat acceptance replay. Invoke twice in two processes.

use lumio_server_process::entity_chat::{
    discover, run_round_blocking, AttributeQueryScope, ClrGameplay, RuntimeQuery, RuntimeSurface,
    SuiteOptions, MAIN_ROOM,
};
use serde_json::{json, Value};
use std::env;
use std::path::{Path, PathBuf};
use std::process::ExitCode;

fn main() -> ExitCode {
    let mut out = None;
    let mut restore_snapshot = None;
    let mut log_dir = None;
    let mut args = env::args().skip(1);
    while let Some(flag) = args.next() {
        match flag.as_str() {
            "--out" => out = args.next().map(PathBuf::from),
            "--restore-snapshot" => restore_snapshot = args.next().map(PathBuf::from),
            "--log-dir" => log_dir = args.next().map(PathBuf::from),
            "--instance-id" => {
                if let Some(value) = args.next() {
                    env::set_var("LumioInstanceId", value);
                }
            }
            other => {
                eprintln!("unknown argument `{other}`");
                return ExitCode::from(3);
            }
        }
    }
    let Some(out_dir) = out.or(log_dir) else {
        eprintln!("missing --out <evidenceDir> (or --log-dir)");
        return ExitCode::from(3);
    };
    if let Some(snapshot) = restore_snapshot {
        return restore_only(&snapshot, &out_dir);
    }
    let artifacts = match discover() {
        Ok(artifacts) => artifacts,
        Err(error) => {
            eprintln!("BLOCKED: {error}");
            return ExitCode::from(2);
        }
    };
    let evidence = run_round_blocking(&SuiteOptions {
        out_dir,
        account_server_dll: artifacts.account_server_dll,
        dotnet: env::var("LUMIO_DOTNET").unwrap_or_else(|_| "dotnet".to_owned()),
        clr: Some(artifacts.clr),
    });
    if evidence.get("ok").and_then(serde_json::Value::as_bool) == Some(true)
        && evidence
            .get("blocked")
            .and_then(serde_json::Value::as_str)
            .is_none()
    {
        ExitCode::SUCCESS
    } else {
        ExitCode::from(1)
    }
}

fn restore_only(snapshot: &Path, out_dir: &Path) -> ExitCode {
    let _ = std::fs::create_dir_all(out_dir);
    let result_path = out_dir.join("restore-result.json");
    let write_result = |ok: bool, error: Option<&str>, compared: u64, equal: bool| {
        let body = json!({
            "ok": ok,
            "pid": std::process::id(),
            "process": "lumio-entity-chat-replay",
            "processB": std::process::id(),
            "error": error,
            "compared": compared,
            "lastMessageTextEqual": equal,
            "kind": "restore",
        });
        let _ = std::fs::write(
            &result_path,
            serde_json::to_string_pretty(&body).unwrap_or_default() + "\n",
        );
        let mut ndjson = json!({
            "kind": "restore",
            "tick": 0,
            "processB": std::process::id(),
            "lastMessageTextEqual": equal,
            "compared": compared,
            "windowAfter": 0,
        })
        .to_string();
        ndjson.push('\n');
        let _ = std::fs::write(out_dir.join("server.ndjson"), ndjson);
    };
    let artifacts = match discover() {
        Ok(artifacts) => artifacts,
        Err(error) => {
            write_result(false, Some(&error), 0, false);
            return ExitCode::from(2);
        }
    };
    let bytes = match std::fs::read(snapshot) {
        Ok(bytes) if !bytes.is_empty() => bytes,
        Ok(_) => {
            write_result(false, Some("snapshot is empty"), 0, false);
            return ExitCode::from(1);
        }
        Err(error) => {
            write_result(false, Some(&error.to_string()), 0, false);
            return ExitCode::from(1);
        }
    };
    let mut gameplay = match ClrGameplay::start(&artifacts.clr) {
        Ok(gameplay) => gameplay,
        Err(error) => {
            write_result(false, Some(&error), 0, false);
            return ExitCode::from(2);
        }
    };
    if let Err(error) = gameplay.restore(MAIN_ROOM, &bytes) {
        write_result(false, Some(&error), 0, false);
        return ExitCode::from(1);
    }
    let expected_path = snapshot
        .parent()
        .unwrap_or(out_dir)
        .join("last-messages.jsonl");
    let expected = match std::fs::read_to_string(&expected_path) {
        Ok(text) => text,
        Err(error) => {
            write_result(false, Some(&error.to_string()), 0, false);
            return ExitCode::from(1);
        }
    };
    let mut compared = 0_u64;
    let mut equal = true;
    let mut lines = String::new();
    for line in expected.lines() {
        if line.trim().is_empty() {
            continue;
        }
        let Ok(row) = serde_json::from_str::<Value>(line) else {
            continue;
        };
        let Some(net_entity_id) = row.get("netEntityId").and_then(Value::as_str) else {
            continue;
        };
        let want = row.get("lastMessageText").cloned();
        let got = gameplay.query_attribute(&RuntimeQuery {
            caller_scope: AttributeQueryScope::ServerAuthoritative,
            room_id: MAIN_ROOM.to_owned(),
            net_entity_id: net_entity_id.to_owned(),
            attribute_id: "ChatComponent.lastMessageText".to_owned(),
            connection_generation: None,
        });
        compared += 1;
        let same = match (got.value.as_deref(), want.as_ref()) {
            (Some(text), Some(Value::String(expected))) => text == expected,
            (None | Some(""), Some(Value::Null) | None) => true,
            _ => false,
        };
        if !same {
            equal = false;
        }
        lines.push_str(
            &json!({
                "kind": "restore.entity",
                "tick": 0,
                "netEntityId": net_entity_id,
                "lastMessageTextEqual": same,
                "lastMessageText": got.value,
            })
            .to_string(),
        );
        lines.push('\n');
    }
    write_result(equal && compared > 0, None, compared, equal && compared > 0);
    let mut ndjson = json!({
        "kind": "restore",
        "tick": 0,
        "processB": std::process::id(),
        "lastMessageTextEqual": equal && compared > 0,
        "compared": compared,
        "windowAfter": 0,
    })
    .to_string();
    ndjson.push('\n');
    ndjson.push_str(&lines);
    let _ = std::fs::write(out_dir.join("server.ndjson"), ndjson);
    if equal && compared > 0 {
        ExitCode::SUCCESS
    } else {
        ExitCode::from(1)
    }
}
