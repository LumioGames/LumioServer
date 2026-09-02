# entity-chat-host

Slice-scoped CoreCLR managed entry for R-00374. It loads Runtime `EntityBindingQuery` / `ChatCommandRuntime` / `EcsPersistSnapshotPipeline` and exposes a JSON op protocol. The Rust host owns connections, admission verify, NativeCore timer drain and Room WebSocket broadcast.

```text
cd entity-chat-host
dotnet build src/Lumio.Server.EntityChat.HostEntry/Lumio.Server.EntityChat.HostEntry.csproj
```

Boot JSON must include `replicationAssembly` and `ecsAssembly` paths (from `LUMIO_RUNTIME_REPLICATION_DLL` / `LUMIO_RUNTIME_ECS_DLL`). Missing artifacts are BLOCKED.

Entry: `Lumio.Server.EntityChat.HostEntry.HostEntry, Lumio.Server.EntityChat.HostEntry` / `LumioEntityChatEntry`.
