//! Account Server process + login-or-register client. Secrets are never logged.

use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use futures_util::{SinkExt, StreamExt};
use serde_json::{json, Value};
use tokio_tungstenite::tungstenite::client::IntoClientRequest;
use tokio_tungstenite::tungstenite::http::HeaderValue;
use tokio_tungstenite::tungstenite::Message;

use super::crypto::hex_lower;

const SUBPROTOCOL: &str = "lumio-account-v1";
const READY_PREFIX: &str = "ACCOUNT_SERVER_READY ";

/// One login-or-register response. Password material is never stored.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AccountLoginResult {
    pub accepted: bool,
    pub account_newly_created: bool,
    pub account_id: Option<String>,
    pub login_name: Option<String>,
    pub admission_credential: Option<String>,
    pub error_code: Option<String>,
}

/// Child Account Server process.
pub struct AccountServerProcess {
    child: Child,
    pub port: u16,
    pub pid: u32,
}

impl AccountServerProcess {
    /// Starts `lumio-account-server` and waits for the ready line.
    ///
    /// # Errors
    ///
    /// Returns a human-readable failure when the process cannot start or ready
    /// is not observed.
    pub fn start(
        dll: &Path,
        store: &Path,
        admission_seed: &[u8],
        bot_public: &[u8],
        dotnet: &str,
    ) -> Result<Self, String> {
        if !dll.is_file() {
            return Err(format!("account-server dll not found: {}", dll.display()));
        }
        std::fs::create_dir_all(store).map_err(|error| error.to_string())?;
        let directory = dll
            .parent()
            .map(Path::to_path_buf)
            .unwrap_or_else(|| PathBuf::from("."));
        let mut command = Command::new(dotnet);
        command
            .current_dir(&directory)
            .arg("exec")
            .arg(dll)
            .arg("--store-path")
            .arg(store)
            .arg("--listen")
            .arg("127.0.0.1:0")
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .env("DOTNET_NOLOGO", "1")
            .env(
                "LUMIO_ACCOUNT_ADMISSION_PRIVATE_KEY_HEX",
                hex_lower(admission_seed),
            )
            .env(
                "LUMIO_ACCOUNT_BOT_TOOL_PUBLIC_KEY_HEX",
                hex_lower(bot_public),
            );
        if let Ok(root) = std::env::var("DOTNET_ROOT") {
            command.env("DOTNET_ROOT", root);
        }
        let mut child = command
            .spawn()
            .map_err(|error| format!("could not start account-server: {error}"))?;
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| "account-server stdout missing".to_owned())?;
        let mut lines = BufReader::new(stdout).lines();
        let deadline = Instant::now() + Duration::from_secs(30);
        while Instant::now() < deadline {
            let line = match lines.next() {
                Some(Ok(line)) => line,
                Some(Err(error)) => {
                    let _ = child.kill();
                    return Err(format!("account-server stdout: {error}"));
                }
                None => {
                    let _ = child.kill();
                    return Err("account-server exited before ready".to_owned());
                }
            };
            if let Some(ready) = parse_ready(&line) {
                return Ok(Self {
                    port: ready.0,
                    pid: ready.1,
                    child,
                });
            }
        }
        let _ = child.kill();
        Err("account-server ready line not observed".to_owned())
    }

    #[must_use]
    pub fn uri(&self) -> String {
        format!("ws://127.0.0.1:{}/", self.port)
    }
}

impl Drop for AccountServerProcess {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn parse_ready(line: &str) -> Option<(u16, u32)> {
    let json_text = line.strip_prefix(READY_PREFIX)?;
    let value: Value = serde_json::from_str(json_text).ok()?;
    let port = u16::try_from(value.get("port")?.as_u64()?).ok()?;
    let pid = u32::try_from(value.get("pid")?.as_u64()?).ok()?;
    if port == 0 || pid == 0 {
        return None;
    }
    Some((port, pid))
}

/// Submits login-or-register. Does not log the password or credential.
///
/// # Errors
///
/// Returns a transport failure. Domain rejects are `accepted = false`.
pub async fn login_or_register(
    uri: &str,
    login_name: &str,
    password: &str,
    bot_tool_credential: Option<&str>,
) -> Result<AccountLoginResult, String> {
    let mut request = uri
        .into_client_request()
        .map_err(|error| error.to_string())?;
    request.headers_mut().insert(
        "Sec-WebSocket-Protocol",
        HeaderValue::from_static(SUBPROTOCOL),
    );
    let (mut socket, _) = tokio_tungstenite::connect_async(request)
        .await
        .map_err(|error| error.to_string())?;
    let mut body = json!({
        "messageType": "LoginOrRegister",
        "loginName": login_name,
        "password": password,
    });
    if let Some(claim) = bot_tool_credential {
        body["botToolCredential"] = json!(claim);
    }
    socket
        .send(Message::Text(body.to_string().into()))
        .await
        .map_err(|error| error.to_string())?;
    let message = socket
        .next()
        .await
        .ok_or_else(|| "account-server closed".to_owned())?
        .map_err(|error| error.to_string())?;
    let text = message.into_text().map_err(|error| error.to_string())?;
    let _ = socket.close(None).await;
    parse_login(&text)
}

fn parse_login(text: &str) -> Result<AccountLoginResult, String> {
    let value: Value = serde_json::from_str(text).map_err(|error| error.to_string())?;
    let message_type = value
        .get("messageType")
        .and_then(Value::as_str)
        .unwrap_or("");
    if message_type == "LoginOrRegisterAck"
        && value.get("accepted").and_then(Value::as_bool) == Some(true)
    {
        return Ok(AccountLoginResult {
            accepted: true,
            account_newly_created: value
                .get("accountNewlyCreated")
                .and_then(Value::as_bool)
                .unwrap_or(false),
            account_id: value
                .get("accountId")
                .and_then(Value::as_str)
                .map(str::to_owned),
            login_name: value
                .get("loginName")
                .and_then(Value::as_str)
                .map(str::to_owned),
            admission_credential: value
                .get("admissionCredential")
                .and_then(Value::as_str)
                .map(str::to_owned),
            error_code: None,
        });
    }
    Ok(AccountLoginResult {
        accepted: false,
        account_newly_created: false,
        account_id: None,
        login_name: None,
        admission_credential: None,
        error_code: value
            .get("code")
            .and_then(Value::as_str)
            .map(str::to_owned)
            .or_else(|| Some("invalid_request".to_owned())),
    })
}
