//! Slice-scoped host-runtime: monotonic clock, bounded channels, supervised
//! threads, and a one-shot/periodic host timer.

mod channel;
mod clock;
mod supervisor;
mod timer;

pub use channel::{bounded_channel, Receiver, RecvError, SendError, Sender};
pub use clock::{HostClock, SharedClock, SystemMonotonicClock, TestMonotonicClock};
pub use supervisor::{spawn_supervised, CancelToken, SupervisedTask, TaskPanicked};
pub use timer::{HostTimer, TimerId};
