//! Discover and spawn `Lumio.Client.Bot.Host`. Evidence is its log directory.

use std::fs::File;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

use serde_json::Value;

use super::envelope::InputCommand;

const FLEET_WAIT: Duration = Duration::from_secs(15);
const R4_04_BLOCKED: &str = "BLOCKED: 等 R4-04";

/// Observed Bot.Host log evidence. Empty unless R4-04 Bot.Host wrote logs.
#[derive(Debug, Clone, Default)]
pub struct ClientBotTrace {
    pub tick_source: String,
    pub utterance_ticks: Vec<u64>,
    pub timer_manager_invoked: bool,
    pub submitted: u32,
    pub pid: u32,
    pub blocked: Option<String>,
}

/// Live Bot.Host process until [`ClientBotFleet::release`].
pub struct ClientBotFleet {
    pub trace: ClientBotTrace,
    child: Option<Child>,
    release_path: PathBuf,
}

impl ClientBotFleet {
    /// Signals Bot.Host to stop after Room observed chat.event.
    pub fn release(mut self) {
        self.release_mut();
    }

    fn release_mut(&mut self) {
        let _ = std::fs::write(&self.release_path, "release\n");
        let Some(mut child) = self.child.take() else {
            return;
        };
        let deadline = Instant::now() + Duration::from_secs(15);
        loop {
            match child.try_wait() {
                Ok(None) if Instant::now() >= deadline => {
                    let _ = child.kill();
                    let _ = child.wait();
                    break;
                }
                Ok(None) => thread::sleep(Duration::from_millis(50)),
                Ok(Some(_)) | Err(_) => break,
            }
        }
    }
}

impl Drop for ClientBotFleet {
    fn drop(&mut self) {
        self.release_mut();
    }
}

/// Env lookup used by discovery. Process env in production; map in unit tests.
pub trait BotHostEnv {
    /// Reads one environment variable.
    ///
    /// # Errors
    ///
    /// Returns [`std::env::VarError`] when the name is unset or invalid.
    fn var(&self, name: &str) -> Result<String, std::env::VarError>;
}

struct StdEnv;

impl BotHostEnv for StdEnv {
    fn var(&self, name: &str) -> Result<String, std::env::VarError> {
        std::env::var(name)
    }
}

struct BotHostLaunch {
    server: String,
    account_from: String,
    account_to: String,
    engine_native: PathBuf,
    log_dir: PathBuf,
}

/// Locates `Lumio.Client.Bot.Host` via `LumioClientRoot` / `LUMIO_CLIENT_ROOT` /
/// `LUMIO_BOT_HOST` or a `LumioClient` sibling of this repo. Missing is BLOCKED.
///
/// # Errors
///
/// Returns a BLOCKED reason when no host dll/exe/csproj can be found.
pub fn discover_bot_host() -> Result<PathBuf, String> {
    discover_bot_host_in(&StdEnv, &process_repo_root())
}

pub(crate) fn discover_bot_host_in(env: &dyn BotHostEnv, repo: &Path) -> Result<PathBuf, String> {
    if let Some(raw) = env_first(env, &["LUMIO_BOT_HOST"]) {
        let path = PathBuf::from(raw);
        if path.is_file() {
            return Ok(path);
        }
        if path.is_dir() {
            if let Some(found) = bot_host_in_dir(&path) {
                return Ok(found);
            }
        }
        return Err(format!(
            "BLOCKED: LUMIO_BOT_HOST missing: {}",
            path.display()
        ));
    }

    let mut roots = Vec::new();
    if let Some(root) = env_first(env, &["LumioClientRoot", "LUMIO_CLIENT_ROOT"]) {
        roots.push(PathBuf::from(root));
    }
    if let Some(parent) = repo.parent() {
        roots.push(parent.join("LumioClient"));
        if let Some(grand) = parent.parent() {
            roots.push(grand.join("LumioClient"));
        }
    }
    for root in roots {
        if !root.is_dir() {
            continue;
        }
        if let Some(found) = bot_host_under_client(&root) {
            return Ok(found);
        }
        let csproj = root.join("modules/bot/host/Lumio.Client.Bot.Host.csproj");
        if csproj.is_file() {
            return Ok(csproj);
        }
    }
    Err(
        "BLOCKED: Lumio.Client.Bot.Host not found (set LumioClientRoot, LUMIO_CLIENT_ROOT, or LUMIO_BOT_HOST)"
            .to_owned(),
    )
}

/// Builds Bot.Host when discovery returned a csproj; otherwise returns the file.
///
/// # Errors
///
/// Returns BLOCKED when `dotnet build` fails or the output dll is missing.
pub fn ensure_bot_host_executable(path: &Path, dotnet: &str) -> Result<PathBuf, String> {
    let ext = path.extension().and_then(|ext| ext.to_str()).unwrap_or("");
    if ext.eq_ignore_ascii_case("csproj") {
        return build_bot_host(path, dotnet);
    }
    if path.is_file() {
        return Ok(path.to_path_buf());
    }
    Err(format!(
        "BLOCKED: Lumio.Client.Bot.Host missing: {}",
        path.display()
    ))
}

/// Spawns `Lumio.Client.Bot.Host` and reads its log directory. No injection.
///
/// # Errors
///
/// Returns BLOCKED when the host is missing, or when logs are absent (R4-04).
pub fn run_client_bot_fleet<F>(
    bot_host: &Path,
    engine_native: &Path,
    room_uri: &str,
    envelopes: &[(String, InputCommand)],
    out_dir: &Path,
    dotnet: &str,
    mut on_progress: F,
) -> Result<ClientBotFleet, String>
where
    F: FnMut(),
{
    std::fs::create_dir_all(out_dir).map_err(|error| error.to_string())?;
    let host = ensure_bot_host_executable(bot_host, dotnet)?;
    let launch = bot_host_launch(room_uri, envelopes, engine_native, out_dir);
    let release_path = launch.log_dir.join("release.flag");
    let stdout_path = launch.log_dir.join("bot-host.stdout");
    let stderr_path = launch.log_dir.join("bot-host.stderr");
    let stdout = File::create(&stdout_path).map_err(|error| error.to_string())?;
    let stderr = File::create(&stderr_path).map_err(|error| error.to_string())?;
    let mut command = bot_host_command(dotnet, &host);
    apply_bot_host_launch(&mut command, &launch);
    command
        .env("DOTNET_NOLOGO", "1")
        .stdin(Stdio::null())
        .stdout(Stdio::from(stdout))
        .stderr(Stdio::from(stderr));
    let mut child = command
        .spawn()
        .map_err(|error| format!("BLOCKED: spawn Lumio.Client.Bot.Host: {error}"))?;
    let deadline = Instant::now() + FLEET_WAIT;
    loop {
        on_progress();
        if let Ok(trace) = read_bot_host_logs(&launch.log_dir) {
            return Ok(ClientBotFleet {
                trace,
                child: Some(child),
                release_path,
            });
        }
        match child.try_wait() {
            Ok(Some(status)) => {
                return Err(format!(
                    "{R4_04_BLOCKED}: Lumio.Client.Bot.Host exited {status} without log evidence{}",
                    tail_logs(&stdout_path, &stderr_path)
                ));
            }
            Ok(None) => {}
            Err(error) => {
                return Err(format!("BLOCKED: Lumio.Client.Bot.Host wait: {error}"));
            }
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            return Err(format!(
                "{R4_04_BLOCKED}: Lumio.Client.Bot.Host timed out without log evidence{}",
                tail_logs(&stdout_path, &stderr_path)
            ));
        }
        thread::sleep(Duration::from_millis(50));
    }
}

fn process_repo_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .and_then(Path::parent)
        .map(Path::to_path_buf)
        .unwrap_or_else(|| PathBuf::from("."))
}

fn env_first(env: &dyn BotHostEnv, names: &[&str]) -> Option<String> {
    for name in names {
        if let Ok(value) = env.var(name) {
            if !value.is_empty() {
                return Some(value);
            }
        }
    }
    None
}

fn bot_host_under_client(root: &Path) -> Option<PathBuf> {
    bot_host_in_dir(&root.join("modules/bot/host/bin/Debug/net10.0"))
        .or_else(|| bot_host_in_dir(&root.join("modules/bot/host/bin/Release/net10.0")))
}

fn bot_host_in_dir(dir: &Path) -> Option<PathBuf> {
    first_existing(&[
        dir.join("Lumio.Client.Bot.Host.dll"),
        dir.join("Lumio.Client.Bot.Host.exe"),
    ])
}

fn first_existing(candidates: &[PathBuf]) -> Option<PathBuf> {
    candidates.iter().find(|path| path.is_file()).cloned()
}

fn build_bot_host(csproj: &Path, dotnet: &str) -> Result<PathBuf, String> {
    let status = Command::new(dotnet)
        .arg("build")
        .arg(csproj)
        .arg("-c")
        .arg("Debug")
        .arg("--nologo")
        .status()
        .map_err(|error| format!("BLOCKED: dotnet build Lumio.Client.Bot.Host: {error}"))?;
    if !status.success() {
        return Err(format!(
            "BLOCKED: dotnet build Lumio.Client.Bot.Host failed: {status}"
        ));
    }
    let dir = csproj.parent().unwrap_or(csproj);
    bot_host_in_dir(&dir.join("bin/Debug/net10.0"))
        .or_else(|| bot_host_in_dir(&dir.join("bin/Release/net10.0")))
        .ok_or_else(|| "BLOCKED: Lumio.Client.Bot.Host.dll missing after dotnet build".to_owned())
}

fn bot_host_launch(
    server: &str,
    envelopes: &[(String, InputCommand)],
    engine_native: &Path,
    log_dir: &Path,
) -> BotHostLaunch {
    let count = if envelopes.is_empty() {
        super::BOT_COUNT
    } else {
        u32::try_from(envelopes.len())
            .unwrap_or(super::BOT_COUNT)
            .max(1)
    };
    BotHostLaunch {
        server: server.to_owned(),
        account_from: super::bot_name(1),
        account_to: super::bot_name(count),
        engine_native: engine_native.to_path_buf(),
        log_dir: log_dir.to_path_buf(),
    }
}

fn apply_bot_host_launch(command: &mut Command, launch: &BotHostLaunch) {
    command
        .arg("--server")
        .arg(&launch.server)
        .arg("--account-from")
        .arg(&launch.account_from)
        .arg("--account-to")
        .arg(&launch.account_to)
        .arg("--engine-native")
        .arg(&launch.engine_native)
        .arg("--log-dir")
        .arg(&launch.log_dir)
        .env("LumioBotServer", &launch.server)
        .env("LumioBotAccountFrom", &launch.account_from)
        .env("LumioBotAccountTo", &launch.account_to)
        .env("LumioEngineNative", &launch.engine_native)
        .env("LUMIO_ENGINE_NATIVE", &launch.engine_native)
        .env("LumioBotLogDir", &launch.log_dir);
}

fn bot_host_command(dotnet: &str, host: &Path) -> Command {
    let ext = host.extension().and_then(|ext| ext.to_str()).unwrap_or("");
    if ext.eq_ignore_ascii_case("dll") {
        let mut command = Command::new(dotnet);
        command.arg("exec").arg(host);
        command
    } else {
        Command::new(host)
    }
}

fn tail_logs(stdout_path: &Path, stderr_path: &Path) -> String {
    let stdout = std::fs::read_to_string(stdout_path).unwrap_or_default();
    let stderr = std::fs::read_to_string(stderr_path).unwrap_or_default();
    let mut logs = String::new();
    if !stdout.trim().is_empty() {
        logs.push_str(" stdout=");
        logs.push_str(stdout.trim());
    }
    if !stderr.trim().is_empty() {
        logs.push_str(" stderr=");
        logs.push_str(stderr.trim());
    }
    logs
}

fn is_bot_host_log_file(path: &Path) -> bool {
    let name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("");
    if name.eq_ignore_ascii_case("timer-trace.json")
        || name.eq_ignore_ascii_case("fleet-spec.json")
        || name.eq_ignore_ascii_case("release.flag")
    {
        return false;
    }
    let ext = path
        .extension()
        .and_then(|ext| ext.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    matches!(ext.as_str(), "ndjson" | "jsonl" | "log") || name == "bot-host.stdout"
}

fn read_bot_host_logs(log_dir: &Path) -> Result<ClientBotTrace, String> {
    let mut submitted = 0_u32;
    let mut utterance_ticks = Vec::new();
    let mut tick_source = String::new();
    let mut pid = 0_u32;
    let entries = match std::fs::read_dir(log_dir) {
        Ok(entries) => entries,
        Err(_) => {
            return Err(format!(
                "{R4_04_BLOCKED}: Lumio.Client.Bot.Host logs missing"
            ));
        }
    };
    for entry in entries.filter_map(Result::ok) {
        let path = entry.path();
        if !path.is_file() || !is_bot_host_log_file(&path) {
            continue;
        }
        let Ok(text) = std::fs::read_to_string(&path) else {
            continue;
        };
        for line in text.lines() {
            let trimmed = line.trim();
            if trimmed.is_empty() {
                continue;
            }
            let Ok(value) = serde_json::from_str::<Value>(trimmed) else {
                continue;
            };
            if let Some(source) = value.get("tickSource").and_then(Value::as_str) {
                if tick_source.is_empty() || source == "native-kernel/tickFrame" {
                    tick_source = source.to_owned();
                }
            }
            if let Some(process_id) = value.get("pid").and_then(Value::as_u64) {
                pid = u32::try_from(process_id).unwrap_or(pid);
            }
            if value.get("kind").and_then(Value::as_str) != Some("chat.input") {
                continue;
            }
            submitted = submitted.saturating_add(1);
            if let Some(tick) = value.get("tick").and_then(Value::as_u64) {
                utterance_ticks.push(tick);
            }
            if let Some(ticks) = value.get("utteranceTicks").and_then(Value::as_array) {
                for tick in ticks.iter().filter_map(Value::as_u64) {
                    utterance_ticks.push(tick);
                }
            }
        }
    }
    if submitted == 0 {
        return Err(format!(
            "{R4_04_BLOCKED}: Lumio.Client.Bot.Host logs missing chat.input lines"
        ));
    }
    utterance_ticks.sort_unstable();
    utterance_ticks.dedup();
    Ok(ClientBotTrace {
        timer_manager_invoked: tick_source == "native-kernel/tickFrame"
            && !utterance_ticks.is_empty(),
        tick_source,
        utterance_ticks,
        submitted,
        pid,
        blocked: None,
    })
}

#[cfg(test)]
mod tests {
    use super::{
        bot_host_launch, discover_bot_host_in, read_bot_host_logs, BotHostEnv, ClientBotFleet,
        ClientBotTrace, R4_04_BLOCKED,
    };
    use std::collections::HashMap;
    use std::fs;
    use std::path::Path;

    struct MapEnv(HashMap<String, String>);

    impl BotHostEnv for MapEnv {
        fn var(&self, name: &str) -> Result<String, std::env::VarError> {
            self.0
                .get(name)
                .cloned()
                .ok_or(std::env::VarError::NotPresent)
        }
    }

    #[test]
    fn missing_client_bot_host_is_blocked() {
        let repo = tempfile::tempdir().expect("tmp");
        let err = discover_bot_host_in(&MapEnv(HashMap::new()), repo.path()).unwrap_err();
        assert!(err.starts_with("BLOCKED:"), "{err}");
        assert!(
            err.contains("LumioClientRoot")
                || err.contains("LUMIO_CLIENT_ROOT")
                || err.contains("LUMIO_BOT_HOST"),
            "{err}"
        );
    }

    #[test]
    fn lumio_bot_host_file_is_discovered() {
        let tmp = tempfile::tempdir().expect("tmp");
        let host = tmp.path().join("Lumio.Client.Bot.Host.dll");
        fs::write(&host, []).expect("touch");
        let mut env = HashMap::new();
        env.insert(
            "LUMIO_BOT_HOST".to_owned(),
            host.to_string_lossy().into_owned(),
        );
        let found = discover_bot_host_in(&MapEnv(env), tmp.path()).expect("discover");
        assert_eq!(found, host);
    }

    #[test]
    fn lumio_client_root_csproj_is_discovered() {
        let tmp = tempfile::tempdir().expect("tmp");
        let csproj = tmp
            .path()
            .join("modules/bot/host/Lumio.Client.Bot.Host.csproj");
        fs::create_dir_all(csproj.parent().expect("dir")).expect("dirs");
        fs::write(&csproj, "<Project />").expect("csproj");
        let mut env = HashMap::new();
        env.insert(
            "LUMIO_CLIENT_ROOT".to_owned(),
            tmp.path().to_string_lossy().into_owned(),
        );
        let found = discover_bot_host_in(&MapEnv(env), tmp.path()).expect("discover");
        assert_eq!(found, csproj);
    }

    #[test]
    fn lumio_client_root_pascal_is_discovered() {
        let tmp = tempfile::tempdir().expect("tmp");
        let csproj = tmp
            .path()
            .join("modules/bot/host/Lumio.Client.Bot.Host.csproj");
        fs::create_dir_all(csproj.parent().expect("dir")).expect("dirs");
        fs::write(&csproj, "<Project />").expect("csproj");
        let mut env = HashMap::new();
        env.insert(
            "LumioClientRoot".to_owned(),
            tmp.path().to_string_lossy().into_owned(),
        );
        let found = discover_bot_host_in(&MapEnv(env), tmp.path()).expect("discover");
        assert_eq!(found, csproj);
    }

    #[test]
    fn launch_spec_uses_inclusive_bot_account_range() {
        let spec = bot_host_launch(
            "ws://127.0.0.1:1/",
            &[],
            Path::new("engine"),
            Path::new("logs"),
        );
        assert_eq!(spec.server, "ws://127.0.0.1:1/");
        assert_eq!(spec.account_from, "Bot01");
        assert_eq!(spec.account_to, "Bot100");
    }

    #[test]
    fn empty_log_dir_is_blocked_waiting_for_r4_04() {
        let tmp = tempfile::tempdir().expect("tmp");
        let err = read_bot_host_logs(tmp.path()).unwrap_err();
        assert!(err.starts_with(R4_04_BLOCKED), "{err}");
    }

    #[test]
    fn timer_trace_json_is_not_bot_host_log_evidence() {
        let tmp = tempfile::tempdir().expect("tmp");
        fs::write(
            tmp.path().join("timer-trace.json"),
            r#"{"kind":"chat.input","tickSource":"native-kernel/tickFrame","tick":5}"#,
        )
        .expect("trace");
        let err = read_bot_host_logs(tmp.path()).unwrap_err();
        assert!(err.starts_with(R4_04_BLOCKED), "{err}");
    }

    #[test]
    fn bot_host_ndjson_chat_input_is_log_evidence() {
        let tmp = tempfile::tempdir().expect("tmp");
        fs::write(
            tmp.path().join("bot-host.ndjson"),
            "{\"kind\":\"chat.input\",\"tickSource\":\"native-kernel/tickFrame\",\"tick\":5}\n",
        )
        .expect("ndjson");
        let trace = read_bot_host_logs(tmp.path()).expect("logs");
        assert_eq!(trace.tick_source, "native-kernel/tickFrame");
        assert!(trace.utterance_ticks.contains(&5));
        assert_eq!(trace.submitted, 1);
        assert!(trace.timer_manager_invoked);
        assert!(trace.blocked.is_none());
    }

    #[test]
    fn fleet_release_writes_release_path() {
        let tmp = tempfile::tempdir().expect("tmp");
        let release_path = tmp.path().join("release.flag");
        let fleet = ClientBotFleet {
            trace: ClientBotTrace::default(),
            child: None,
            release_path: release_path.clone(),
        };
        fleet.release();
        assert!(
            release_path.is_file(),
            "suite release must create the Bot.Host stop file"
        );
    }
}
