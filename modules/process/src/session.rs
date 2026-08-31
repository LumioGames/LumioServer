//! Session admission state machine.
//!
//! Pure state (no IO): `AwaitHandshake -> Baselined -> Active -> Closed`
//! transitions, admission rules (`maxSessions`, role uniqueness) and the
//! per-sender committed-sequence watermark. The world loop owns one table and
//! is its only writer, mirroring the single-writer registry rule the module
//! map gives `session` in the full architecture.

use std::collections::HashMap;

use crate::wire::Role;

/// Lifecycle phase of one server-side session.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionPhase {
    /// Connected, waiting for a Handshake envelope.
    AwaitHandshake,
    /// Admitted, FullSnapshot sent, waiting for BaselineAck.
    Baselined,
    /// Baseline acknowledged; may send commands and receive deltas.
    Active,
    /// Terminal.
    Closed,
}

/// Why an admission attempt was refused.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AdmissionError {
    /// `session_limit`: more sessions than the contract allows.
    SessionLimit,
    /// `role_taken`: the requested role is already held.
    RoleTaken(Role),
}

/// State of one session slot.
#[derive(Debug, Clone)]
struct SessionState {
    phase: SessionPhase,
    role: Option<Role>,
    client_name: String,
    baselined_revision: Option<u64>,
}

/// Session registry: admission, phases and per-role committed sequence.
#[derive(Debug)]
pub struct SessionTable {
    max_sessions: usize,
    sessions: HashMap<String, SessionState>,
    committed_by_role: HashMap<Role, u64>,
}

impl SessionTable {
    /// New table honouring the contract's `maxSessions`.
    #[must_use]
    pub fn new(max_sessions: usize) -> Self {
        Self {
            max_sessions,
            sessions: HashMap::new(),
            committed_by_role: HashMap::new(),
        }
    }

    /// Register a fresh connection in [`SessionPhase::AwaitHandshake`].
    pub fn open(&mut self, session_id: &str) {
        self.sessions.insert(
            session_id.to_owned(),
            SessionState {
                phase: SessionPhase::AwaitHandshake,
                role: None,
                client_name: String::new(),
                baselined_revision: None,
            },
        );
    }

    /// Admit a session to a role.
    ///
    /// Capacity is checked before role uniqueness so the third connection
    /// reports `session_limit` even when its role is also taken, while a
    /// duplicate role on a free slot reports `role_taken`.
    ///
    /// # Errors
    ///
    /// Returns [`AdmissionError::SessionLimit`] or [`AdmissionError::RoleTaken`].
    pub fn admit(
        &mut self,
        session_id: &str,
        role: Role,
        client_name: &str,
    ) -> Result<(), AdmissionError> {
        let admitted = self
            .sessions
            .values()
            .filter(|s| s.phase != SessionPhase::Closed)
            .filter(|s| s.role.is_some())
            .count();
        if admitted >= self.max_sessions {
            return Err(AdmissionError::SessionLimit);
        }
        if self
            .sessions
            .values()
            .any(|s| s.phase != SessionPhase::Closed && s.role == Some(role))
        {
            return Err(AdmissionError::RoleTaken(role));
        }
        let Some(state) = self.sessions.get_mut(session_id) else {
            return Err(AdmissionError::RoleTaken(role));
        };
        state.phase = SessionPhase::Baselined;
        state.role = Some(role);
        state.client_name = client_name.to_owned();
        Ok(())
    }

    /// Record the revision the FullSnapshot carried (enter [`SessionPhase::Baselined`]).
    pub fn mark_baselined(&mut self, session_id: &str, revision: u64) {
        if let Some(state) = self.sessions.get_mut(session_id) {
            state.phase = SessionPhase::Baselined;
            state.baselined_revision = Some(revision);
        }
    }

    /// BaselineAck accepted (enter [`SessionPhase::Active`]).
    pub fn mark_active(&mut self, session_id: &str) {
        if let Some(state) = self.sessions.get_mut(session_id) {
            state.phase = SessionPhase::Active;
        }
    }

    /// Terminal transition; frees the role slot.
    pub fn close(&mut self, session_id: &str) {
        if let Some(state) = self.sessions.get_mut(session_id) {
            state.phase = SessionPhase::Closed;
            state.role = None;
        }
    }

    /// Current phase, if the session is registered.
    #[must_use]
    pub fn phase(&self, session_id: &str) -> Option<SessionPhase> {
        self.sessions.get(session_id).map(|s| s.phase)
    }

    /// Admitted role of the session.
    #[must_use]
    pub fn role(&self, session_id: &str) -> Option<Role> {
        self.sessions.get(session_id).and_then(|s| s.role)
    }

    /// Revision carried by the FullSnapshot sent to this session.
    #[must_use]
    pub fn baselined_revision(&self, session_id: &str) -> Option<u64> {
        self.sessions.get(session_id).and_then(|s| s.baselined_revision)
    }

    /// True when `sequence` is not strictly above the role's committed max.
    ///
    /// The watermark survives session close: sequence monotonicity belongs to
    /// the sender (role), not to one connection.
    #[must_use]
    pub fn is_duplicate_sequence(&self, role: Role, sequence: u64) -> bool {
        sequence <= self.committed_by_role.get(&role).copied().unwrap_or(0)
    }

    /// Advance the role's committed watermark after a delta commit.
    pub fn record_commit(&mut self, role: Role, sequence: u64) {
        let entry = self.committed_by_role.entry(role).or_insert(0);
        if sequence > *entry {
            *entry = sequence;
        }
    }

    /// Session ids currently in [`SessionPhase::Active`].
    #[must_use]
    pub fn active_ids(&self) -> Vec<String> {
        self.sessions
            .iter()
            .filter(|(_, s)| s.phase == SessionPhase::Active)
            .map(|(id, _)| id.clone())
            .collect()
    }

    /// Session id holding `role` in a non-closed phase, if any.
    #[must_use]
    pub fn session_with_role(&self, role: Role) -> Option<String> {
        self.sessions
            .iter()
            .find(|(_, s)| s.phase != SessionPhase::Closed && s.role == Some(role))
            .map(|(id, _)| id.clone())
    }

    /// Number of live (non-closed) entries.
    #[must_use]
    pub fn live_count(&self) -> usize {
        self.sessions
            .values()
            .filter(|s| s.phase != SessionPhase::Closed)
            .count()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn admits_browser_and_bot_then_rejects_third_by_limit() {
        let mut table = SessionTable::new(2);
        for id in ["s-1", "s-2", "s-3"] {
            table.open(id);
        }
        table.admit("s-1", Role::Browser, "b").expect("browser admitted");
        table.admit("s-2", Role::Bot, "c").expect("bot admitted");
        // Third session: capacity fires before role uniqueness.
        assert_eq!(table.admit("s-3", Role::Browser, "d"), Err(AdmissionError::SessionLimit));
        assert_eq!(table.live_count(), 3);
    }

    #[test]
    fn duplicate_role_is_taken() {
        let mut table = SessionTable::new(2);
        table.open("s-1");
        table.open("s-2");
        table.admit("s-1", Role::Browser, "b").expect("first browser admitted");
        assert_eq!(
            table.admit("s-2", Role::Browser, "c"),
            Err(AdmissionError::RoleTaken(Role::Browser))
        );
        // The failed session stays unadmitted.
        assert_eq!(table.phase("s-2"), Some(SessionPhase::AwaitHandshake));
    }

    #[test]
    fn closing_frees_the_role_slot() {
        let mut table = SessionTable::new(2);
        table.open("s-1");
        table.admit("s-1", Role::Browser, "b").expect("admitted");
        table.close("s-1");
        assert_eq!(table.phase("s-1"), Some(SessionPhase::Closed));
        assert_eq!(table.session_with_role(Role::Browser), None);
        table.open("s-2");
        table.admit("s-2", Role::Browser, "b2").expect("role freed after close");
    }

    #[test]
    fn sequence_watermark_is_per_role_and_survives_close() {
        let mut table = SessionTable::new(2);
        assert!(!table.is_duplicate_sequence(Role::Browser, 1));
        table.record_commit(Role::Browser, 3);
        assert!(table.is_duplicate_sequence(Role::Browser, 3));
        assert!(table.is_duplicate_sequence(Role::Browser, 2));
        assert!(!table.is_duplicate_sequence(Role::Browser, 4));
        assert!(!table.is_duplicate_sequence(Role::Bot, 3));
        table.record_commit(Role::Browser, 2); // regressions never lower the watermark
        assert!(table.is_duplicate_sequence(Role::Browser, 3));
        table.open("s-1");
        table.admit("s-1", Role::Browser, "b").expect("admitted");
        table.close("s-1");
        assert!(table.is_duplicate_sequence(Role::Browser, 1));
    }

    #[test]
    fn baseline_and_active_transitions() {
        let mut table = SessionTable::new(2);
        table.open("s-1");
        table.admit("s-1", Role::Browser, "b").expect("admitted");
        assert_eq!(table.phase("s-1"), Some(SessionPhase::Baselined));
        table.mark_baselined("s-1", 7);
        assert_eq!(table.baselined_revision("s-1"), Some(7));
        table.mark_active("s-1");
        assert_eq!(table.phase("s-1"), Some(SessionPhase::Active));
        assert_eq!(table.active_ids(), vec!["s-1".to_owned()]);
    }
}
