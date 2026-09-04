//! Process-local monotonic clocks. Wall-clock time is not used for deadlines.

use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::Arc;
use std::time::Instant;

/// Millisecond monotonic clock used by reconnect and host timers.
pub trait HostClock: Send + Sync {
    /// Milliseconds since this clock's origin.
    fn now_ms(&self) -> u64;
}

/// Shared handle to a [`HostClock`].
#[derive(Clone)]
pub struct SharedClock {
    inner: Arc<dyn HostClock>,
    test: Option<Arc<TestMonotonicClock>>,
}

impl SharedClock {
    /// Wraps any clock implementation. Production clocks cannot be advanced.
    #[must_use]
    pub fn new(clock: Arc<dyn HostClock>) -> Self {
        Self {
            inner: clock,
            test: None,
        }
    }

    /// System monotonic clock. There is no `advance_ms` backdoor.
    #[must_use]
    pub fn system() -> Self {
        Self::new(Arc::new(SystemMonotonicClock::new()))
    }

    /// Fake clock starting at zero. Tests inject time via [`Self::advance_ms`].
    #[must_use]
    pub fn test() -> Self {
        let clock = Arc::new(TestMonotonicClock::default());
        Self {
            inner: clock.clone(),
            test: Some(clock),
        }
    }

    /// Advances a Fake/test clock. Panics on a production clock.
    ///
    /// # Panics
    ///
    /// Panics when this handle wraps [`SystemMonotonicClock`].
    pub fn advance_ms(&self, delta_ms: u64) {
        self.test
            .as_ref()
            .expect("only Fake/test clocks can advance")
            .advance_ms(delta_ms);
    }
}

impl HostClock for SharedClock {
    fn now_ms(&self) -> u64 {
        self.inner.now_ms()
    }
}

/// Stopwatch-based monotonic clock. Production builds must not offset it.
pub struct SystemMonotonicClock {
    origin: Instant,
}

impl SystemMonotonicClock {
    /// Starts the clock at the current monotonic instant.
    #[must_use]
    pub fn new() -> Self {
        Self {
            origin: Instant::now(),
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
        u64::try_from(self.origin.elapsed().as_millis()).unwrap_or(u64::MAX)
    }
}

/// Deterministic clock that only moves when [`TestMonotonicClock::advance_ms`] is called.
#[derive(Default)]
pub struct TestMonotonicClock {
    now_ms: AtomicI64,
}

impl TestMonotonicClock {
    /// Injects elapsed milliseconds. Test-only; not present on production clocks.
    pub fn advance_ms(&self, delta_ms: u64) {
        let delta = i64::try_from(delta_ms).unwrap_or(i64::MAX);
        self.now_ms.fetch_add(delta, Ordering::SeqCst);
    }
}

impl HostClock for TestMonotonicClock {
    fn now_ms(&self) -> u64 {
        u64::try_from(self.now_ms.load(Ordering::SeqCst).max(0)).unwrap_or(0)
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
    fn host_clock_trait_and_system_clock_have_no_advance_ms() {
        let src = include_str!("clock.rs");
        let trait_start = src.find("pub trait HostClock").expect("trait");
        let trait_end = src[trait_start..].find('}').expect("trait end") + trait_start;
        assert!(
            !src[trait_start..=trait_end].contains("advance_ms"),
            "HostClock must not declare advance_ms"
        );
        let impl_start = src
            .find("impl HostClock for SystemMonotonicClock")
            .expect("system impl");
        let brace = src[impl_start..].find('{').expect("system impl body") + impl_start;
        let mut depth = 0;
        let mut impl_end = brace;
        for (index, ch) in src[brace..].char_indices() {
            match ch {
                '{' => depth += 1,
                '}' => {
                    depth -= 1;
                    if depth == 0 {
                        impl_end = brace + index;
                        break;
                    }
                }
                _ => {}
            }
        }
        assert!(
            !src[impl_start..=impl_end].contains("advance_ms"),
            "SystemMonotonicClock must not implement advance_ms"
        );
    }

    #[test]
    fn shared_test_clock_advance_is_visible() {
        let clock = SharedClock::test();
        clock.advance_ms(5_000);
        assert_eq!(clock.now_ms(), 5_000);
    }

    #[test]
    #[should_panic(expected = "only Fake/test clocks can advance")]
    fn production_clock_cannot_advance() {
        SharedClock::system().advance_ms(1);
    }
}
