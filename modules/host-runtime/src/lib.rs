//! Slice-scoped host-runtime: monotonic clock, bounded channels, supervised threads.
//!
//! This is the subset R-00359 consumes. Timer-wheel, cancel-tree depth, and the
//! rest of the Foundation surface stay out of this crate until a later card.

mod channel;
mod clock;
mod supervisor;

pub use channel::{bounded_channel, Receiver, RecvError, SendError, Sender};
pub use clock::{HostClock, SharedClock, SystemMonotonicClock, TestMonotonicClock};
pub use supervisor::{spawn_supervised, CancelToken, SupervisedTask, TaskPanicked};
