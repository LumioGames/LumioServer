//! Structured JSON-line host log. Oracle (R4-09 / Game verify-evidence) reads this.

use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use serde_json::{json, Map, Value};

/// JSON-lines log. File sink for `--log-dir`; buffer sink for tests.
#[derive(Clone)]
pub struct NdjsonLog {
    inner: Arc<Mutex<NdjsonInner>>,
}

struct NdjsonInner {
    file: Option<File>,
    lines: Vec<String>,
}

impl NdjsonLog {
    /// Discards lines. Used when the caller did not pass `--log-dir`.
    #[must_use]
    pub fn null() -> Self {
        Self {
            inner: Arc::new(Mutex::new(NdjsonInner {
                file: None,
                lines: Vec::new(),
            })),
        }
    }

    /// Writes `{dir}/server.ndjson`. Missing parent dirs are created.
    ///
    /// # Errors
    ///
    /// Returns a filesystem error.
    pub fn create_dir(dir: &Path) -> Result<Self, String> {
        fs::create_dir_all(dir).map_err(|error| format!("BLOCKED: log-dir: {error}"))?;
        let path = dir.join("server.ndjson");
        let file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)
            .map_err(|error| format!("BLOCKED: log-dir {}: {error}", path.display()))?;
        Ok(Self {
            inner: Arc::new(Mutex::new(NdjsonInner {
                file: Some(file),
                lines: Vec::new(),
            })),
        })
    }

    /// In-memory sink for unit tests.
    #[must_use]
    pub fn buffer() -> Self {
        Self::null()
    }

    /// Appends one event. `kind` is required; extra fields are merged.
    pub fn emit(&self, kind: &str, tick: u64, extra: Map<String, Value>) {
        let mut payload = extra;
        payload
            .entry("ts".to_owned())
            .or_insert_with(|| Value::String(rfc3339_now()));
        payload.insert("kind".to_owned(), Value::String(kind.to_owned()));
        payload
            .entry("tick".to_owned())
            .or_insert_with(|| json!(tick));
        let line = Value::Object(payload).to_string();
        let mut inner = self.inner.lock().expect("log mutex");
        inner.lines.push(line.clone());
        if let Some(file) = inner.file.as_mut() {
            let _ = writeln!(file, "{line}");
            let _ = file.flush();
        }
    }

    /// Snapshot of emitted lines (tests / process-B compare).
    #[must_use]
    pub fn lines(&self) -> Vec<String> {
        self.inner.lock().expect("log mutex").lines.clone()
    }

    /// Directory used by [`Self::create_dir`], if any.
    #[must_use]
    pub fn path_hint(dir: &Path) -> PathBuf {
        dir.join("server.ndjson")
    }
}

fn rfc3339_now() -> String {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    format!("{}.{:03}Z", now.as_secs(), now.subsec_millis())
}
