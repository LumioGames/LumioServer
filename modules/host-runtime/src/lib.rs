//! Slice-scoped host-runtime: monotonic clock, bounded channels, supervised
//! threads, and a `NativeCore` timer ABI adapter.

mod channel;
mod clock;
mod kernel;
mod native_timer;
mod supervisor;
mod timer;

pub use channel::{bounded_channel, Receiver, RecvError, SendError, Sender};
pub use clock::{HostClock, SharedClock, SystemMonotonicClock, TestMonotonicClock};
pub use kernel::{KernelError, KernelFired, KernelHandle, KernelTimer, TimerMode};
pub use native_timer::{engine_native_from_env, NativeAbiKernel};
pub use supervisor::{spawn_supervised, CancelToken, SupervisedTask, TaskPanicked};
pub use timer::HostTimer;
