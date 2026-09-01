//! Bounded MPSC channel. Full and closed are explicit; there is no unbounded path.

use std::sync::mpsc::{self, RecvTimeoutError, TryRecvError, TrySendError};
use std::time::Duration;

/// Why a send failed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SendError<T> {
    /// Capacity is exhausted; the value is returned.
    Full(T),
    /// The receiver was dropped; the value is returned.
    Closed(T),
}

/// Why a receive failed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RecvError {
    /// The channel is empty.
    Empty,
    /// Every sender was dropped.
    Closed,
}

/// Sending end of a bounded channel.
#[derive(Clone)]
pub struct Sender<T> {
    inner: mpsc::SyncSender<T>,
}

/// Receiving end of a bounded channel.
pub struct Receiver<T> {
    inner: mpsc::Receiver<T>,
}

/// Creates a bounded MPSC channel with `capacity` slots.
///
/// # Panics
///
/// Panics when `capacity` is zero.
#[must_use]
pub fn bounded_channel<T>(capacity: usize) -> (Sender<T>, Receiver<T>) {
    assert!(capacity > 0, "bounded channel capacity must be positive");
    let (tx, rx) = mpsc::sync_channel(capacity);
    (Sender { inner: tx }, Receiver { inner: rx })
}

impl<T> Sender<T> {
    /// Non-blocking send.
    ///
    /// # Errors
    ///
    /// [`SendError::Full`] when the bound is reached, [`SendError::Closed`]
    /// when the receiver is gone.
    pub fn try_send(&self, value: T) -> Result<(), SendError<T>> {
        match self.inner.try_send(value) {
            Ok(()) => Ok(()),
            Err(TrySendError::Full(value)) => Err(SendError::Full(value)),
            Err(TrySendError::Disconnected(value)) => Err(SendError::Closed(value)),
        }
    }

    /// Blocking send used by owner-thread marshalling.
    ///
    /// # Errors
    ///
    /// [`SendError::Closed`] when the receiver is gone.
    pub fn send(&self, value: T) -> Result<(), SendError<T>> {
        self.inner
            .send(value)
            .map_err(|error| SendError::Closed(error.0))
    }
}

impl<T> Receiver<T> {
    /// Blocking receive.
    ///
    /// # Errors
    ///
    /// [`RecvError::Closed`] when every sender is gone.
    pub fn recv(&self) -> Result<T, RecvError> {
        self.inner.recv().map_err(|_| RecvError::Closed)
    }

    /// Non-blocking receive.
    ///
    /// # Errors
    ///
    /// [`RecvError::Empty`] or [`RecvError::Closed`].
    pub fn try_recv(&self) -> Result<T, RecvError> {
        match self.inner.try_recv() {
            Ok(value) => Ok(value),
            Err(TryRecvError::Empty) => Err(RecvError::Empty),
            Err(TryRecvError::Disconnected) => Err(RecvError::Closed),
        }
    }

    /// Receive with a timeout. Used only to observe cancel; not a business timer.
    ///
    /// # Errors
    ///
    /// [`RecvError::Empty`] on timeout, [`RecvError::Closed`] when disconnected.
    pub fn recv_timeout(&self, timeout: Duration) -> Result<T, RecvError> {
        match self.inner.recv_timeout(timeout) {
            Ok(value) => Ok(value),
            Err(RecvTimeoutError::Timeout) => Err(RecvError::Empty),
            Err(RecvTimeoutError::Disconnected) => Err(RecvError::Closed),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_when_full_instead_of_growing() {
        let (tx, rx) = bounded_channel::<u8>(1);
        tx.try_send(1).expect("first slot");
        match tx.try_send(2) {
            Err(SendError::Full(2)) => {}
            other => panic!("expected full, got {other:?}"),
        }
        assert_eq!(rx.recv().expect("drain"), 1);
        tx.try_send(3).expect("slot freed");
    }

    #[test]
    fn closed_receive_is_explicit() {
        let (tx, rx) = bounded_channel::<u8>(1);
        drop(tx);
        assert_eq!(rx.recv(), Err(RecvError::Closed));
    }
}
