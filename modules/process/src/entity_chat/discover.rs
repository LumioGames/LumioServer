//! Artifact discovery for the slice replay. Missing files are BLOCKED.

use std::path::{Path, PathBuf};

use super::clr::ClrGameplayConfig;
use super::DEFAULT_INSTANCE_ID;

/// Account Server + CoreCLR files required to replay the suite.
pub struct ReplayArtifacts {
    pub account_server_dll: PathBuf,
    pub clr: ClrGameplayConfig,
}

/// Locates sibling build outputs. Missing items are named, never invented.
#[must_use]
pub fn discover() -> Result<ReplayArtifacts, String> {
    let repo = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .and_then(Path::parent)
        .map(Path::to_path_buf)
        .ok_or_else(|| "BLOCKED: cannot resolve LumioServer root".to_owned())?;

    let account = env_file("LUMIO_ACCOUNT_SERVER_DLL").or_else(|_| {
        first_existing(&[
            repo.join("account-server/src/Lumio.Server.Account.App/bin/Debug/net10.0/lumio-account-server.dll"),
            repo.join("account-server/src/Lumio.Server.Account.App/bin/Release/net10.0/lumio-account-server.dll"),
        ])
        .ok_or_else(|| "BLOCKED: account-server dll not found (set LUMIO_ACCOUNT_SERVER_DLL)".to_owned())
    })?;

    let host_entry_dir = env_dir("LUMIO_HOST_ENTRY_DIR").or_else(|_| {
        first_existing(&[
            repo.join("entity-chat-host/src/Lumio.Server.EntityChat.HostEntry/bin/Debug/net10.0"),
            repo.join("entity-chat-host/src/Lumio.Server.EntityChat.HostEntry/bin/Release/net10.0"),
        ])
        .ok_or_else(|| {
            "BLOCKED: entity-chat HostEntry build output not found (set LUMIO_HOST_ENTRY_DIR)"
                .to_owned()
        })
    })?;
    let assembly = host_entry_dir.join("Lumio.Server.EntityChat.HostEntry.dll");
    let runtime_config =
        host_entry_dir.join("Lumio.Server.EntityChat.HostEntry.runtimeconfig.json");
    if !assembly.is_file() || !runtime_config.is_file() {
        return Err("BLOCKED: entity-chat HostEntry dll/runtimeconfig missing".to_owned());
    }

    let replication = env_file("LUMIO_RUNTIME_REPLICATION_DLL").or_else(|_| {
        runtime_dll(&repo, "Lumio.GameRuntime.Replication.dll")
            .ok_or_else(|| "BLOCKED: Lumio.GameRuntime.Replication.dll not found (set LUMIO_RUNTIME_REPLICATION_DLL)".to_owned())
    })?;
    let ecs = env_file("LUMIO_RUNTIME_ECS_DLL").or_else(|_| {
        runtime_dll(&repo, "Lumio.GameRuntime.Ecs.dll").ok_or_else(|| {
            "BLOCKED: Lumio.GameRuntime.Ecs.dll not found (set LUMIO_RUNTIME_ECS_DLL)".to_owned()
        })
    })?;
    let username = env_file("LUMIO_USERNAME_SERVER_DLL").or_else(|_| {
        runtime_dll(&repo, "Lumio.GameRuntime.Samples.Username.Server.dll").ok_or_else(|| {
            "BLOCKED: Lumio.GameRuntime.Samples.Username.Server.dll not found (set LumioRuntimeRoot or LUMIO_USERNAME_SERVER_DLL)"
                .to_owned()
        })
    })?;
    let instance_id = std::env::var("LumioInstanceId")
        .or_else(|_| std::env::var("LUMIO_INSTANCE_ID"))
        .ok()
        .and_then(|text| {
            u64::from_str_radix(text.trim_start_matches("0x"), 16)
                .ok()
                .or_else(|| text.parse().ok())
        })
        .unwrap_or(DEFAULT_INSTANCE_ID);

    let hostfxr = env_file("LUMIO_HOSTFXR").or_else(|_| {
        discover_hostfxr()
            .ok_or_else(|| "BLOCKED: hostfxr missing (set LUMIO_HOSTFXR or DOTNET_ROOT)".to_owned())
    })?;
    let engine_native = env_file("LUMIO_ENGINE_NATIVE")?;

    Ok(ReplayArtifacts {
        account_server_dll: account,
        clr: ClrGameplayConfig {
            engine_native,
            hostfxr,
            runtime_config,
            assembly,
            entry_type:
                "Lumio.Server.EntityChat.HostEntry.HostEntry, Lumio.Server.EntityChat.HostEntry"
                    .to_owned(),
            entry_method: "LumioEntityChatEntry".to_owned(),
            replication_assembly: replication,
            ecs_assembly: ecs,
            username_server_assembly: username,
            instance_id,
        },
    })
}

fn env_file(var: &str) -> Result<PathBuf, String> {
    let path = PathBuf::from(std::env::var(var).map_err(|_| format!("BLOCKED: {var} is not set"))?);
    if path.is_file() {
        Ok(path)
    } else {
        Err(format!("BLOCKED: {var} missing: {}", path.display()))
    }
}

fn env_dir(var: &str) -> Result<PathBuf, String> {
    let path = PathBuf::from(std::env::var(var).map_err(|_| format!("BLOCKED: {var} is not set"))?);
    if path.is_dir() {
        Ok(path)
    } else {
        Err(format!("BLOCKED: {var} missing: {}", path.display()))
    }
}

fn runtime_dll(repo: &Path, file_name: &str) -> Option<PathBuf> {
    let root = std::env::var("LumioRuntimeRoot")
        .or_else(|_| std::env::var("LUMIO_RUNTIME_ROOT"))
        .map(PathBuf::from)
        .ok()
        .or_else(|| repo.parent().map(|parent| parent.join("LumioGameRuntime")))?;
    first_existing(&[
        root.join(format!(
            "modules/replication/src/Lumio.GameRuntime.Replication/bin/Debug/net10.0/{file_name}"
        )),
        root.join(format!(
            "modules/ecs/src/Lumio.GameRuntime.Ecs/bin/Debug/net10.0/{file_name}"
        )),
        root.join(format!(
            "modules/ecs/samples/username/bin/Debug/net10.0/{file_name}"
        )),
        root.join(format!(
            "modules/replication/src/Lumio.GameRuntime.Replication/bin/Release/net10.0/{file_name}"
        )),
        root.join(format!(
            "modules/ecs/src/Lumio.GameRuntime.Ecs/bin/Release/net10.0/{file_name}"
        )),
        root.join(format!(
            "modules/ecs/samples/username/bin/Release/net10.0/{file_name}"
        )),
    ])
}

fn discover_hostfxr() -> Option<PathBuf> {
    let root = PathBuf::from(std::env::var("DOTNET_ROOT").ok()?);
    let fxr = root.join("host/fxr");
    let mut versions = Vec::new();
    if let Ok(entries) = std::fs::read_dir(fxr) {
        for entry in entries.filter_map(Result::ok) {
            let dll = entry.path().join(hostfxr_name());
            if dll.is_file() {
                versions.push(dll);
            }
        }
    }
    versions.sort();
    versions.pop()
}

fn hostfxr_name() -> &'static str {
    if cfg!(windows) {
        "hostfxr.dll"
    } else if cfg!(target_os = "macos") {
        "libhostfxr.dylib"
    } else {
        "libhostfxr.so"
    }
}

fn first_existing(candidates: &[PathBuf]) -> Option<PathBuf> {
    candidates
        .iter()
        .find(|path| path.is_file() || path.is_dir())
        .cloned()
}
