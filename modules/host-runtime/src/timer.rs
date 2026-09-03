//! Host adapter over [`crate::KernelTimer`]. Due decisions stay in the kernel.

use std::sync::Mutex;

use crate::clock::HostClock;
use crate::kernel::{KernelError, KernelFired, KernelHandle, KernelTimer, TimerMode};
use crate::SharedClock;

/// Adapter that translates host delay/tick requests into kernel schedule/pump.
pub struct HostTimer {
    clock: SharedClock,
    kernel: Mutex<Box<dyn KernelTimer>>,
}

impl HostTimer {
    /// Wraps an already-constructed kernel adapter.
    #[must_use]
    pub fn new(clock: SharedClock, kernel: Box<dyn KernelTimer>) -> Self {
        Self {
            clock,
            kernel: Mutex::new(kernel),
        }
    }

    /// Schedules a wallClock one-shot `delay_ms` from the host clock origin.
    ///
    /// # Errors
    ///
    /// Returns the kernel status.
    ///
    /// # Panics
    ///
    /// Panics when the kernel mutex is poisoned.
    pub fn schedule_wall_one_shot(
        &self,
        delay_ms: u64,
        dispatch_id: u32,
    ) -> Result<KernelHandle, KernelError> {
        let due = self.clock.now_ms().saturating_add(delay_ms);
        self.kernel.lock().expect("kernel mutex").schedule_one_shot(
            TimerMode::WallClock,
            due,
            dispatch_id,
        )
    }

    /// Schedules tickFrame repeating from tick 1.
    ///
    /// # Errors
    ///
    /// Returns the kernel status.
    ///
    /// # Panics
    ///
    /// Panics when the kernel mutex is poisoned.
    pub fn schedule_tick_repeating(
        &self,
        interval: u64,
        dispatch_id: u32,
    ) -> Result<KernelHandle, KernelError> {
        self.kernel
            .lock()
            .expect("kernel mutex")
            .schedule_repeating(TimerMode::TickFrame, 1, interval.max(1), dispatch_id)
    }

    /// Pumps wallClock at the current host clock reading.
    ///
    /// # Errors
    ///
    /// Returns the kernel status.
    ///
    /// # Panics
    ///
    /// Panics when the kernel mutex is poisoned.
    pub fn pump_wall_clock(&self) -> Result<Vec<KernelFired>, KernelError> {
        let now = self.clock.now_ms();
        self.kernel
            .lock()
            .expect("kernel mutex")
            .pump_wall_clock(now)
    }

    /// Advances tickFrame to `to_tick`.
    ///
    /// # Errors
    ///
    /// Returns the kernel status.
    ///
    /// # Panics
    ///
    /// Panics when the kernel mutex is poisoned.
    pub fn advance_tick_frame(&self, to_tick: u64) -> Result<Vec<KernelFired>, KernelError> {
        self.kernel
            .lock()
            .expect("kernel mutex")
            .advance_tick_frame(to_tick)
    }

    /// Cancels a kernel handle.
    ///
    /// # Errors
    ///
    /// Returns the kernel status.
    ///
    /// # Panics
    ///
    /// Panics when the kernel mutex is poisoned.
    pub fn cancel(&self, handle: KernelHandle) -> Result<(), KernelError> {
        self.kernel.lock().expect("kernel mutex").cancel(handle)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::kernel::KernelError;
    use crate::SharedClock;

    struct ScriptedKernel {
        one_shots: Vec<(u64, u32, KernelHandle)>,
        repeating: Vec<(u64, u64, u32, KernelHandle)>,
        next: u32,
        committed_ms: u64,
        committed_tick: u64,
    }

    impl ScriptedKernel {
        fn new() -> Self {
            Self {
                one_shots: Vec::new(),
                repeating: Vec::new(),
                next: 1,
                committed_ms: 0,
                committed_tick: 0,
            }
        }

        fn alloc(&mut self) -> KernelHandle {
            let handle = KernelHandle {
                index: self.next,
                generation: 1,
                context: 1,
            };
            self.next += 1;
            handle
        }
    }

    impl KernelTimer for ScriptedKernel {
        fn schedule_one_shot(
            &mut self,
            mode: TimerMode,
            due: u64,
            dispatch_id: u32,
        ) -> Result<KernelHandle, KernelError> {
            assert_eq!(mode, TimerMode::WallClock);
            let handle = self.alloc();
            self.one_shots.push((due, dispatch_id, handle));
            Ok(handle)
        }

        fn schedule_repeating(
            &mut self,
            mode: TimerMode,
            first_due: u64,
            interval: u64,
            dispatch_id: u32,
        ) -> Result<KernelHandle, KernelError> {
            assert_eq!(mode, TimerMode::TickFrame);
            let handle = self.alloc();
            self.repeating
                .push((first_due, interval, dispatch_id, handle));
            Ok(handle)
        }

        fn cancel(&mut self, handle: KernelHandle) -> Result<(), KernelError> {
            self.one_shots.retain(|row| row.2 != handle);
            self.repeating.retain(|row| row.3 != handle);
            Ok(())
        }

        fn pump_wall_clock(&mut self, now_ms: u64) -> Result<Vec<KernelFired>, KernelError> {
            if now_ms < self.committed_ms {
                return Err(KernelError {
                    status: 9,
                    detail: "invalid_due_tick".to_owned(),
                });
            }
            self.committed_ms = now_ms;
            let mut fired = Vec::new();
            self.one_shots.retain(|(due, dispatch, handle)| {
                if *due <= now_ms {
                    fired.push(KernelFired {
                        handle: *handle,
                        due: *due,
                        schedule_sequence: 1,
                        dispatch_id: *dispatch,
                    });
                    false
                } else {
                    true
                }
            });
            Ok(fired)
        }

        fn advance_tick_frame(&mut self, to_tick: u64) -> Result<Vec<KernelFired>, KernelError> {
            if to_tick < self.committed_tick {
                return Err(KernelError {
                    status: 9,
                    detail: "invalid_due_tick".to_owned(),
                });
            }
            let mut fired = Vec::new();
            for (next_due, interval, dispatch, handle) in &mut self.repeating {
                while *next_due <= to_tick {
                    fired.push(KernelFired {
                        handle: *handle,
                        due: *next_due,
                        schedule_sequence: 1,
                        dispatch_id: *dispatch,
                    });
                    *next_due = next_due.saturating_add(*interval);
                }
            }
            self.committed_tick = to_tick;
            Ok(fired)
        }
    }

    #[test]
    fn wall_clock_one_shot_fires_after_clock_advance() {
        let clock = SharedClock::test();
        let timer = HostTimer::new(clock.clone(), Box::new(ScriptedKernel::new()));
        timer.schedule_wall_one_shot(100, 7).expect("schedule");
        assert!(timer.pump_wall_clock().expect("pump").is_empty());
        clock.advance_ms(100);
        let fired = timer.pump_wall_clock().expect("pump due");
        assert_eq!(fired.len(), 1);
        assert_eq!(fired[0].dispatch_id, 7);
    }

    #[test]
    fn tick_frame_repeating_fires_on_advance() {
        let clock = SharedClock::test();
        let timer = HostTimer::new(clock, Box::new(ScriptedKernel::new()));
        timer.schedule_tick_repeating(1, 2).expect("schedule tick");
        let first = timer.advance_tick_frame(1).expect("tick 1");
        assert_eq!(first.len(), 1);
        assert_eq!(first[0].dispatch_id, 2);
        let second = timer.advance_tick_frame(2).expect("tick 2");
        assert_eq!(second.len(), 1);
    }

    #[test]
    fn cancelled_one_shot_does_not_fire() {
        let clock = SharedClock::test();
        let timer = HostTimer::new(clock.clone(), Box::new(ScriptedKernel::new()));
        let handle = timer.schedule_wall_one_shot(50, 1).expect("schedule");
        timer.cancel(handle).expect("cancel");
        clock.advance_ms(50);
        assert!(timer.pump_wall_clock().expect("pump").is_empty());
    }
}
