//! Slice-scoped one-shot and periodic host timers.
//!
//! Callbacks fire when due on the host monotonic clock. This is not Native
//! Timer ABI (C-4); reconnect windows and tick pacing stay on this service.

use std::sync::{Arc, Mutex};
use std::time::Duration;

use crate::{
    bounded_channel, spawn_supervised, HostClock, RecvError, Sender, SharedClock, SupervisedTask,
};

/// Identifier returned by [`HostTimer::schedule_one_shot`] / [`schedule_periodic`].
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct TimerId(u64);

enum Callback {
    Once(Option<Box<dyn FnOnce() + Send>>),
    Periodic(Arc<dyn Fn() + Send + Sync>),
}

struct Job {
    id: u64,
    due_ms: u64,
    period_ms: Option<u64>,
    callback: Callback,
}

struct Inner {
    next_id: u64,
    jobs: Vec<Job>,
}

/// Host clock timer. Due callbacks run on the pump thread or [`Self::pump_due`].
pub struct HostTimer {
    clock: SharedClock,
    inner: Arc<Mutex<Inner>>,
    wakeup: Sender<()>,
    _pump: SupervisedTask,
}

impl HostTimer {
    /// Starts a supervised pump against `clock`.
    ///
    /// # Panics
    ///
    /// Panics when the timer mutex is poisoned.
    #[must_use]
    pub fn new(clock: SharedClock) -> Self {
        let inner = Arc::new(Mutex::new(Inner {
            next_id: 1,
            jobs: Vec::new(),
        }));
        let (wakeup, rx) = bounded_channel(8);
        let pump_inner = Arc::clone(&inner);
        let pump_clock = clock.clone();
        let pump = spawn_supervised("lumio-host-timer", move |cancel| loop {
            if cancel.is_cancelled() {
                break;
            }
            pump_due(&pump_clock, &pump_inner);
            let wait = next_wait(&pump_clock, &pump_inner);
            match rx.recv_timeout(wait) {
                Ok(()) | Err(RecvError::Empty) => {}
                Err(RecvError::Closed) => break,
            }
        });
        Self {
            clock,
            inner,
            wakeup,
            _pump: pump,
        }
    }

    /// Schedules `callback` once after `delay_ms` on the host clock.
    ///
    /// # Panics
    ///
    /// Panics when the timer mutex is poisoned.
    pub fn schedule_one_shot<F>(&self, delay_ms: u64, callback: F) -> TimerId
    where
        F: FnOnce() + Send + 'static,
    {
        self.push(delay_ms, None, Callback::Once(Some(Box::new(callback))))
    }

    /// Schedules `callback` every `period_ms` on the host clock.
    ///
    /// # Panics
    ///
    /// Panics when the timer mutex is poisoned.
    pub fn schedule_periodic<F>(&self, period_ms: u64, callback: F) -> TimerId
    where
        F: Fn() + Send + Sync + 'static,
    {
        let period = period_ms.max(1);
        self.push(period, Some(period), Callback::Periodic(Arc::new(callback)))
    }

    /// Drops a pending job. No-op when the id is unknown or already fired.
    ///
    /// # Panics
    ///
    /// Panics when the timer mutex is poisoned.
    pub fn cancel(&self, id: TimerId) {
        self.inner
            .lock()
            .expect("host timer mutex")
            .jobs
            .retain(|job| job.id != id.0);
    }

    /// Runs every job whose due time is `<= now`. Tests with a test clock call
    /// this after [`HostClock::advance_ms`].
    ///
    /// # Panics
    ///
    /// Panics when the timer mutex is poisoned.
    pub fn pump_due(&self) {
        pump_due(&self.clock, &self.inner);
    }

    fn push(&self, delay_ms: u64, period_ms: Option<u64>, callback: Callback) -> TimerId {
        let due_ms = self.clock.now_ms().saturating_add(delay_ms);
        let mut inner = self.inner.lock().expect("host timer mutex");
        let id = inner.next_id;
        inner.next_id = inner.next_id.saturating_add(1);
        inner.jobs.push(Job {
            id,
            due_ms,
            period_ms,
            callback,
        });
        drop(inner);
        let _ = self.wakeup.try_send(());
        TimerId(id)
    }
}

fn pump_due(clock: &SharedClock, inner: &Arc<Mutex<Inner>>) {
    let now = clock.now_ms();
    let mut once = Vec::new();
    let mut periodic = Vec::new();
    {
        let mut guard = inner.lock().expect("host timer mutex");
        let mut index = 0;
        while index < guard.jobs.len() {
            if guard.jobs[index].due_ms > now {
                index += 1;
                continue;
            }
            match &mut guard.jobs[index].callback {
                Callback::Once(slot) => {
                    if let Some(callback) = slot.take() {
                        once.push(callback);
                    }
                    guard.jobs.remove(index);
                }
                Callback::Periodic(callback) => {
                    let callback = Arc::clone(callback);
                    if let Some(period) = guard.jobs[index].period_ms {
                        guard.jobs[index].due_ms = now.saturating_add(period);
                    }
                    periodic.push(callback);
                    index += 1;
                }
            }
        }
    }
    for callback in once {
        callback();
    }
    for callback in periodic {
        callback();
    }
}

fn next_wait(clock: &SharedClock, inner: &Arc<Mutex<Inner>>) -> Duration {
    let now = clock.now_ms();
    let guard = inner.lock().expect("host timer mutex");
    let Some(due) = guard.jobs.iter().map(|job| job.due_ms).min() else {
        return Duration::from_millis(50);
    };
    if due <= now {
        Duration::from_millis(1)
    } else {
        Duration::from_millis((due - now).clamp(1, 50))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{bounded_channel, RecvError, SharedClock};

    #[test]
    fn one_shot_fires_after_clock_advance() {
        let clock = SharedClock::test();
        let timer = HostTimer::new(clock.clone());
        let (tx, rx) = bounded_channel(1);
        timer.schedule_one_shot(100, move || {
            let _ = tx.send(());
        });
        assert!(
            matches!(rx.try_recv(), Err(RecvError::Empty)),
            "one-shot must not run before it is due"
        );
        clock.advance_ms(100);
        timer.pump_due();
        rx.recv().expect("one-shot should fire after due");
    }

    #[test]
    fn periodic_fires_twice_then_cancel_stops() {
        let clock = SharedClock::test();
        let timer = HostTimer::new(clock.clone());
        let (tx, rx) = bounded_channel(8);
        let id = timer.schedule_periodic(10, move || {
            let _ = tx.try_send(());
        });
        clock.advance_ms(10);
        timer.pump_due();
        clock.advance_ms(10);
        timer.pump_due();
        assert_eq!(rx.try_recv(), Ok(()));
        assert_eq!(rx.try_recv(), Ok(()));
        timer.cancel(id);
        clock.advance_ms(10);
        timer.pump_due();
        assert!(
            matches!(rx.try_recv(), Err(RecvError::Empty | RecvError::Closed)),
            "cancelled periodic must not fire again"
        );
    }

    #[test]
    fn cancelled_one_shot_does_not_fire() {
        let clock = SharedClock::test();
        let timer = HostTimer::new(clock.clone());
        let (tx, rx) = bounded_channel(1);
        let id = timer.schedule_one_shot(50, move || {
            let _ = tx.send(());
        });
        timer.cancel(id);
        clock.advance_ms(50);
        timer.pump_due();
        assert!(
            matches!(rx.try_recv(), Err(RecvError::Empty | RecvError::Closed)),
            "cancelled one-shot must not fire"
        );
    }
}
