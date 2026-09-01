# account-server

Independent C# process for RM-00011 login-or-register. Public field truth is architecture `engine/wire/account-port-v1.json` (`lumio.account-port.v1`, frozen at `2b7e321`); this directory does not define a second protocol.

```text
cd account-server
dotnet restore build.proj --locked-mode
dotnet build build.proj -c Release --no-restore
dotnet test tests/Lumio.Server.Account.Tests/Lumio.Server.Account.Tests.csproj -c Release --no-build
```

Listen only on loopback (`127.0.0.1:0`). Inject Ed25519 keys through the environment; never commit private keys.

```text
lumio-account-server --store-path <dir> [--listen 127.0.0.1:0] [--admission-key-id 0-255]
```

Environment (64 lowercase hex, no `0x` prefix):

- `LUMIO_ACCOUNT_ADMISSION_PRIVATE_KEY_HEX` — 32-byte Ed25519 seed
- `LUMIO_ACCOUNT_BOT_TOOL_PUBLIC_KEY_HEX` — 32-byte Bot-tool public key
- `LUMIO_ACCOUNT_ADMISSION_KEY_ID` — optional u8 key id (default 1)

Ready line: `ACCOUNT_SERVER_READY {"port":N,"pid":P,"contractId":"lumio.account-port.v1","storePath":S}`

Exit codes: 0 normal shutdown, 1 initialization failure, 2 fatal, 3 usage.
