//! One-round entity-chat acceptance replay. Invoke twice in two processes.

use lumio_server_process::entity_chat::{discover, run_round_blocking, SuiteOptions};
use std::env;
use std::path::PathBuf;
use std::process::ExitCode;

fn main() -> ExitCode {
    let mut out = None;
    let mut args = env::args().skip(1);
    while let Some(flag) = args.next() {
        match flag.as_str() {
            "--out" => out = args.next().map(PathBuf::from),
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
