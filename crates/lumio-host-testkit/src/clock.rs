use std::fmt;
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::Duration;

/// A monotonic instant represented independently from any wall clock.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct TestInstant(u64);

impl TestInstant {
    /// Returns the elapsed nanoseconds represented by this instant.
    #[must_use]
    pub const fn as_nanos(self) -> u64 {
        self.0
    }
}

/// A controlled clock for testing a host-runtime clock adapter.
#[derive(Clone, Debug, Default)]
pub struct TestMonotonicClock {
    nanos: Arc<Mutex<u64>>,
}

impl TestMonotonicClock {
    /// Creates a clock at the supplied monotonic offset.
    #[must_use]
    pub fn from_nanos(nanos: u64) -> Self {
        Self {
            nanos: Arc::new(Mutex::new(nanos)),
        }
    }

    /// Reads the current controlled instant.
    #[must_use]
    pub fn now(&self) -> TestInstant {
        TestInstant(*self.lock_nanos())
    }

    /// Advances the clock without sleeping or consulting wall-clock time.
    ///
    /// # Errors
    ///
    /// Returns [`ClockError::Overflow`] when the duration cannot be represented.
    pub fn advance(&self, duration: Duration) -> Result<TestInstant, ClockError> {
        let increment = u64::try_from(duration.as_nanos()).map_err(|_| ClockError::Overflow)?;
        let mut nanos = self.lock_nanos();
        *nanos = nanos.checked_add(increment).ok_or(ClockError::Overflow)?;
        Ok(TestInstant(*nanos))
    }

    /// Moves the clock to an absolute monotonic offset.
    ///
    /// # Errors
    ///
    /// Returns [`ClockError::WouldMoveBackwards`] when the supplied offset is
    /// earlier than the current instant.
    pub fn set_nanos(&self, nanos: u64) -> Result<TestInstant, ClockError> {
        let mut current = self.lock_nanos();
        if nanos < *current {
            return Err(ClockError::WouldMoveBackwards);
        }
        *current = nanos;
        Ok(TestInstant(*current))
    }

    fn lock_nanos(&self) -> MutexGuard<'_, u64> {
        self.nanos
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }
}

/// Error returned when a controlled monotonic-clock operation is invalid.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ClockError {
    /// The requested operation would move monotonic time backwards.
    WouldMoveBackwards,
    /// The requested instant cannot be represented as a `u64` nanosecond offset.
    Overflow,
}

impl fmt::Display for ClockError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::WouldMoveBackwards => formatter.write_str("clock cannot move backwards"),
            Self::Overflow => formatter.write_str("clock value overflowed"),
        }
    }
}

impl std::error::Error for ClockError {}
