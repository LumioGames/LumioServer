use std::collections::{BTreeMap, BTreeSet};
use std::fmt;

/// One deterministic injection point and the occurrence on which it fires.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FaultPoint<P> {
    point: P,
    occurrence: u64,
}

impl<P> FaultPoint<P> {
    /// Creates an injection point that fires on a one-based occurrence.
    ///
    /// # Errors
    ///
    /// Returns [`FaultPlanError::ZeroOccurrence`] for occurrence zero.
    pub fn at(point: P, occurrence: u64) -> Result<Self, FaultPlanError<P>> {
        if occurrence == 0 {
            return Err(FaultPlanError::ZeroOccurrence { point });
        }
        Ok(Self { point, occurrence })
    }

    /// Returns the caller-defined point identifier.
    #[must_use]
    pub const fn point(&self) -> &P {
        &self.point
    }

    /// Returns the one-based hit number that triggers injection.
    #[must_use]
    pub const fn occurrence(&self) -> u64 {
        self.occurrence
    }
}

/// A deterministic, typed fault-injection schedule.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FaultPlan<P> {
    scheduled: BTreeMap<P, BTreeSet<u64>>,
    observed: BTreeMap<P, u64>,
    remaining: usize,
}

impl<P> FaultPlan<P>
where
    P: Clone + Ord,
{
    /// Builds a plan without assigning meaning to the caller's point type.
    ///
    /// # Errors
    ///
    /// Returns [`FaultPlanError::Duplicate`] when a point occurrence is listed
    /// more than once.
    pub fn new(points: impl IntoIterator<Item = FaultPoint<P>>) -> Result<Self, FaultPlanError<P>> {
        let mut scheduled = BTreeMap::<P, BTreeSet<u64>>::new();
        let mut remaining = 0;

        for FaultPoint { point, occurrence } in points {
            if scheduled
                .get(&point)
                .is_some_and(|occurrences| occurrences.contains(&occurrence))
            {
                return Err(FaultPlanError::Duplicate { point, occurrence });
            }
            scheduled.entry(point).or_default().insert(occurrence);
            remaining += 1;
        }

        Ok(Self {
            scheduled,
            observed: BTreeMap::new(),
            remaining,
        })
    }

    /// Records one hit and reports whether this occurrence must inject a fault.
    pub fn hit(&mut self, point: &P) -> bool {
        let Some(scheduled) = self.scheduled.get_mut(point) else {
            return false;
        };
        let occurrence = self.observed.entry(point.clone()).or_default();
        *occurrence = occurrence.saturating_add(1);

        let inject = scheduled.remove(occurrence);
        if inject {
            self.remaining -= 1;
        }
        inject
    }

    /// Returns the number of configured injections that have not fired.
    #[must_use]
    pub const fn remaining(&self) -> usize {
        self.remaining
    }
}

/// A configuration error in a deterministic fault plan.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum FaultPlanError<P> {
    /// Occurrences are one-based, so zero cannot identify a hit.
    ZeroOccurrence { point: P },
    /// The same point and occurrence were configured more than once.
    Duplicate { point: P, occurrence: u64 },
}

impl<P> fmt::Display for FaultPlanError<P>
where
    P: fmt::Debug,
{
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::ZeroOccurrence { point } => {
                write!(formatter, "fault point {point:?} uses occurrence zero")
            }
            Self::Duplicate { point, occurrence } => write!(
                formatter,
                "fault point {point:?} repeats occurrence {occurrence}"
            ),
        }
    }
}

impl<P> std::error::Error for FaultPlanError<P> where P: fmt::Debug {}
