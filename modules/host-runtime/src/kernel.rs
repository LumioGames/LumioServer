//! `NativeCore` timer ABI adapter types. Due decisions stay in the kernel.

use std::fmt::{Display, Formatter};

/// Kernel time domain. Matches C-4 `abiSurface.modes`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum TimerMode {
    /// Monotonic milliseconds. Drive with [`KernelTimer::pump_wall_clock`].
    WallClock = 0,
    /// Deterministic tick/frame. Drive with [`KernelTimer::advance_tick_frame`].
    TickFrame = 1,
}

/// 16-byte `TimerHandle` projection.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct KernelHandle {
    pub index: u32,
    pub generation: u32,
    pub context: u64,
}

/// One drain record after pump/advance.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct KernelFired {
    pub handle: KernelHandle,
    pub due: u64,
    pub schedule_sequence: u64,
    pub dispatch_id: u32,
}

/// Adapter-facing kernel port. Implementations call `NativeCore` `timer_*` or a
/// test double of that ABI; they must not invent a second due-decision kernel
/// in production.
pub trait KernelTimer: Send {
    /// One-shot in `mode`. `due` is ms for wallClock and tick id for tickFrame.
    ///
    /// # Errors
    ///
    /// Returns a kernel/ABI status string.
    fn schedule_one_shot(
        &mut self,
        mode: TimerMode,
        due: u64,
        dispatch_id: u32,
    ) -> Result<KernelHandle, KernelError>;

    /// Repeating timer.
    ///
    /// # Errors
    ///
    /// Returns a kernel/ABI status string.
    fn schedule_repeating(
        &mut self,
        mode: TimerMode,
        first_due: u64,
        interval: u64,
        dispatch_id: u32,
    ) -> Result<KernelHandle, KernelError>;

    /// Cancel a live handle.
    ///
    /// # Errors
    ///
    /// Returns a kernel/ABI status string.
    fn cancel(&mut self, handle: KernelHandle) -> Result<(), KernelError>;

    /// Pump wallClock with monotonic `now_ms` and drain firings.
    ///
    /// # Errors
    ///
    /// Returns a kernel/ABI status string.
    fn pump_wall_clock(&mut self, now_ms: u64) -> Result<Vec<KernelFired>, KernelError>;

    /// Advance tickFrame to `to_tick` and drain firings.
    ///
    /// # Errors
    ///
    /// Returns a kernel/ABI status string.
    fn advance_tick_frame(&mut self, to_tick: u64) -> Result<Vec<KernelFired>, KernelError>;
}

/// Kernel/ABI failure.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KernelError {
    pub status: i32,
    pub detail: String,
}

impl Display for KernelError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "kernel status {}: {}", self.status, self.detail)
    }
}

impl std::error::Error for KernelError {}
