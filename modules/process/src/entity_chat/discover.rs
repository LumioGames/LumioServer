//! Sibling artifact discovery for the slice replay. Missing files are BLOCKED.

use std::path::{Path, PathBuf};

use super::clr::ClrGameplayConfig;

const DEFAULT_HOSTFXR: &str = r"C:\Users\g923\.dotnet\host\fxr\10.0.11\hostfxr.dll";
const DEFAULT_NATIVE: &str = r"C:\Work\LumioGames\LumioGameEngineArchitecture\.run\ab12bf280961a39632022f7c6f3be78f\win-x64\lumio_engine_native.dll";

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
        .ok_or_else(|| "cannot resolve LumioServer root".to_owned())?;

    let account = first_existing(&[
        repo.join("account-server/src/Lumio.Server.Account.App/bin/Debug/net10.0/lumio-account-server.dll"),
        repo.join("account-server/src/Lumio.Server.Account.App/bin/Release/net10.0/lumio-account-server.dll"),
        PathBuf::from(
            r"C:\Work\LumioGames\wt-server\r-00344\account-server\src\Lumio.Server.Account.App\bin\Debug\net10.0\lumio-account-server.dll",
        ),
    ])
    .ok_or_else(|| "account-server dll not found".to_owned())?;

    let host_entry_dir = first_existing(&[
        repo.join("entity-chat-host/src/Lumio.Server.EntityChat.HostEntry/bin/Debug/net10.0"),
        repo.join("entity-chat-host/src/Lumio.Server.EntityChat.HostEntry/bin/Release/net10.0"),
    ])
    .ok_or_else(|| "entity-chat HostEntry build output not found".to_owned())?;
    let assembly = host_entry_dir.join("Lumio.Server.EntityChat.HostEntry.dll");
    let runtime_config =
        host_entry_dir.join("Lumio.Server.EntityChat.HostEntry.runtimeconfig.json");
    if !assembly.is_file() || !runtime_config.is_file() {
        return Err("entity-chat HostEntry dll/runtimeconfig missing".to_owned());
    }

    let gameplay = first_existing(&[
        PathBuf::from(
            r"C:\Work\LumioGames\wt-game\r-00354\modules\server-gameplay\src\Lumio.Game.ServerGameplay\bin\Debug\net10.0\Lumio.Game.ServerGameplay.dll",
        ),
        PathBuf::from(
            r"C:\Work\LumioGames\wt-game\r-00354-review\modules\server-gameplay\src\Lumio.Game.ServerGameplay\bin\Debug\net10.0\Lumio.Game.ServerGameplay.dll",
        ),
        PathBuf::from(
            r"C:\Work\LumioGames\LumioGame\modules\server-gameplay\src\Lumio.Game.ServerGameplay\bin\Debug\net10.0\Lumio.Game.ServerGameplay.dll",
        ),
    ])
    .ok_or_else(|| "Lumio.Game.ServerGameplay.dll not found".to_owned())?;

    let hostfxr = PathBuf::from(
        std::env::var("LUMIO_HOSTFXR").unwrap_or_else(|_| DEFAULT_HOSTFXR.to_owned()),
    );
    if !hostfxr.is_file() {
        return Err(format!("hostfxr missing: {}", hostfxr.display()));
    }
    let engine_native = PathBuf::from(
        std::env::var("LUMIO_ENGINE_NATIVE").unwrap_or_else(|_| DEFAULT_NATIVE.to_owned()),
    );
    if !engine_native.is_file() {
        return Err(format!("native SDK missing: {}", engine_native.display()));
    }

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
            gameplay_assembly: gameplay,
        },
    })
}

fn first_existing(candidates: &[PathBuf]) -> Option<PathBuf> {
    candidates
        .iter()
        .find(|path| path.is_file() || path.is_dir())
        .cloned()
}
