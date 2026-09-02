//! Real Playwright Browser login against Account Server. Does not inject DOM chat.

use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use serde_json::{json, Value};

const STATIC_READY_PREFIX: &str = "STATIC_READY ";
const DEFAULT_GAME_ROOT: &str = r"C:\Work\LumioGames\wt-game\r-00354-live11";

/// Game worktree that owns `integration/entity-chat` static page + Playwright helper.
#[must_use]
pub fn game_root() -> PathBuf {
    std::env::var("LUMIO_GAME_ROOT")
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from(DEFAULT_GAME_ROOT))
}

/// Child static file server for the Game Browser page.
pub struct StaticServer {
    child: Child,
    pub port: u16,
}

impl StaticServer {
    /// Starts Game `static-server.mjs` and waits for `STATIC_READY`.
    ///
    /// # Errors
    ///
    /// Returns a human-readable failure when the process cannot start or ready
    /// is not observed.
    pub fn start(web_root: &Path, ready_file: &Path) -> Result<Self, String> {
        let script = game_root().join("integration/entity-chat/static-server.mjs");
        if !script.is_file() {
            return Err(format!("static-server.mjs missing: {}", script.display()));
        }
        if let Some(parent) = ready_file.parent() {
            std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
        }
        let mut child = Command::new("node")
            .arg(&script)
            .arg("--root")
            .arg(web_root)
            .arg("--port")
            .arg("0")
            .arg("--ready-file")
            .arg(ready_file)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|error| format!("static-server spawn: {error}"))?;
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| "static-server stdout missing".to_owned())?;
        let mut lines = BufReader::new(stdout).lines();
        let deadline = Instant::now() + Duration::from_secs(15);
        while Instant::now() < deadline {
            let line = match lines.next() {
                Some(Ok(line)) => line,
                Some(Err(error)) => {
                    let _ = child.kill();
                    return Err(format!("static-server stdout: {error}"));
                }
                None => {
                    let _ = child.kill();
                    return Err("static-server exited before ready".to_owned());
                }
            };
            if let Some(port) = parse_static_ready(&line) {
                return Ok(Self { child, port });
            }
        }
        let _ = child.kill();
        Err("static-server ready line not observed".to_owned())
    }
}

impl Drop for StaticServer {
    fn drop(&mut self) {
        if let Some(stdin) = self.child.stdin.as_mut() {
            let _ = stdin.write_all(b"shutdown\n");
        }
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn parse_static_ready(line: &str) -> Option<u16> {
    let json_text = line.strip_prefix(STATIC_READY_PREFIX)?;
    let value: Value = serde_json::from_str(json_text).ok()?;
    u16::try_from(value.get("port")?.as_u64()?)
        .ok()
        .filter(|port| *port != 0)
}

/// Honest Playwright capture. `ran` is never painted without a real browser.
#[derive(Debug, Clone)]
pub struct PlaywrightCapture {
    pub ran: bool,
    pub browser: Option<String>,
    pub received_from_network: bool,
    pub injected: bool,
    pub channel: Option<String>,
    pub error: Option<String>,
}

impl PlaywrightCapture {
    /// Failed launch / import. `ran` stays false.
    #[must_use]
    pub fn failed(reason: &str) -> Self {
        Self {
            ran: false,
            browser: None,
            received_from_network: false,
            injected: false,
            channel: None,
            error: Some(reason.to_owned()),
        }
    }

    /// Game `playwrightRan()` predicate.
    #[must_use]
    pub fn playwright_ran(&self) -> bool {
        self.ran
            && self.browser.as_deref().is_some_and(|browser| {
                let lower = browser.to_ascii_lowercase();
                lower.contains("chromium") || lower.contains("firefox") || lower.contains("webkit")
            })
            && self.received_from_network
            && !self.injected
    }

    /// Evidence payload. Password is never included.
    #[must_use]
    pub fn to_json(&self) -> Value {
        json!({
            "ran": self.ran,
            "browser": self.browser,
            "receivedFromNetwork": self.received_from_network,
            "injected": self.injected,
            "channel": self.channel,
            "error": self.error,
        })
    }
}

/// RFC 3986 query-component encode (Account WS URI in the page URL).
#[must_use]
pub fn encode_query_component(value: &str) -> String {
    let mut out = String::new();
    for &byte in value.as_bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                out.push(char::from(byte));
            }
            _ => {
                const HEX: &[u8; 16] = b"0123456789ABCDEF";
                out.push('%');
                out.push(char::from(HEX[(byte >> 4) as usize]));
                out.push(char::from(HEX[(byte & 0x0f) as usize]));
            }
        }
    }
    out
}

/// Spawns Game `runPlaywrightBrowser` via the thin wrapper. Never injects DOM events.
#[must_use]
pub fn run_playwright_browser(
    page_url: &str,
    password: &str,
    result_path: &Path,
    console_path: &Path,
) -> PlaywrightCapture {
    let wrapper =
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("tests/run_playwright_browser.mjs");
    if !wrapper.is_file() {
        return PlaywrightCapture::failed(&format!("wrapper missing: {}", wrapper.display()));
    }
    if let Some(parent) = result_path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let output = Command::new("node")
        .arg(&wrapper)
        .arg("--page-url")
        .arg(page_url)
        .arg("--password")
        .arg(password)
        .arg("--result-path")
        .arg(result_path)
        .arg("--console-path")
        .arg(console_path)
        .env("LUMIO_GAME_ROOT", game_root())
        .output();
    let output = match output {
        Ok(output) => output,
        Err(error) => return PlaywrightCapture::failed(&format!("node spawn: {error}")),
    };
    let stdout = String::from_utf8_lossy(&output.stdout);
    let line = stdout
        .lines()
        .rev()
        .find(|row| row.trim().starts_with('{'))
        .unwrap_or("")
        .trim();
    let Ok(value) = serde_json::from_str::<Value>(line) else {
        let err = String::from_utf8_lossy(&output.stderr);
        return PlaywrightCapture::failed(&format!(
            "playwright json missing: {}",
            first_line(&err)
        ));
    };
    PlaywrightCapture {
        ran: value.get("ran").and_then(Value::as_bool).unwrap_or(false),
        browser: value
            .get("browser")
            .and_then(Value::as_str)
            .map(str::to_owned),
        received_from_network: value
            .get("receivedFromNetwork")
            .and_then(Value::as_bool)
            .unwrap_or(false),
        injected: value
            .get("injected")
            .and_then(Value::as_bool)
            .unwrap_or(false),
        channel: value
            .get("channel")
            .and_then(Value::as_str)
            .map(str::to_owned),
        error: value
            .get("error")
            .and_then(Value::as_str)
            .map(str::to_owned),
    }
}

/// Runs static server + Playwright against the live Account Server.
#[must_use]
pub fn capture_browser_login(
    account_uri: &str,
    password: &str,
    out_dir: &Path,
) -> PlaywrightCapture {
    let web = game_root().join("integration/entity-chat/web");
    if !web.is_dir() {
        return PlaywrightCapture::failed(&format!("game web missing: {}", web.display()));
    }
    let ready = out_dir.join("static-ready.json");
    let static_server = match StaticServer::start(&web, &ready) {
        Ok(server) => server,
        Err(error) => return PlaywrightCapture::failed(&error),
    };
    let page_url = format!(
        "http://127.0.0.1:{}/index.html?account={}&login=Browser01",
        static_server.port,
        encode_query_component(account_uri)
    );
    let capture = run_playwright_browser(
        &page_url,
        password,
        &out_dir.join("browser-result.json"),
        &out_dir.join("browser-console.ndjson"),
    );
    drop(static_server);
    capture
}

fn first_line(text: &str) -> String {
    text.lines()
        .next()
        .unwrap_or(text)
        .trim()
        .chars()
        .take(200)
        .collect()
}
