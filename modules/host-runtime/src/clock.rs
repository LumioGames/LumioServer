//! Process-local monotonic clocks. Wall-clock time is not used for deadlines.

use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::Arc;
use std::time::Instant;

/// Millisecond monotonic clock used by reconnect and host timers.
pub trait HostClock: Send + Sync {
    /// Milliseconds since this clock's origin, plus any test offset.
    fn now_ms(&self) -> u64;

    /// Advances the clock. Production clocks still accept this so tests can
    /// inject the five-minute reconnect deadline without sleeping.
    fn advance_ms(&self, delta_ms: u64);
}

/// Shared handle to a [`HostClock`].
#[derive(Clone)]
pub struct SharedClock {
    inner: Arc<dyn HostClock>,
}

impl SharedClock {
    /// Wraps any clock implementation.
    #[must_use]
    pub fn new(clock: Arc<dyn HostClock>) -> Self {
        Self { inner: clock }
    }

    /// System monotonic clock with a test-advanceable offset.
    #[must_use]
    pub fn system() -> Self {
        Self::new(Arc::new(SystemMonotonicClock::new()))
    }

    /// Pure test clock starting at zero.
    #[must_use]
    pub fn test() -> Self {
        Self::new(Arc::new(TestMonotonicClock::default()))
    }
}

impl HostClock for SharedClock {
    fn now_ms(&self) -> u64 {
        self.inner.now_ms()
    }

    fn advance_ms(&self, delta_ms: u64) {
        self.inner.advance_ms(delta_ms);
    }
}

/// Stopwatch-based monotonic clock plus an injected offset.
pub struct SystemMonotonicClock {
    origin: Instant,
    offset_ms: AtomicI64,
}

impl SystemMonotonicClock {
    /// Starts the clock at the current monotonic instant.
    #[must_use]
    pub fn new() -> Self {
        Self {
            origin: Instant::now(),
            offset_ms: AtomicI64::new(0),
        }
    }
}

impl Default for SystemMonotonicClock {
    fn default() -> Self {
        Self::new()
    }
}

impl HostClock for SystemMonotonicClock {
    fn now_ms(&self) -> u64 {
        let elapsed = u64::try_from(self.origin.elapsed().as_millis()).unwrap_or(u64::MAX);
        let offset = self.offset_ms.load(Ordering::SeqCst);
        if offset >= 0 {
            elapsed.saturating_add(u64::try_from(offset).unwrap_or(0))
        } else {
            elapsed.saturating_sub(u64::try_from(offset.saturating_neg()).unwrap_or(0))
        }
    }

    fn advance_ms(&self, delta_ms: u64) {
        let delta = i64::try_from(delta_ms).unwrap_or(i64::MAX);
        self.offset_ms.fetch_add(delta, Ordering::SeqCst);
    }
}

/// Deterministic clock that only moves when [`HostClock::advance_ms`] is called.
#[derive(Default)]
pub struct TestMonotonicClock {
    now_ms: AtomicI64,
}

impl HostClock for TestMonotonicClock {
    fn now_ms(&self) -> u64 {
        u64::try_from(self.now_ms.load(Ordering::SeqCst).max(0)).unwrap_or(0)
    }

    fn advance_ms(&self, delta_ms: u64) {
        let delta = i64::try_from(delta_ms).unwrap_or(i64::MAX);
        self.now_ms.fetch_add(delta, Ordering::SeqCst);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_clock_starts_at_zero_and_advances() {
        let clock = TestMonotonicClock::default();
        assert_eq!(clock.now_ms(), 0);
        clock.advance_ms(300_000);
        assert_eq!(clock.now_ms(), 300_000);
    }

    #[test]
    fn system_clock_advance_is_visible() {
        let clock = SystemMonotonicClock::new();
        let before = clock.now_ms();
        clock.advance_ms(5_000);
        assert!(clock.now_ms() >= before + 5_000);
    }
}
