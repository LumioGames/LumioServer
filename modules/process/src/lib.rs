//! Minimal process entry surface.
//!
//! Lifecycle assembly is intentionally deferred to the process implementation
//! tasks. This foundation crate exposes only a deterministic, side-effect-free
//! exit result so the binary can be compiled and exercised immediately.

/// Process exit categories reserved for the future lifecycle runner.
#[derive(Debug, Clone, Copy, Eq, PartialEq)]
#[repr(i32)]
pub enum ProcessExitCode {
    /// The foundation binary has no work to perform.
    Success = 0,
}

impl ProcessExitCode {
    /// Return the operating-system representation of this exit category.
    #[must_use]
    pub const fn as_i32(self) -> i32 {
        self as i32
    }
}

/// Run the process using operating-system inputs.
///
/// The actual configuration, signal and component wiring is implemented by
/// later process tasks; the foundation deliberately performs no production IO.
#[must_use]
pub const fn run_from_os() -> ProcessExitCode {
    ProcessExitCode::Success
}

#[cfg(test)]
mod tests {
    use super::{run_from_os, ProcessExitCode};

    #[test]
    fn foundation_entry_is_deterministic() {
        assert_eq!(run_from_os(), ProcessExitCode::Success);
        assert_eq!(run_from_os().as_i32(), 0);
    }
}
