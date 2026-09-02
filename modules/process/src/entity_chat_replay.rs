//! One-round entity-chat acceptance replay. Invoke twice in two processes.

use lumio_server_process::entity_chat::{
    discover, run_round_blocking, ClrGameplay, RuntimeSurface, SuiteOptions, MAIN_ROOM,
};
use std::env;
use std::path::{Path, PathBuf};
use std::process::ExitCode;

fn main() -> ExitCode {
    let mut out = None;
    let mut restore_snapshot = None;
    let mut args = env::args().skip(1);
    while let Some(flag) = args.next() {
        match flag.as_str() {
            "--out" => out = args.next().map(PathBuf::from),
            "--restore-snapshot" => restore_snapshot = args.next().map(PathBuf::from),
            other => {
                eprintln!("unknown argument `{other}`");
                return ExitCode::from(3);
            }
        }
    }
    let Some(out_dir) = out else {
        eprintln!("missing --out <evidenceDir>");
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
    let write_result = |ok: bool, error: Option<&str>| {
        let body = serde_json::json!({
            "ok": ok,
            "pid": std::process::id(),
            "process": "lumio-entity-chat-replay",
            "error": error,
        });
        let _ = std::fs::write(
            &result_path,
            serde_json::to_string_pretty(&body).unwrap_or_default() + "\n",
        );
    };
    let artifacts = match discover() {
        Ok(artifacts) => artifacts,
        Err(error) => {
            write_result(false, Some(&error));
            return ExitCode::from(2);
        }
    };
    let bytes = match std::fs::read(snapshot) {
        Ok(bytes) if !bytes.is_empty() => bytes,
        Ok(_) => {
            write_result(false, Some("snapshot is empty"));
            return ExitCode::from(1);
        }
        Err(error) => {
            write_result(false, Some(&error.to_string()));
            return ExitCode::from(1);
        }
    };
    let mut gameplay = match ClrGameplay::start(&artifacts.clr) {
        Ok(gameplay) => gameplay,
        Err(error) => {
            write_result(false, Some(&error));
            return ExitCode::from(2);
        }
    };
    match gameplay.restore(MAIN_ROOM, &bytes) {
        Ok(()) => {
            write_result(true, None);
            ExitCode::SUCCESS
        }
        Err(error) => {
            write_result(false, Some(&error));
            ExitCode::from(1)
        }
    }
}
