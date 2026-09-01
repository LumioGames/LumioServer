//! Gameplay world port. Production uses CoreCLR ChatRoomWorld; tests may use LocalGameplay.

use std::collections::{HashMap, HashSet, VecDeque};

use super::INGRESS_QUEUE_PER_CONNECTION;

/// Outcome of admitting or applying chat.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatOperation {
    pub kind: ChatOpKind,
    pub error_code: Option<String>,
}

/// Chat operation kind matching ChatOperationKind.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChatOpKind {
    Admitted,
    Committed,
    Rejected,
    Fatal,
}

impl ChatOperation {
    #[must_use]
    pub fn admitted() -> Self {
        Self {
            kind: ChatOpKind::Admitted,
            error_code: None,
        }
    }

    #[must_use]
    pub fn committed() -> Self {
        Self {
            kind: ChatOpKind::Committed,
            error_code: None,
        }
    }

    #[must_use]
    pub fn rejected(code: &str) -> Self {
        Self {
            kind: ChatOpKind::Rejected,
            error_code: Some(code.to_owned()),
        }
    }
}

/// Live chat event from the committed tick.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatMessageEvent {
    pub message_id: u64,
    pub room_sequence: u64,
    pub sender_net_entity_id: u64,
    pub text: String,
    pub applied_tick: u64,
}

/// One tick of chat apply.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatTickResult {
    pub applied_tick: u64,
    pub events: Vec<ChatMessageEvent>,
}

/// Persist-only last-message row. History is always zero.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatPersistEntity {
    pub net_entity_id: u64,
    pub account_id: String,
    pub entity_type: super::BoundEntityKind,
    pub last_message_text: String,
    pub last_message_tick: u64,
    pub history_count: i32,
}

/// Persist snapshot for one room.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatPersistSnapshot {
    pub entities: Vec<ChatPersistEntity>,
}

/// Authoritative chat world. Implementations must run on the Simulation Owner Thread.
pub trait GameplayWorld: Send {
    fn create_room(&mut self, room_id: &str);
    fn create_entity(&mut self, room_id: &str, net_entity_id: u64) -> bool;
    fn destroy_entity(&mut self, room_id: &str, net_entity_id: u64) -> bool;
    fn admit_chat(&mut self, room_id: &str, sender: u64, text: &str) -> ChatOperation;
    fn run_tick(&mut self, room_id: &str) -> ChatTickResult;
    fn last_message(&mut self, room_id: &str, net_entity_id: u64) -> Option<(String, u64)>;
    fn capture_persist(&mut self, room_id: &str) -> Vec<(u64, String, u64)>;
    fn restore_last_message(
        &mut self,
        room_id: &str,
        net_entity_id: u64,
        text: &str,
        tick: u64,
    ) -> bool;
    fn current_tick(&mut self, room_id: &str) -> u64;
}

/// In-process double of ChatRoomWorld for host unit tests. The acceptance suite
/// does not use this type; it loads the C# ChatRoomWorld through CoreCLR.
pub struct LocalGameplay {
    rooms: HashMap<String, LocalRoom>,
}

struct LocalRoom {
    current_tick: u64,
    components: HashMap<u64, (String, u64)>,
    retired: HashSet<u64>,
    ingress: VecDeque<(u64, String)>,
    ingress_per_sender: HashMap<u64, usize>,
    next_message_id: u64,
}

impl LocalGameplay {
    #[must_use]
    pub fn new() -> Self {
        Self {
            rooms: HashMap::new(),
        }
    }

    fn room_mut(&mut self, room_id: &str) -> &mut LocalRoom {
        self.rooms
            .entry(room_id.to_owned())
            .or_insert_with(|| LocalRoom {
                current_tick: 0,
                components: HashMap::new(),
                retired: HashSet::new(),
                ingress: VecDeque::new(),
                ingress_per_sender: HashMap::new(),
                next_message_id: 1,
            })
    }
}

impl Default for LocalGameplay {
    fn default() -> Self {
        Self::new()
    }
}

impl GameplayWorld for LocalGameplay {
    fn create_room(&mut self, room_id: &str) {
        let _ = self.room_mut(room_id);
    }

    fn create_entity(&mut self, room_id: &str, net_entity_id: u64) -> bool {
        let room = self.room_mut(room_id);
        if net_entity_id == 0
            || room.retired.contains(&net_entity_id)
            || room.components.contains_key(&net_entity_id)
        {
            return false;
        }
        room.components.insert(net_entity_id, (String::new(), 0));
        true
    }

    fn destroy_entity(&mut self, room_id: &str, net_entity_id: u64) -> bool {
        let room = self.room_mut(room_id);
        if room.components.remove(&net_entity_id).is_none() {
            return false;
        }
        room.retired.insert(net_entity_id);
        true
    }

    fn admit_chat(&mut self, room_id: &str, sender: u64, text: &str) -> ChatOperation {
        if text.as_bytes().len() > 512 {
            return ChatOperation::rejected("chat_text_too_long");
        }
        let room = self.room_mut(room_id);
        let queued = room.ingress_per_sender.get(&sender).copied().unwrap_or(0);
        if queued >= INGRESS_QUEUE_PER_CONNECTION {
            return ChatOperation::rejected("queue_full");
        }
        room.ingress.push_back((sender, text.to_owned()));
        room.ingress_per_sender.insert(sender, queued + 1);
        ChatOperation::admitted()
    }

    fn run_tick(&mut self, room_id: &str) -> ChatTickResult {
        let room = self.room_mut(room_id);
        room.current_tick += 1;
        let pending: Vec<(u64, String)> = room.ingress.drain(..).collect();
        room.ingress_per_sender.clear();
        let mut events = Vec::new();
        let mut committed = HashSet::new();
        for (sender, text) in pending {
            if !room.components.contains_key(&sender) || committed.contains(&sender) {
                continue;
            }
            room.components
                .insert(sender, (text.clone(), room.current_tick));
            committed.insert(sender);
            events.push(ChatMessageEvent {
                message_id: room.next_message_id,
                room_sequence: room.next_message_id,
                sender_net_entity_id: sender,
                text,
                applied_tick: room.current_tick,
            });
            room.next_message_id += 1;
        }
        ChatTickResult {
            applied_tick: room.current_tick,
            events,
        }
    }

    fn last_message(&mut self, room_id: &str, net_entity_id: u64) -> Option<(String, u64)> {
        self.rooms
            .get(room_id)
            .and_then(|room| room.components.get(&net_entity_id).cloned())
    }

    fn capture_persist(&mut self, room_id: &str) -> Vec<(u64, String, u64)> {
        self.rooms
            .get(room_id)
            .map(|room| {
                room.components
                    .iter()
                    .map(|(id, (text, tick))| (*id, text.clone(), *tick))
                    .collect()
            })
            .unwrap_or_default()
    }

    fn restore_last_message(
        &mut self,
        room_id: &str,
        net_entity_id: u64,
        text: &str,
        tick: u64,
    ) -> bool {
        let room = self.room_mut(room_id);
        if !room.components.contains_key(&net_entity_id) {
            return false;
        }
        room.components
            .insert(net_entity_id, (text.to_owned(), tick));
        true
    }

    fn current_tick(&mut self, room_id: &str) -> u64 {
        self.rooms.get(room_id).map_or(0, |room| room.current_tick)
    }
}
