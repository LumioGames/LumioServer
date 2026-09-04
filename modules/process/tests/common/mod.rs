//! Test doubles of Runtime and `NativeCore` ABI. Not production kernels or binding tables.
#![allow(dead_code)]

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use lumio_host_runtime::{KernelError, KernelFired, KernelHandle, KernelTimer, TimerMode};
use lumio_server_process::entity_chat::{
    normalize_net_entity_id, AttributeQueryOutcome, AttributeQueryScope, BoundEntityKind,
    ChatOperation, PersistRecord, QueryResult, RebindMode, RuntimeAdmit, RuntimeBinding,
    RuntimeQuery, RuntimeSurface, RuntimeTick, MAX_CHAT_INPUTS_PER_TICK,
};

pub const DISPATCH_EXPIRE: u32 = 1;
pub const DISPATCH_TICK: u32 = 2;

pub struct TestKernel {
    one_shots: Vec<(u64, u32, KernelHandle)>,
    repeating: Vec<(u64, u64, u32, KernelHandle)>,
    next: u32,
    committed_ms: u64,
    committed_tick: u64,
}

impl TestKernel {
    #[must_use]
    pub fn new() -> Self {
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

impl Default for TestKernel {
    fn default() -> Self {
        Self::new()
    }
}

impl KernelTimer for TestKernel {
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

#[derive(Clone)]
struct Occupancy {
    binding: RuntimeBinding,
    live_connection: Option<String>,
}

pub struct ScriptedRuntime {
    next: u64,
    by_connection: HashMap<String, RuntimeBinding>,
    retained: HashMap<String, Occupancy>,
    entities: HashMap<String, Occupancy>,
    tombstoned: HashMap<String, String>,
    planted_query: HashMap<(String, String, String), QueryResult>,
    planted_snapshot: Option<Vec<u8>>,
    snapshot_failed: bool,
    planted_delta: Vec<Vec<u8>>,
    persist_bytes: Vec<u8>,
    tick: u64,
    revision: u64,
    expire_calls: Vec<String>,
    restore_calls: usize,
    pending_chats: Vec<(String, String)>,
    events_by_tick: HashMap<u64, Vec<(String, String)>>,
    run_tick_input_counts: Vec<usize>,
}

impl ScriptedRuntime {
    #[must_use]
    pub fn new() -> Self {
        Self {
            next: 1,
            by_connection: HashMap::new(),
            retained: HashMap::new(),
            entities: HashMap::new(),
            tombstoned: HashMap::new(),
            planted_query: HashMap::new(),
            planted_snapshot: None,
            snapshot_failed: false,
            planted_delta: Vec::new(),
            persist_bytes: b"persist".to_vec(),
            tick: 0,
            revision: 0,
            expire_calls: Vec::new(),
            restore_calls: 0,
            pending_chats: Vec::new(),
            events_by_tick: HashMap::new(),
            run_tick_input_counts: Vec::new(),
        }
    }

    #[must_use]
    pub fn run_tick_input_counts(&self) -> &[usize] {
        &self.run_tick_input_counts
    }

    pub fn plant_query(&mut self, room: &str, net: &str, attr: &str, result: QueryResult) {
        self.planted_query
            .insert((room.to_owned(), net.to_owned(), attr.to_owned()), result);
    }

    pub fn plant_snapshot(&mut self, json: &str) {
        self.snapshot_failed = false;
        self.planted_snapshot = Some(json.as_bytes().to_vec());
    }

    pub fn fail_snapshot(&mut self) {
        self.snapshot_failed = true;
        self.planted_snapshot = None;
    }

    pub fn plant_delta(&mut self, frames: Vec<String>) {
        self.planted_delta = frames.into_iter().map(String::into_bytes).collect();
    }

    #[must_use]
    pub fn expire_calls(&self) -> &[String] {
        &self.expire_calls
    }

    #[must_use]
    pub fn restore_calls(&self) -> usize {
        self.restore_calls
    }

    fn alloc(&mut self) -> String {
        let id = format!("{:032x}", self.next);
        self.next += 1;
        id
    }
}

impl Default for ScriptedRuntime {
    fn default() -> Self {
        Self::new()
    }
}

impl RuntimeSurface for ScriptedRuntime {
    fn admit(
        &mut self,
        connection: &str,
        account_id: &str,
        room_id: &str,
        entity_type: BoundEntityKind,
    ) -> RuntimeAdmit {
        if let Some(existing) = self
            .by_connection
            .values()
            .find(|row| row.account_id == account_id)
            .cloned()
        {
            return RuntimeAdmit::already_online(existing);
        }
        if let Some(live) = self.retained.get(account_id) {
            if live.live_connection.is_some() {
                return RuntimeAdmit::already_online(live.binding.clone());
            }
            return RuntimeAdmit::reject("binding_not_found");
        }
        let binding = RuntimeBinding {
            account_id: account_id.to_owned(),
            room_id: room_id.to_owned(),
            net_entity_id: self.alloc(),
            entity_type,
            connection_generation: 1,
        };
        self.by_connection
            .insert(connection.to_owned(), binding.clone());
        self.entities.insert(
            binding.net_entity_id.clone(),
            Occupancy {
                binding: binding.clone(),
                live_connection: Some(connection.to_owned()),
            },
        );
        RuntimeAdmit::ok(binding)
    }

    fn disconnect(&mut self, connection: &str) -> Result<RuntimeBinding, String> {
        let binding = self
            .by_connection
            .remove(connection)
            .ok_or_else(|| "binding_not_found".to_owned())?;
        if let Some(occupancy) = self.entities.get_mut(&binding.net_entity_id) {
            occupancy.live_connection = None;
            occupancy.binding = binding.clone();
        }
        self.retained.insert(
            binding.account_id.clone(),
            Occupancy {
                binding: binding.clone(),
                live_connection: None,
            },
        );
        Ok(binding)
    }

    fn rebind(
        &mut self,
        connection: &str,
        account_id: &str,
        room_id: &str,
        mode: RebindMode,
    ) -> RuntimeAdmit {
        if mode == RebindMode::Takeover {
            let Some(old_conn) = self
                .by_connection
                .iter()
                .find(|(_, row)| row.account_id == account_id)
                .map(|(id, _)| id.clone())
            else {
                return RuntimeAdmit::reject("binding_not_found");
            };
            let mut binding = self.by_connection.remove(&old_conn).expect("live");
            if binding.room_id != room_id {
                return RuntimeAdmit::reject("cross_room_reference");
            }
            binding.connection_generation += 1;
            self.by_connection
                .insert(connection.to_owned(), binding.clone());
            return RuntimeAdmit::ok(binding);
        }
        let Some(occupancy) = self.retained.remove(account_id) else {
            return RuntimeAdmit::reject("binding_not_found");
        };
        let mut binding = occupancy.binding;
        if binding.room_id != room_id {
            return RuntimeAdmit::reject("cross_room_reference");
        }
        binding.connection_generation += 1;
        self.by_connection
            .insert(connection.to_owned(), binding.clone());
        self.entities.insert(
            binding.net_entity_id.clone(),
            Occupancy {
                binding: binding.clone(),
                live_connection: Some(connection.to_owned()),
            },
        );
        RuntimeAdmit::ok(binding)
    }

    fn expire(&mut self, net_entity_id: &str) -> Result<(), String> {
        let net_entity_id = normalize_net_entity_id(net_entity_id);
        self.expire_calls.push(net_entity_id.clone());
        if let Some(occupancy) = self.entities.remove(&net_entity_id) {
            self.tombstoned
                .insert(net_entity_id.clone(), occupancy.binding.room_id);
            self.retained
                .retain(|_, row| row.binding.net_entity_id != net_entity_id);
        }
        Ok(())
    }

    fn self_lookup(&mut self, connection: &str) -> Option<RuntimeBinding> {
        self.by_connection.get(connection).cloned()
    }

    fn resolve_by_net_entity_id(
        &mut self,
        room_id: &str,
        net_entity_id: &str,
    ) -> Option<RuntimeBinding> {
        let net_entity_id = normalize_net_entity_id(net_entity_id);
        let occupancy = self.entities.get(&net_entity_id)?;
        if occupancy.binding.room_id != room_id {
            return None;
        }
        Some(occupancy.binding.clone())
    }

    fn query_attribute(&mut self, request: &RuntimeQuery) -> QueryResult {
        let net_entity_id = normalize_net_entity_id(&request.net_entity_id);
        if let Some(planted) = self.planted_query.get(&(
            request.room_id.clone(),
            net_entity_id.clone(),
            request.attribute_id.clone(),
        )) {
            return planted.clone();
        }
        if let Some(room) = self.tombstoned.get(&net_entity_id) {
            if room != &request.room_id {
                return QueryResult::request_error("cross_room_reference");
            }
            return QueryResult::fail(AttributeQueryOutcome::Tombstoned);
        }
        let Some(occupancy) = self.entities.get(&net_entity_id) else {
            return QueryResult::fail(AttributeQueryOutcome::NonExistent);
        };
        if occupancy.binding.room_id != request.room_id {
            return QueryResult::request_error("cross_room_reference");
        }
        if let Some(generation) = request.connection_generation {
            if generation < occupancy.binding.connection_generation {
                return QueryResult::fail(AttributeQueryOutcome::StaleGeneration);
            }
        }
        if request.caller_scope == AttributeQueryScope::ClientReplica
            && request.attribute_id == "EntityIdentity.claimedMark"
        {
            return QueryResult::fail(AttributeQueryOutcome::Unauthorized);
        }
        QueryResult::ok(occupancy.binding.entity_type.as_str().to_owned(), 0, 0)
    }

    fn list_bindings(&mut self, room_id: &str) -> Vec<RuntimeBinding> {
        self.by_connection
            .values()
            .filter(|row| row.room_id == room_id)
            .cloned()
            .collect()
    }

    fn attach_member(&mut self, _room_id: &str, _connection: &str) -> Result<(), String> {
        Ok(())
    }

    fn admit_input_command(
        &mut self,
        room_id: &str,
        connection: &str,
        _generation: u64,
        _envelope_json: &str,
    ) -> ChatOperation {
        if self.by_connection.contains_key(connection) {
            self.pending_chats
                .push((room_id.to_owned(), connection.to_owned()));
            ChatOperation::admitted()
        } else {
            ChatOperation::rejected("disconnected")
        }
    }

    fn run_tick(&mut self, _room_id: &str, _tick_id: u64) -> RuntimeTick {
        self.tick = self.tick.saturating_add(1);
        self.revision += 1;
        let pending = std::mem::take(&mut self.pending_chats);
        self.run_tick_input_counts.push(pending.len());
        if pending.len() > MAX_CHAT_INPUTS_PER_TICK {
            return RuntimeTick {
                applied_tick: 0,
                revision: self.revision,
                ok: false,
                event_count: 0,
                code: Some("runtime_failure".to_owned()),
            };
        }
        let event_count = pending.len() as u64;
        self.events_by_tick.insert(self.tick, pending);
        RuntimeTick::committed(self.tick, self.revision, event_count)
    }

    fn build_full_snapshot(&mut self, _room_id: &str, tick_id: u64, revision: u64) -> Vec<u8> {
        if self.snapshot_failed {
            return Vec::new();
        }
        if let Some(planted) = &self.planted_snapshot {
            return planted.clone();
        }
        format!(
            r#"{{"messageType":"FullSnapshot","tickId":{tick_id},"revision":{revision},"stateBlocks":[{{"mappingId":"entity.identity","payload":"00","payloadSha256":"00"}}]}}"#
        )
        .into_bytes()
    }

    fn build_delta(&mut self, room_id: &str, tick_id: u64, revision: u64) -> Vec<Vec<u8>> {
        if !self.planted_delta.is_empty() {
            return self.planted_delta.clone();
        }
        let mut frames = Vec::new();
        if let Some(committed) = self.events_by_tick.get(&tick_id) {
            for (room, _) in committed {
                if room == room_id {
                    frames.push(
                        format!(
                            r#"{{"messageType":"Delta","tickId":{tick_id},"revision":{revision},"changedBlocks":[{{"mappingId":"chat.event","payload":"{tick_id}","payloadSha256":"bb"}}]}}"#
                        )
                        .into_bytes(),
                    );
                }
            }
        }
        if frames.is_empty() {
            vec![format!(
                r#"{{"messageType":"Delta","tickId":{tick_id},"revision":{revision},"changedBlocks":[]}}"#
            )
            .into_bytes()]
        } else {
            frames
        }
    }

    fn persist(&mut self, _room_id: &str) -> PersistRecord {
        PersistRecord {
            bytes: self.persist_bytes.clone(),
        }
    }

    fn restore(&mut self, _room_id: &str, _bytes: &[u8]) -> Result<(), String> {
        self.restore_calls += 1;
        Ok(())
    }
}

#[derive(Clone)]
pub struct SharedRuntime(pub Arc<Mutex<ScriptedRuntime>>);

impl SharedRuntime {
    #[must_use]
    pub fn new() -> Self {
        Self(Arc::new(Mutex::new(ScriptedRuntime::new())))
    }

    pub fn lock(&self) -> std::sync::MutexGuard<'_, ScriptedRuntime> {
        self.0.lock().expect("scripted runtime")
    }
}

impl RuntimeSurface for SharedRuntime {
    fn admit(
        &mut self,
        connection: &str,
        account_id: &str,
        room_id: &str,
        entity_type: BoundEntityKind,
    ) -> RuntimeAdmit {
        self.lock()
            .admit(connection, account_id, room_id, entity_type)
    }

    fn disconnect(&mut self, connection: &str) -> Result<RuntimeBinding, String> {
        self.lock().disconnect(connection)
    }

    fn rebind(
        &mut self,
        connection: &str,
        account_id: &str,
        room_id: &str,
        mode: RebindMode,
    ) -> RuntimeAdmit {
        self.lock().rebind(connection, account_id, room_id, mode)
    }

    fn expire(&mut self, net_entity_id: &str) -> Result<(), String> {
        self.lock().expire(net_entity_id)
    }

    fn self_lookup(&mut self, connection: &str) -> Option<RuntimeBinding> {
        self.lock().self_lookup(connection)
    }

    fn resolve_by_net_entity_id(
        &mut self,
        room_id: &str,
        net_entity_id: &str,
    ) -> Option<RuntimeBinding> {
        self.lock().resolve_by_net_entity_id(room_id, net_entity_id)
    }

    fn query_attribute(&mut self, request: &RuntimeQuery) -> QueryResult {
        self.lock().query_attribute(request)
    }

    fn list_bindings(&mut self, room_id: &str) -> Vec<RuntimeBinding> {
        self.lock().list_bindings(room_id)
    }

    fn attach_member(&mut self, room_id: &str, connection: &str) -> Result<(), String> {
        self.lock().attach_member(room_id, connection)
    }

    fn admit_input_command(
        &mut self,
        room_id: &str,
        connection: &str,
        generation: u64,
        envelope_json: &str,
    ) -> ChatOperation {
        self.lock()
            .admit_input_command(room_id, connection, generation, envelope_json)
    }

    fn run_tick(&mut self, room_id: &str, tick_id: u64) -> RuntimeTick {
        self.lock().run_tick(room_id, tick_id)
    }

    fn build_full_snapshot(&mut self, room_id: &str, tick_id: u64, revision: u64) -> Vec<u8> {
        self.lock().build_full_snapshot(room_id, tick_id, revision)
    }

    fn build_delta(&mut self, room_id: &str, tick_id: u64, revision: u64) -> Vec<Vec<u8>> {
        self.lock().build_delta(room_id, tick_id, revision)
    }

    fn persist(&mut self, room_id: &str) -> PersistRecord {
        self.lock().persist(room_id)
    }

    fn restore(&mut self, room_id: &str, bytes: &[u8]) -> Result<(), String> {
        self.lock().restore(room_id, bytes)
    }
}

pub fn snapshot_with_state_blocks() -> String {
    r#"{"messageType":"FullSnapshot","tickId":1,"revision":1,"stateBlocks":[{"mappingId":"entity.identity","payload":"01000000010000000000000006000000706c6179657200000000","payloadSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}"#
        .to_owned()
}

pub fn delta_frame(seq: u64) -> String {
    format!(
        r#"{{"messageType":"Delta","tickId":{seq},"revision":{seq},"changedBlocks":[{{"mappingId":"chat.event","payload":"{seq}","payloadSha256":"bb"}}]}}"#
    )
}
