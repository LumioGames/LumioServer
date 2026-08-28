use std::collections::VecDeque;
use std::fmt;
use std::num::NonZeroUsize;

/// A deterministic reference model for a bounded typed port.
///
/// This test probe does not implement a production port trait and does not
/// replace codec, authentication, permission, or queue adapters.
#[derive(Debug)]
pub struct BoundedPortProbe<T> {
    queue: VecDeque<T>,
    capacity: NonZeroUsize,
    closed: bool,
    accepted: u64,
    full_rejections: u64,
    closed_rejections: u64,
    received: u64,
}

impl<T> BoundedPortProbe<T> {
    /// Creates a probe with caller-supplied, non-zero capacity.
    #[must_use]
    pub fn new(capacity: NonZeroUsize) -> Self {
        Self {
            queue: VecDeque::with_capacity(capacity.get()),
            capacity,
            closed: false,
            accepted: 0,
            full_rejections: 0,
            closed_rejections: 0,
            received: 0,
        }
    }

    /// Attempts to enqueue a typed value without blocking.
    ///
    /// # Errors
    ///
    /// Returns the original value when the port is full or closed.
    pub fn try_send(&mut self, value: T) -> Result<(), ProbeSendError<T>> {
        if self.closed {
            self.closed_rejections = self.closed_rejections.saturating_add(1);
            return Err(ProbeSendError::Closed(value));
        }
        if self.queue.len() == self.capacity.get() {
            self.full_rejections = self.full_rejections.saturating_add(1);
            return Err(ProbeSendError::Full(value));
        }

        self.queue.push_back(value);
        self.accepted = self.accepted.saturating_add(1);
        Ok(())
    }

    /// Attempts to receive the oldest queued value without blocking.
    ///
    /// A closed probe remains drainable. It reports closed only after all
    /// accepted values have been received.
    ///
    /// # Errors
    ///
    /// Returns [`ProbeReceiveError::Empty`] for an open, empty probe and
    /// [`ProbeReceiveError::Closed`] for a closed, drained probe.
    pub fn try_receive(&mut self) -> Result<T, ProbeReceiveError> {
        if let Some(value) = self.queue.pop_front() {
            self.received = self.received.saturating_add(1);
            return Ok(value);
        }
        if self.closed {
            Err(ProbeReceiveError::Closed)
        } else {
            Err(ProbeReceiveError::Empty)
        }
    }

    /// Closes the send side. Calling this more than once is idempotent.
    pub fn close(&mut self) {
        self.closed = true;
    }

    /// Returns fixed-size observations without exposing the queued values.
    #[must_use]
    pub fn snapshot(&self) -> PortProbeSnapshot {
        PortProbeSnapshot {
            capacity: self.capacity.get(),
            depth: self.queue.len(),
            closed: self.closed,
            accepted: self.accepted,
            full_rejections: self.full_rejections,
            closed_rejections: self.closed_rejections,
            received: self.received,
        }
    }
}

/// Fixed-size counters observed from a bounded port probe.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PortProbeSnapshot {
    capacity: usize,
    depth: usize,
    closed: bool,
    accepted: u64,
    full_rejections: u64,
    closed_rejections: u64,
    received: u64,
}

impl PortProbeSnapshot {
    /// Returns the configured probe capacity.
    #[must_use]
    pub const fn capacity(self) -> usize {
        self.capacity
    }

    /// Returns the number of values waiting to be received.
    #[must_use]
    pub const fn depth(self) -> usize {
        self.depth
    }

    /// Reports whether the send side is closed.
    #[must_use]
    pub const fn is_closed(self) -> bool {
        self.closed
    }

    /// Returns the number of accepted sends.
    #[must_use]
    pub const fn accepted(self) -> u64 {
        self.accepted
    }

    /// Returns the number of values rejected because capacity was exhausted.
    #[must_use]
    pub const fn full_rejections(self) -> u64 {
        self.full_rejections
    }

    /// Returns the number of values rejected because the probe was closed.
    #[must_use]
    pub const fn closed_rejections(self) -> u64 {
        self.closed_rejections
    }

    /// Returns the number of values received successfully.
    #[must_use]
    pub const fn received(self) -> u64 {
        self.received
    }
}

/// A non-blocking send rejection that preserves ownership of the value.
#[derive(Debug, Eq, PartialEq)]
pub enum ProbeSendError<T> {
    /// The configured capacity is exhausted.
    Full(T),
    /// The send side has been closed.
    Closed(T),
}

impl<T> ProbeSendError<T> {
    /// Returns the value that was not accepted.
    #[must_use]
    pub fn into_inner(self) -> T {
        match self {
            Self::Full(value) | Self::Closed(value) => value,
        }
    }
}

impl<T> fmt::Display for ProbeSendError<T> {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Full(_) => formatter.write_str("bounded port probe is full"),
            Self::Closed(_) => formatter.write_str("bounded port probe is closed"),
        }
    }
}

impl<T> std::error::Error for ProbeSendError<T> where T: fmt::Debug {}

/// A non-blocking receive failure from a bounded port probe.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProbeReceiveError {
    /// The probe is open but currently contains no values.
    Empty,
    /// The probe is closed and all accepted values have been drained.
    Closed,
}

impl fmt::Display for ProbeReceiveError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Empty => formatter.write_str("bounded port probe is empty"),
            Self::Closed => formatter.write_str("bounded port probe is closed and drained"),
        }
    }
}

impl std::error::Error for ProbeReceiveError {}
