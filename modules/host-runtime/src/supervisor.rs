//! Named supervised threads. Panic is captured and surfaced; it is not silent.

use std::panic::{self, AssertUnwindSafe};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::thread::{self, JoinHandle};

/// Cooperative cancel flag shared with a supervised thread.
#[derive(Clone, Debug, Default)]
pub struct CancelToken {
    cancelled: Arc<AtomicBool>,
}

impl CancelToken {
    /// Creates an open token.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// True after [`Self::cancel`].
    #[must_use]
    pub fn is_cancelled(&self) -> bool {
        self.cancelled.load(Ordering::SeqCst)
    }

    /// Requests cooperative stop. Idempotent.
    pub fn cancel(&self) {
        self.cancelled.store(true, Ordering::SeqCst);
    }
}

/// Panic captured from a supervised thread.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TaskPanicked {
    /// Thread name supplied at spawn.
    pub name: String,
    /// `Display` of the panic payload when it was a string.
    pub detail: String,
}

/// Join handle for a supervised named thread.
pub struct SupervisedTask {
    name: String,
    cancel: CancelToken,
    join: Option<JoinHandle<Option<TaskPanicked>>>,
}

impl SupervisedTask {
    /// The name passed to [`spawn_supervised`].
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Cancel token observed by the thread body.
    #[must_use]
    pub fn cancel_token(&self) -> CancelToken {
        self.cancel.clone()
    }

    /// Requests stop. The thread still has to observe the token or channel close.
    pub fn cancel(&self) {
        self.cancel.cancel();
    }

    /// Joins the thread and returns a panic event when the body unwound.
    pub fn join(&mut self) -> Option<TaskPanicked> {
        self.cancel.cancel();
        self.join
            .take()
            .and_then(|handle| handle.join().ok().flatten())
    }
}

impl Drop for SupervisedTask {
    fn drop(&mut self) {
        self.cancel.cancel();
        if let Some(handle) = self.join.take() {
            let _ = handle.join();
        }
    }
}

/// Spawns a named thread. Panic is caught and returned from [`SupervisedTask::join`].
///
/// # Panics
///
/// Panics when the OS refuses to create the thread.
pub fn spawn_supervised<F>(name: &str, body: F) -> SupervisedTask
where
    F: FnOnce(CancelToken) + Send + 'static,
{
    let cancel = CancelToken::new();
    let thread_cancel = cancel.clone();
    let thread_name = name.to_owned();
    let join = thread::Builder::new()
        .name(thread_name.clone())
        .spawn(move || {
            let result = panic::catch_unwind(AssertUnwindSafe(|| body(thread_cancel.clone())));
            match result {
                Ok(()) => None,
                Err(payload) => Some(TaskPanicked {
                    name: thread_name,
                    detail: panic_detail(payload.as_ref()),
                }),
            }
        })
        .unwrap_or_else(|error| panic!("failed to spawn supervised thread `{name}`: {error}"));
    SupervisedTask {
        name: name.to_owned(),
        cancel,
        join: Some(join),
    }
}

fn panic_detail(payload: &(dyn std::any::Any + Send)) -> String {
    payload
        .downcast_ref::<&str>()
        .map(|value| (*value).to_owned())
        .or_else(|| payload.downcast_ref::<String>().cloned())
        .unwrap_or_else(|| "unknown panic payload".to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::bounded_channel;
    use std::sync::mpsc::sync_channel;

    #[test]
    fn named_thread_runs_and_joins() {
        let (tx, rx) = sync_channel(1);
        let mut task = spawn_supervised("lumio-host-runtime-test", move |_| {
            tx.send(std::thread::current().name().map(str::to_owned))
                .expect("send");
        });
        let name = rx.recv().expect("name");
        assert_eq!(name.as_deref(), Some("lumio-host-runtime-test"));
        assert!(task.join().is_none());
    }

    #[test]
    fn panic_becomes_task_panicked() {
        let mut task = spawn_supervised("lumio-host-runtime-panic", |_| {
            panic!("owner exploded");
        });
        let event = task.join().expect("panic event");
        assert_eq!(event.name, "lumio-host-runtime-panic");
        assert!(event.detail.contains("owner exploded"));
    }

    #[test]
    fn cancel_is_visible_to_the_body() {
        let (tx, rx) = bounded_channel::<()>(1);
        let mut task = spawn_supervised("lumio-host-runtime-cancel", move |cancel| {
            while !cancel.is_cancelled() {
                if rx.recv().is_err() {
                    break;
                }
            }
        });
        task.cancel();
        drop(tx);
        assert!(task.join().is_none());
    }
}
