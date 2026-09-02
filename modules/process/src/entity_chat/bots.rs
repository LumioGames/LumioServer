//! Discover and spawn `Lumio.Client.Bot.Host`. S6 cadence is ClientTimerManager drain.

use std::fs::File;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

use serde_json::{json, Value};

use super::envelope::InputCommand;

/// Observed Client Timer Manager drain from a Bot.Host process.
#[derive(Debug, Clone, Default)]
pub struct ClientBotTrace {
    pub tick_source: String,
    pub utterance_ticks: Vec<u64>,
    pub timer_manager_invoked: bool,
    pub submitted: u32,
    pub pid: u32,
    pub blocked: Option<String>,
}

/// Live Bot.Host process whose Room sockets stay open until [`ClientBotFleet::release`].
pub struct ClientBotFleet {
    pub trace: ClientBotTrace,
    child: Option<Child>,
    release_path: PathBuf,
}

impl ClientBotFleet {
    /// Lets the hook dispose Room sockets and exit after Room observed chat.event.
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

/// Locates `Lumio.Client.Bot.Host` via `LUMIO_BOT_HOST` / `LUMIO_CLIENT_ROOT` or
/// a `LumioClient` sibling of this repo. Missing is BLOCKED.
///
/// # Errors
///
/// Returns a BLOCKED reason when no host dll/exe/csproj can be found.
pub fn discover_bot_host() -> Result<PathBuf, String> {
    discover_bot_host_in(&StdEnv, &process_repo_root())
}

pub(crate) fn discover_bot_host_in(env: &dyn BotHostEnv, repo: &Path) -> Result<PathBuf, String> {
    if let Ok(raw) = env.var("LUMIO_BOT_HOST") {
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
    if let Ok(root) = env.var("LUMIO_CLIENT_ROOT") {
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
        "BLOCKED: Lumio.Client.Bot.Host not found (set LUMIO_CLIENT_ROOT or LUMIO_BOT_HOST)"
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

/// Spawns `Lumio.Client.Bot.Host` so ClientTimerManager can drain native tickFrame.
///
/// # Errors
///
/// Returns BLOCKED when the host, hook, native ABI, or trace is missing.
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
    let bot_dll = bot_assembly_beside(&host)?;
    let hook = compile_startup_hook(out_dir, &bot_dll, dotnet)?;
    let spec_path = out_dir.join("fleet-spec.json");
    let trace_path = out_dir.join("timer-trace.json");
    let sent_path = out_dir.join("sent.txt");
    let release_path = out_dir.join("release.flag");
    let spec = json!({
        "roomUri": room_uri,
        "engineNative": engine_native.display().to_string(),
        "tracePath": trace_path.display().to_string(),
        "sentPath": sent_path.display().to_string(),
        "releasePath": release_path.display().to_string(),
        "advanceToTick": 15,
        "bots": envelopes.iter().map(|(connection, envelope)| {
            json!({
                "connectionId": connection,
                "envelope": envelope.to_json(),
            })
        }).collect::<Vec<_>>(),
    });
    std::fs::write(
        &spec_path,
        serde_json::to_string_pretty(&spec).map_err(|error| error.to_string())? + "\n",
    )
    .map_err(|error| error.to_string())?;

    let stdout_path = out_dir.join("bot-host.stdout");
    let stderr_path = out_dir.join("bot-host.stderr");
    let stdout = File::create(&stdout_path).map_err(|error| error.to_string())?;
    let stderr = File::create(&stderr_path).map_err(|error| error.to_string())?;
    let mut command = bot_host_command(dotnet, &host);
    command
        .env("DOTNET_STARTUP_HOOKS", &hook)
        .env("LUMIO_BOT_FLEET_SPEC", &spec_path)
        .env("LUMIO_ENGINE_NATIVE", engine_native)
        .env("DOTNET_NOLOGO", "1")
        .stdin(Stdio::null())
        .stdout(Stdio::from(stdout))
        .stderr(Stdio::from(stderr));
    let mut child = command
        .spawn()
        .map_err(|error| format!("BLOCKED: spawn Lumio.Client.Bot.Host: {error}"))?;
    let deadline = Instant::now() + Duration::from_secs(60);
    loop {
        on_progress();
        if trace_path.is_file() {
            break;
        }
        match child.try_wait() {
            Ok(Some(status)) => {
                if !trace_path.is_file() {
                    return Err(format!(
                        "BLOCKED: Lumio.Client.Bot.Host exited {status} without ClientTimerManager trace{}",
                        tail_logs(&stdout_path, &stderr_path)
                    ));
                }
                break;
            }
            Ok(None) => {}
            Err(error) => {
                return Err(format!("BLOCKED: Lumio.Client.Bot.Host wait: {error}"));
            }
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            return Err(format!(
                "BLOCKED: Lumio.Client.Bot.Host timed out waiting for ClientTimerManager drain{}",
                tail_logs(&stdout_path, &stderr_path)
            ));
        }
        thread::sleep(Duration::from_millis(50));
    }
    on_progress();
    let trace = match parse_trace(&trace_path) {
        Ok(trace) => trace,
        Err(reason) => {
            let _ = child.kill();
            let _ = child.wait();
            return Err(reason);
        }
    };
    Ok(ClientBotFleet {
        trace,
        child: Some(child),
        release_path,
    })
}

fn process_repo_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .and_then(Path::parent)
        .map(Path::to_path_buf)
        .unwrap_or_else(|| PathBuf::from("."))
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

fn bot_assembly_beside(host: &Path) -> Result<PathBuf, String> {
    let dir = host
        .parent()
        .ok_or_else(|| "BLOCKED: Lumio.Client.Bot.Host has no directory".to_owned())?;
    let dll = dir.join("Lumio.Client.Bot.dll");
    if dll.is_file() {
        Ok(dll)
    } else {
        Err("BLOCKED: Lumio.Client.Bot.dll missing beside Bot.Host".to_owned())
    }
}

fn hook_source() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("src/entity_chat/bot_startup_hook/StartupHook.cs")
}

/// Stops parent (Game evidence) Directory.Build.props from failing the hook build.
pub(crate) fn write_hook_isolation_files(hook_dir: &Path) -> Result<(), String> {
    std::fs::create_dir_all(hook_dir).map_err(|error| error.to_string())?;
    std::fs::write(
        hook_dir.join("Directory.Build.props"),
        r"<Project>
  <PropertyGroup>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <AnalysisLevel>none</AnalysisLevel>
    <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
  </PropertyGroup>
</Project>
",
    )
    .map_err(|error| error.to_string())?;
    std::fs::write(hook_dir.join("Directory.Build.targets"), "<Project />\n")
        .map_err(|error| error.to_string())?;
    std::fs::write(
        hook_dir.join("Directory.Packages.props"),
        r"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
",
    )
    .map_err(|error| error.to_string())?;
    Ok(())
}

pub(crate) fn hook_compile_failure_text(stdout: &[u8], stderr: &[u8]) -> String {
    let stderr = String::from_utf8_lossy(stderr);
    let stdout = String::from_utf8_lossy(stdout);
    let mut text = stderr.trim().to_owned();
    let stdout = stdout.trim();
    if !stdout.is_empty() {
        if !text.is_empty() {
            text.push('\n');
        }
        text.push_str(stdout);
    }
    text
}

fn compile_startup_hook(out_dir: &Path, bot_dll: &Path, dotnet: &str) -> Result<PathBuf, String> {
    let source = hook_source();
    if !source.is_file() {
        return Err(format!(
            "BLOCKED: Bot.Host startup hook source missing: {}",
            source.display()
        ));
    }
    let hook_dir = out_dir.join("bot-hook");
    write_hook_isolation_files(&hook_dir)?;
    std::fs::copy(&source, hook_dir.join("StartupHook.cs")).map_err(|error| error.to_string())?;
    let hint = bot_dll.display().to_string().replace('\\', "/");
    let csproj = format!(
        r#"<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <AssemblyName>Lumio.EntityChat.BotStartupHook</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="StartupHook.cs" />
    <Reference Include="Lumio.Client.Bot">
      <HintPath>{hint}</HintPath>
      <Private>true</Private>
    </Reference>
  </ItemGroup>
</Project>
"#
    );
    std::fs::write(hook_dir.join("BotHook.csproj"), csproj).map_err(|error| error.to_string())?;
    let output = Command::new(dotnet)
        .arg("build")
        .arg("BotHook.csproj")
        .arg("-c")
        .arg("Debug")
        .arg("--nologo")
        .current_dir(&hook_dir)
        .output()
        .map_err(|error| format!("BLOCKED: compile Bot.Host startup hook: {error}"))?;
    if !output.status.success() {
        return Err(format!(
            "BLOCKED: compile Bot.Host startup hook failed: {}",
            hook_compile_failure_text(&output.stdout, &output.stderr)
        ));
    }
    let dll = hook_dir
        .join("bin/Debug/net10.0/Lumio.EntityChat.BotStartupHook.dll")
        .canonicalize()
        .unwrap_or_else(|_| hook_dir.join("bin/Debug/net10.0/Lumio.EntityChat.BotStartupHook.dll"));
    if dll.is_file() {
        Ok(dll)
    } else {
        Err("BLOCKED: Bot.Host startup hook dll missing after build".to_owned())
    }
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

fn parse_trace(path: &Path) -> Result<ClientBotTrace, String> {
    let text = std::fs::read_to_string(path)
        .map_err(|error| format!("BLOCKED: ClientTimerManager trace missing: {error}"))?;
    let value: Value = serde_json::from_str(&text)
        .map_err(|error| format!("BLOCKED: ClientTimerManager trace is not JSON: {error}"))?;
    let utterance_ticks = value
        .get("utteranceTicks")
        .and_then(Value::as_array)
        .map(|rows| rows.iter().filter_map(Value::as_u64).collect::<Vec<u64>>())
        .unwrap_or_default();
    let tick_source = value
        .get("tickSource")
        .and_then(Value::as_str)
        .unwrap_or("")
        .to_owned();
    let blocked = value
        .get("blocked")
        .and_then(Value::as_str)
        .filter(|text| !text.is_empty())
        .map(str::to_owned);
    if let Some(reason) = blocked.clone() {
        return Err(reason);
    }
    Ok(ClientBotTrace {
        timer_manager_invoked: value
            .get("timerManagerInvoked")
            .and_then(Value::as_bool)
            .unwrap_or(false)
            && tick_source == "native-kernel/tickFrame"
            && !utterance_ticks.is_empty(),
        tick_source,
        utterance_ticks,
        submitted: u32::try_from(value.get("submitted").and_then(Value::as_u64).unwrap_or(0))
            .unwrap_or(u32::MAX),
        pid: u32::try_from(value.get("pid").and_then(Value::as_u64).unwrap_or(0)).unwrap_or(0),
        blocked,
    })
}

#[cfg(test)]
mod tests {
    use super::{
        discover_bot_host_in, hook_compile_failure_text, write_hook_isolation_files, BotHostEnv,
        ClientBotFleet, ClientBotTrace,
    };
    use std::collections::HashMap;
    use std::fs;

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
            err.contains("LUMIO_CLIENT_ROOT") || err.contains("LUMIO_BOT_HOST"),
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

    const GAME_LIKE_PROPS: &str = r"<Project>
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
  </PropertyGroup>
</Project>
";

    const ANALYZER_WARN_CS: &str = r#"using System.Runtime.InteropServices;
using System.Text.Json;
internal static class Warn
{
    public static string Go(int n)
    {
        var options = new JsonSerializerOptions();
        return n.ToString();
    }
    [DllImport("kernel32", CharSet = CharSet.Ansi)]
    private static extern int Native(string path);
}
"#;

    fn write_warn_csproj(dir: &std::path::Path) {
        fs::write(
            dir.join("Warn.csproj"),
            r#"<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AssemblyName>Lumio.EntityChat.BotHookWarn</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Warn.cs" />
  </ItemGroup>
</Project>
"#,
        )
        .expect("csproj");
        fs::write(dir.join("Warn.cs"), ANALYZER_WARN_CS).expect("cs");
    }

    fn dotnet_build(dir: &std::path::Path) -> std::process::Output {
        std::process::Command::new("dotnet")
            .arg("build")
            .arg("Warn.csproj")
            .arg("-c")
            .arg("Debug")
            .arg("--nologo")
            .current_dir(dir)
            .output()
            .expect("dotnet build")
    }

    #[test]
    fn game_parent_props_turn_hook_analyzer_warnings_into_errors() {
        let tmp = tempfile::tempdir().expect("tmp");
        fs::write(tmp.path().join("Directory.Build.props"), GAME_LIKE_PROPS).expect("game props");
        let hook_dir = tmp
            .path()
            .join("integration/entity-chat/evidence/round-1/client-bots/bot-hook");
        fs::create_dir_all(&hook_dir).expect("hook dir");
        write_warn_csproj(&hook_dir);
        let output = dotnet_build(&hook_dir);
        assert!(
            !output.status.success(),
            "Game TreatWarningsAsErrors must fail an unisolated hook build"
        );
        let text = format!(
            "{}{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
        assert!(
            text.contains("CA1869") || text.contains("CA2101") || text.contains("CA1305"),
            "expected analyzer errors on stdout, got {text}"
        );
    }

    #[test]
    fn isolated_hook_dir_builds_under_game_treat_warnings_as_errors() {
        let tmp = tempfile::tempdir().expect("tmp");
        fs::write(tmp.path().join("Directory.Build.props"), GAME_LIKE_PROPS).expect("game props");
        let hook_dir = tmp
            .path()
            .join("integration/entity-chat/evidence/round-1/client-bots/bot-hook");
        write_hook_isolation_files(&hook_dir).expect("isolate");
        write_warn_csproj(&hook_dir);
        let output = dotnet_build(&hook_dir);
        let text = format!(
            "{}{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
        assert!(
            output.status.success(),
            "isolated hook must build under Game Directory.Build.props, got {text}"
        );
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
            "suite release must create the hook wait file"
        );
    }

    #[test]
    fn hook_compile_blocked_text_includes_stdout_when_stderr_empty() {
        let text = hook_compile_failure_text(b"error CA1869: cache JsonSerializerOptions\n", b"");
        assert!(
            text.contains("CA1869"),
            "BLOCKED suffix must keep analyzer text from stdout, got {text}"
        );
    }
}
