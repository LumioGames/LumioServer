use std::fs;
use std::num::NonZeroUsize;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use lumio_host_testkit::{
    BoundedPortProbe, ClockError, DeterministicSequence, FaultPlan, FaultPlanError, FaultPoint,
    FixtureError, FixtureLoader, ProbeReceiveError, ProbeSendError, SequenceError,
    TestMonotonicClock,
};

static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

struct TestDirectory(PathBuf);

impl TestDirectory {
    fn new() -> Self {
        let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "lumio-host-testkit-{}-{sequence}",
            std::process::id()
        ));
        fs::create_dir_all(&path).unwrap();
        Self(path)
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TestDirectory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

#[test]
fn controlled_clock_advances_without_wall_clock_waiting() {
    let clock = TestMonotonicClock::from_nanos(40);

    assert_eq!(clock.now().as_nanos(), 40);
    assert_eq!(
        clock.advance(Duration::from_nanos(2)).unwrap().as_nanos(),
        42
    );
    assert_eq!(clock.now().as_nanos(), 42);
}

#[test]
fn controlled_clock_rejects_backwards_and_overflowing_changes() {
    let clock = TestMonotonicClock::from_nanos(9);

    assert_eq!(clock.set_nanos(8), Err(ClockError::WouldMoveBackwards));

    let clock = TestMonotonicClock::from_nanos(u64::MAX);
    assert_eq!(
        clock.advance(Duration::from_nanos(1)),
        Err(ClockError::Overflow)
    );
}

#[test]
fn fault_points_fire_on_configured_occurrences_in_repeatable_order() {
    let mut plan = FaultPlan::new([
        FaultPoint::at("connect", 2).unwrap(),
        FaultPoint::at("connect", 4).unwrap(),
        FaultPoint::at("flush", 1).unwrap(),
    ])
    .unwrap();

    assert!(!plan.hit(&"connect"));
    assert!(plan.hit(&"connect"));
    assert!(plan.hit(&"flush"));
    assert!(!plan.hit(&"connect"));
    assert!(plan.hit(&"connect"));
    assert_eq!(plan.remaining(), 0);
}

#[test]
fn fault_plan_rejects_ambiguous_occurrences() {
    assert_eq!(
        FaultPoint::at("connect", 0),
        Err(FaultPlanError::ZeroOccurrence { point: "connect" })
    );

    let point = FaultPoint::at("connect", 1).unwrap();
    assert_eq!(
        FaultPlan::new([point.clone(), point]),
        Err(FaultPlanError::Duplicate {
            point: "connect",
            occurrence: 1,
        })
    );
}

#[test]
fn bounded_port_probe_rejects_full_without_losing_the_message() {
    let mut probe = BoundedPortProbe::new(NonZeroUsize::new(2).unwrap());

    assert_eq!(probe.try_send(10), Ok(()));
    assert_eq!(probe.try_send(20), Ok(()));
    assert_eq!(probe.try_send(30), Err(ProbeSendError::Full(30)));

    let snapshot = probe.snapshot();
    assert_eq!(snapshot.capacity(), 2);
    assert_eq!(snapshot.depth(), 2);
    assert_eq!(snapshot.accepted(), 2);
    assert_eq!(snapshot.full_rejections(), 1);
    assert_eq!(probe.try_receive(), Ok(10));
}

#[test]
fn bounded_port_probe_drains_before_reporting_closed() {
    let mut probe = BoundedPortProbe::new(NonZeroUsize::new(1).unwrap());

    assert_eq!(probe.try_receive(), Err(ProbeReceiveError::Empty));
    probe.try_send("queued").unwrap();
    probe.close();

    assert_eq!(probe.try_send("late"), Err(ProbeSendError::Closed("late")));
    assert_eq!(probe.try_receive(), Ok("queued"));
    assert_eq!(probe.try_receive(), Err(ProbeReceiveError::Closed));

    let snapshot = probe.snapshot();
    assert!(snapshot.is_closed());
    assert_eq!(snapshot.received(), 1);
    assert_eq!(snapshot.closed_rejections(), 1);
}

#[test]
fn fixture_loader_returns_upstream_bytes_without_interpreting_schema() {
    let root = TestDirectory::new();
    fs::create_dir_all(root.path().join("valid")).unwrap();
    fs::write(
        root.path().join("valid/reference.json"),
        br#"{"ownedBy":"upstream"}"#,
    )
    .unwrap();
    let loader = FixtureLoader::new(root.path()).unwrap();

    let fixture = loader.load("valid/reference.json").unwrap();

    assert_eq!(fixture.relative_path(), Path::new("valid/reference.json"));
    assert_eq!(fixture.bytes(), br#"{"ownedBy":"upstream"}"#);
    assert!(fixture.source_path().is_absolute());
}

#[test]
fn fixture_loader_rejects_paths_outside_the_declared_root() {
    let root = TestDirectory::new();
    let loader = FixtureLoader::new(root.path()).unwrap();

    assert!(matches!(
        loader.load("../outside.json"),
        Err(FixtureError::InvalidRelativePath { .. })
    ));
    assert!(matches!(
        loader.load(root.path().join("absolute.json")),
        Err(FixtureError::InvalidRelativePath { .. })
    ));
}

#[test]
fn deterministic_sequence_accepts_each_expected_typed_step_once() {
    let mut sequence = DeterministicSequence::new(["start", "ready", "stop"]);

    assert_eq!(sequence.observe("start"), Ok(()));
    assert_eq!(sequence.observe("ready"), Ok(()));
    assert_eq!(sequence.remaining(), 1);
    assert_eq!(sequence.observe("stop"), Ok(()));
    assert_eq!(sequence.finish(), Ok(()));
}

#[test]
fn deterministic_sequence_reports_first_ordering_difference() {
    let mut sequence = DeterministicSequence::new(["start", "ready"]);

    sequence.observe("start").unwrap();
    assert_eq!(
        sequence.observe("stop"),
        Err(SequenceError::Mismatch {
            index: 1,
            expected: "ready",
            actual: "stop",
        })
    );

    let sequence = DeterministicSequence::new(["start"]);
    assert_eq!(
        sequence.finish(),
        Err(SequenceError::Incomplete {
            index: 0,
            next_expected: "start",
            remaining: 1,
        })
    );

    let mut sequence = DeterministicSequence::<&str>::new([]);
    assert_eq!(
        sequence.observe("extra"),
        Err(SequenceError::Unexpected {
            index: 0,
            actual: "extra",
        })
    );
}
