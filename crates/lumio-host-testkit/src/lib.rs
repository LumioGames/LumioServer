//! Development-only support for deterministic `LumioServer` host tests.
//!
//! Production crates must use this package only through development
//! dependencies or test targets. The utilities remain supplier-neutral and do
//! not replace generated contracts, codecs, authentication, permission checks,
//! production queues, or production clocks.
#![forbid(unsafe_code)]

mod assertions;
mod clock;
mod fault;
mod fixtures;
mod ports;

pub use assertions::{DeterministicSequence, SequenceError};
pub use clock::{ClockError, TestInstant, TestMonotonicClock};
pub use fault::{FaultPlan, FaultPlanError, FaultPoint};
pub use fixtures::{FixtureError, FixtureLoader, LoadedFixture};
pub use ports::{BoundedPortProbe, PortProbeSnapshot, ProbeReceiveError, ProbeSendError};
