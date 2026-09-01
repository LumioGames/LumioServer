# entity-chat-host

Slice-scoped CoreCLR managed entry for R-00359. It loads `Lumio.Game.ServerGameplay.ChatRoomWorld` at runtime and exposes a JSON op protocol. Host process, clock, connections, admission and Room world-slot stay in Rust.

```text
cd entity-chat-host
dotnet build src/Lumio.Server.EntityChat.HostEntry/Lumio.Server.EntityChat.HostEntry.csproj
```

Entry: `Lumio.Server.EntityChat.HostEntry.HostEntry, Lumio.Server.EntityChat.HostEntry` / `LumioEntityChatEntry`.
