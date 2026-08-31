//! `lumio-server` — MS-00002 Hello World dedicated server process.
//!
//! Composition root for this milestone: dynamic-port loopback WebSocket
//! listener, two-session admission, SDK DLL verification, `CoreCLR` runtime
//! bridge, authoritative tick routing and NDJSON audit. Wire truth is the
//! architecture repo's `engine/wire/hello-wire-v1.json`, loaded at startup
//! via `--wire-contract`; process behaviour (readiness, shutdown, exit codes,
//! audit vocabulary) follows its `process` block.
//!
//! Exit codes: 0 normal shutdown, 1 initialization failure, 2 fatal runtime
//! error, 3 argument error.

pub mod audit;
pub mod cli;
pub mod runtime_bridge;
pub mod sdk_loader;
pub mod server;
pub mod session;
pub mod wire;
pub mod world;

use std::io::Write as _;
use std::sync::Arc;
use std::sync::Mutex;

use tokio::io::AsyncBufReadExt;
use tokio::io::BufReader;

use crate::audit::AuditLog;
use crate::cli::Args;
use crate::runtime_bridge::ClrBridge;
use crate::runtime_bridge::ClrStart;
use crate::server::ServerConfig;
use crate::server::SERVER_READY_PREFIX;

/// Run the server until `shutdown` on stdin or Ctrl-C.
///
/// Performs the full startup sequence (contract -> audit -> SDK -> CLR host ->
/// listener) and the graceful shutdown sequence (sessions closed -> bridge
/// shutdown -> CLR destroyed -> audit flushed). Every startup failure prints
/// the offending check to stderr and maps to exit code 1.
///
/// # Panics
///
/// Never panics on expected failures; a panic here is a process-fatal bug.
pub async fn run(args: Args) -> i32 {
    let contract = match wire::load(&args.wire_contract) {
        Ok(contract) => contract,
        Err(error) => {
            eprintln!("initialization failure: {error}");
            return 1;
        }
    };

    let audit = match AuditLog::open(&args.audit_file) {
        Ok(audit) => Arc::new(Mutex::new(audit)),
        Err(error) => {
            eprintln!(
                "initialization failure: audit {}: {error}",
                args.audit_file.display()
            );
            return 1;
        }
    };

    let lease = match sdk_loader::load(&args.engine_native) {
        Ok(lease) => lease,
        Err(error) => {
            eprintln!("initialization failure: SDK load: {error}");
            return 1;
        }
    };

    let start = ClrStart {
        hostfxr: args.hostfxr.to_string_lossy().into_owned(),
        runtime_config: args.runtime_config.to_string_lossy().into_owned(),
        assembly: args.assembly.to_string_lossy().into_owned(),
        entry_type: args.entry_type.clone(),
        entry_method: args.entry_method.clone(),
    };
    let bridge = match ClrBridge::start(lease, &start) {
        Ok(bridge) => bridge,
        Err(error) => {
            eprintln!("initialization failure: CoreCLR host: {error}");
            return 1;
        }
    };

    let instance = match server::start(ServerConfig {
        bridge: Box::new(bridge),
        audit: Arc::clone(&audit),
        contract: contract.clone(),
    })
    .await
    {
        Ok(instance) => instance,
        Err(error) => {
            eprintln!("initialization failure: {error}");
            return 1;
        }
    };

    if let Err(error) = server::write_ready_file(
        &args.ready_file,
        instance.port,
        instance.pid,
        &contract.contract_id,
    ) {
        eprintln!(
            "initialization failure: ready file {}: {error}",
            args.ready_file.display()
        );
        return 1;
    }
    {
        let mut log = audit.lock().expect("audit lock");
        log.server_listening(instance.port, instance.pid, &contract.contract_id);
    }
    println!(
        "{SERVER_READY_PREFIX}{}",
        server::readiness_json(instance.port, instance.pid, &contract.contract_id)
    );
    let _ = std::io::stdout().flush();

    let reason = wait_for_shutdown_request().await;
    instance.request_shutdown();
    let outcome = instance.join().await;
    eprintln!(
        "shutting down ({reason}): closed {} session(s), reason {}",
        outcome.sessions, outcome.reason
    );
    0
}

/// Resolve the first shutdown trigger: a `shutdown` line on stdin or Ctrl-C.
async fn wait_for_shutdown_request() -> String {
    let (trigger_tx, mut trigger_rx) = tokio::sync::mpsc::channel::<&'static str>(1);
    let stdin_task = {
        let trigger_tx = trigger_tx.clone();
        let stdin = tokio::io::stdin();
        let mut lines = BufReader::new(stdin).lines();
        tokio::spawn(async move {
            while let Ok(Some(line)) = lines.next_line().await {
                if line.trim() == "shutdown" {
                    let _ = trigger_tx.send("stdin shutdown").await;
                    break;
                }
            }
        })
    };
    let ctrl_c_task = tokio::spawn(async move {
        if tokio::signal::ctrl_c().await.is_ok() {
            let _ = trigger_tx.send("ctrl-c").await;
        }
    });
    let reason = trigger_rx
        .recv()
        .await
        .unwrap_or("channel closed")
        .to_owned();
    stdin_task.abort();
    ctrl_c_task.abort();
    reason
}

#[cfg(test)]
mod tests {
    use super::*;

    fn base_args(dir: &std::path::Path) -> Args {
        Args {
            engine_native: dir.join("missing-sdk.dll"),
            hostfxr: dir.join("hostfxr.dll"),
            runtime_config: dir.join("runtimeconfig.json"),
            assembly: dir.join("game.dll"),
            entry_type: "Lumio.GameRuntime.HelloEntry.HelloEntry, Lumio.GameRuntime.HelloEntry"
                .to_owned(),
            entry_method: "lumio_hello_entry".to_owned(),
            wire_contract: dir.join("contract.json"),
            audit_file: dir.join("audit.ndjson"),
            ready_file: dir.join("ready.json"),
            client: None,
        }
    }

    fn write(path: &std::path::Path, body: &str) {
        std::fs::write(path, body).expect("write fixture");
    }

    fn valid_contract() -> String {
        r#"{
  "contractId": "lumio.hello-wire.v1",
  "transport": { "subprotocol": "lumio-hello-v1", "maxFrameBytes": 65536 },
  "roles": ["browser", "bot"],
  "limits": { "maxPayloadBytes": 4096, "maxSessions": 2, "ingressQueuePerSession": 64,
              "helloLogCapacity": 32, "handshakeTimeoutMs": 5000, "baselineTimeoutMs": 5000,
              "scenarioTimeoutMs": 30000 }
}"#
        .to_owned()
    }

    #[tokio::test]
    async fn run_rejects_a_wrong_contract_with_exit_1() {
        let dir = tempfile::tempdir().expect("tempdir");
        let mut args = base_args(dir.path());
        args.wire_contract = dir.path().join("bad.json");
        write(&args.wire_contract, "{\"contractId\":\"wrong\"}");
        assert_eq!(run(args).await, 1);
    }

    #[tokio::test]
    async fn run_rejects_a_missing_sdk_dll_with_exit_1() {
        let dir = tempfile::tempdir().expect("tempdir");
        let mut args = base_args(dir.path());
        write(&args.wire_contract, &valid_contract());
        args.engine_native = dir.path().join("nope.dll");
        let audit_path = args.audit_file.clone();
        assert_eq!(run(args).await, 1);
        // The audit sink was opened before the SDK check.
        assert!(audit_path.is_file());
    }

    #[test]
    fn stdout_readiness_prefix_is_stable() {
        assert_eq!(SERVER_READY_PREFIX, "SERVER_READY ");
        let _ = std::io::stdout().flush();
    }
}
