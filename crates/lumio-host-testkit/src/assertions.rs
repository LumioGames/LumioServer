use std::collections::VecDeque;
use std::fmt;

/// Incrementally checks a caller-defined sequence without domain assumptions.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DeterministicSequence<T> {
    expected: VecDeque<T>,
    matched: usize,
}

impl<T> DeterministicSequence<T> {
    /// Creates an exact sequence assertion from typed expected steps.
    #[must_use]
    pub fn new(expected: impl IntoIterator<Item = T>) -> Self {
        Self {
            expected: expected.into_iter().collect(),
            matched: 0,
        }
    }

    /// Returns how many expected steps have not yet been matched.
    #[must_use]
    pub fn remaining(&self) -> usize {
        self.expected.len()
    }

    /// Completes the assertion after the producer has emitted all steps.
    ///
    /// # Errors
    ///
    /// Returns [`SequenceError::Incomplete`] when expected steps remain.
    pub fn finish(mut self) -> Result<(), SequenceError<T>> {
        let Some(next_expected) = self.expected.pop_front() else {
            return Ok(());
        };
        Err(SequenceError::Incomplete {
            index: self.matched,
            next_expected,
            remaining: self.expected.len() + 1,
        })
    }
}

impl<T> DeterministicSequence<T>
where
    T: Clone + PartialEq,
{
    /// Compares one observed step with the next expected step.
    ///
    /// # Errors
    ///
    /// Returns the first mismatch or unexpected extra step. A mismatch leaves
    /// the expected step in place so the assertion remains inspectable.
    pub fn observe(&mut self, actual: T) -> Result<(), SequenceError<T>> {
        let Some(expected) = self.expected.front() else {
            return Err(SequenceError::Unexpected {
                index: self.matched,
                actual,
            });
        };
        if expected != &actual {
            return Err(SequenceError::Mismatch {
                index: self.matched,
                expected: expected.clone(),
                actual,
            });
        }

        self.expected.pop_front();
        self.matched += 1;
        Ok(())
    }
}

/// The first difference found by an exact deterministic sequence assertion.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum SequenceError<T> {
    /// The observed step differed from the expected step at this zero-based index.
    Mismatch {
        index: usize,
        expected: T,
        actual: T,
    },
    /// A step was observed after all expected steps were consumed.
    Unexpected { index: usize, actual: T },
    /// Observation ended before all expected steps were consumed.
    Incomplete {
        index: usize,
        next_expected: T,
        remaining: usize,
    },
}

impl<T> fmt::Display for SequenceError<T>
where
    T: fmt::Debug,
{
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Mismatch {
                index,
                expected,
                actual,
            } => write!(
                formatter,
                "sequence differs at index {index}: expected {expected:?}, observed {actual:?}"
            ),
            Self::Unexpected { index, actual } => {
                write!(formatter, "unexpected step at index {index}: {actual:?}")
            }
            Self::Incomplete {
                index,
                next_expected,
                remaining,
            } => write!(
                formatter,
                "sequence ended at index {index}; next expected {next_expected:?}, {remaining} remaining"
            ),
        }
    }
}

impl<T> std::error::Error for SequenceError<T> where T: fmt::Debug {}
