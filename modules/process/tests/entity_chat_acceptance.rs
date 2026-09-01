//! R-00354 101-entity suite replay on the slice-scoped Rust host.
//! Each round is a child process because `CoreCLR` initializes once per process.

use std::path::Path;
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

#[test]
fn replays_the_r00354_suite_twice_on_the_rust_host() {
    let bin = env!("CARGO_BIN_EXE_lumio-entity-chat-replay");
    let out_dir = std::env::temp_dir().join("lumio-r-00359-entity-chat-evidence");
    let _ = std::fs::remove_dir_all(&out_dir);
    std::fs::create_dir_all(&out_dir).expect("out dir");

    let round1 = run_round(Path::new(bin), &out_dir.join("round-1"));
    let round2 = run_round(Path::new(bin), &out_dir.join("round-2"));

    let order1 = &round1["scenarios"]["11"]["eventOrder"];
    let order2 = &round2["scenarios"]["11"]["eventOrder"];
    let ticks1 = &round1["scenarios"]["11"]["appliedTicks"];
    let ticks2 = &round2["scenarios"]["11"]["appliedTicks"];
    assert_eq!(order1, order2, "event order differs across runs");
    assert_eq!(ticks1, ticks2, "applied Tick evidence differs across runs");
    assert_eq!(round1["census"]["total"], 101);
    assert_eq!(round2["census"]["total"], 101);

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
