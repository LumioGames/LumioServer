#!/usr/bin/env node
/**
 * Rust-host SUCCESS oracle for the R-00354 101-entity suite.
 *
 * Game `verify-evidence.mjs` (2260c85) hard-requires `lumio-mvp-host`.
 * This file is the slice-host predicate: honest rust process name, per-entity
 * `nent_*` census, real S5 traces, S8 host NetEntityId rebind, InputCommand
 * on admitted chat, two-round compare. It never treats `lumio-mvp-host` as
 * success.
 */
import { existsSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { test as nodeTest } from 'node:test'
import assert from 'node:assert/strict'

const isObject = (v) => typeof v === 'object' && v !== null && !Array.isArray(v)

function loadJson(path) {
  return JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, ''))
}

function safeReadText(p) {
  try { return readFileSync(p, 'utf8') } catch { return '' }
}

function safeReadJson(p) {
  try { return loadJson(p) } catch { return null }
}

export function eventKind(ev) {
  return ev?.kind ?? ev?.event ?? null
}

export function parseNdjson(text) {
  const events = []
  const lines = String(text ?? '').split(/\r?\n/)
  for (let i = 0; i < lines.length; i++) {
    const trimmed = lines[i].trim()
    if (!trimmed) continue
    try {
      const ev = JSON.parse(trimmed)
      if (isObject(ev)) events.push({ line: i + 1, ev })
    } catch { /* skip */ }
  }
  return events
}

export function isLauncherLoopIndex(id) {
  const n = Number(id)
  return Number.isInteger(n) && n >= 1 && n <= 101 && String(n) === String(id)
}

export function isHostNetEntityId(id) {
  if (id == null) return false
  const s = String(id)
  if (s.length === 0 || s === '0' || isLauncherLoopIndex(s)) return false
  if (/^sess[-_]/i.test(s)) return false
  return /^nent[-_]/i.test(s)
}

function isMvpHostName(name) {
  return String(name ?? '') === 'lumio-mvp-host'
}

export function isRustReplayProcessName(name) {
  const s = String(name ?? '')
  if (isMvpHostName(s)) return false
  return /lumio-entity-chat-replay/i.test(s)
}

function isAdmitKind(kind) {
  return kind === 'entity_admitted' || kind === 'entity_created' || kind === 'binding_committed'
}

function entityTypeOf(ev) {
  const t = ev.entityType ?? ev.entityKind
  if (t === 'bot' || t === 'Bot' || t === 'BotEntity') return 'bot'
  if (t === 'player' || t === 'Player' || t === 'PlayerEntity') return 'player'
  return null
}

function rustProcessOf(ev) {
  return ev?.process ?? ev?.host ?? ev?.source ?? null
}

export function hasRustHostProcessAudit(evidence, auditText = '') {
  const proc = evidence?.hostProcess
  const named = isRustReplayProcessName(proc?.process)
    && Number.isInteger(Number(proc.pid))
    && Number(proc.pid) > 0
  if (!named) return false
  return parseNdjson(auditText).some(({ ev }) => {
    const process = rustProcessOf(ev)
    if (process && isMvpHostName(process)) return false
    if (process && !isRustReplayProcessName(process) && !isAdmitKind(eventKind(ev))) return false
    return isAdmitKind(eventKind(ev)) && isHostNetEntityId(ev.netEntityId)
  })
}

function censusFromIdMap(byId) {
  let botCount = 0
  let playerCount = 0
  for (const t of byId.values()) {
    if (t === 'bot') botCount++
    else playerCount++
  }
  return { botCount, playerCount, total: byId.size, netEntityIds: [...byId.keys()] }
}

function emptyCensus() {
  return { botCount: 0, playerCount: 0, total: 0, netEntityIds: [] }
}

export function censusFromHostAudit(auditText) {
  const byId = new Map()
  for (const { ev } of parseNdjson(auditText)) {
    if (!isAdmitKind(eventKind(ev))) continue
    const process = rustProcessOf(ev)
    if (process && isMvpHostName(process)) continue
    if (process && !isRustReplayProcessName(process)) continue
    const id = ev.netEntityId
    const type = entityTypeOf(ev)
    if (!isHostNetEntityId(id) || type == null) continue
    byId.set(String(id), type)
  }
  return censusFromIdMap(byId)
}

export function censusFromEvidence(evidence, auditText = '') {
  if (!hasRustHostProcessAudit(evidence, auditText)) {
    return emptyCensus()
  }
  return censusFromHostAudit(auditText)
}

function scenario(evidence, n) {
  return evidence?.scenarios?.[String(n)] ?? evidence?.scenarios?.[n] ?? {}
}

export function isEntityRebound(disconnected, admitted) {
  const left = disconnected?.netEntityId
  const right = admitted?.netEntityId
  return isHostNetEntityId(left) && isHostNetEntityId(right) && String(left) === String(right)
}

function reconnectBindingPair(evidence) {
  const s8 = scenario(evidence, 8)
  const t = evidence?.traces?.reconnect
  const disconnected = {
    netEntityId: t?.previousNetEntityId ?? s8?.previousNetEntityId,
    accountId: t?.previousAccountId ?? s8?.previousAccountId,
    sessionId: t?.previousSessionId ?? s8?.previousSessionId,
  }
  const admitted = {
    netEntityId: t?.netEntityId ?? s8?.netEntityId,
    accountId: t?.accountId ?? s8?.accountId,
    sessionId: t?.sessionId ?? s8?.sessionId,
  }
  return { disconnected, admitted, entityA: t?.entityA ?? s8?.entityA }
}

export function isReconnectEntityRebound(evidence) {
  const { disconnected, admitted, entityA } = reconnectBindingPair(evidence)
  if (typeof entityA === 'string' && /^sess[-_]/i.test(entityA)) return false
  if (!isEntityRebound(disconnected, admitted)) return false
  if (admitted.sessionId != null && String(admitted.netEntityId) === String(admitted.sessionId)) {
    return false
  }
  if (
    disconnected.sessionId != null
    && String(disconnected.netEntityId) === String(disconnected.sessionId)
  ) {
    return false
  }
  if (
    disconnected.accountId != null
    && admitted.accountId != null
    && String(disconnected.netEntityId) === String(disconnected.accountId)
  ) {
    return false
  }
  return true
}

function sha256Hex(value) {
  return /^[0-9a-f]{64}$/.test(String(value ?? ''))
}

function lumioBinHex(value) {
  return /^[0-9a-f]+$/.test(String(value ?? '')) && String(value ?? '').length >= 8
}

function eventOrderKey(entry) {
  const parts = String(entry).split(':')
  if (parts.length >= 3) return parts.slice(1).join(':')
  return String(entry)
}

function hasRealQueryTraces(evidence) {
  const traces = evidence?.traces?.queries
  const s5 = scenario(evidence, 5)
  const blob = `${JSON.stringify(traces ?? {})}\n${JSON.stringify(s5)}`.toLowerCase()
  if (!isObject(traces) || traces.blockedReason) return false
  return ['unauthorized', 'invisible', 'stale'].every((k) => blob.includes(k))
}

export function verifyRun(evidence, auditText = '') {
  const failures = []
  if (!isObject(evidence)) {
    return { ok: false, failures: [{ check: 'shape', message: 'evidence is not an object' }] }
  }
  if (evidence.blocked) {
    failures.push({ check: 'blocked', message: String(evidence.blocked) })
  }
  if (isMvpHostName(evidence?.hostProcess?.process)) {
    failures.push({
      check: 'host:mvp-impersonation',
      message: 'rust evidence must not impersonate lumio-mvp-host',
    })
  }
  if (!hasRustHostProcessAudit(evidence, auditText)) {
    failures.push({
      check: 'host:rust',
      message: 'hostProcess must name lumio-entity-chat-replay with a live pid; census comes from rust host-audit nent_* ids',
    })
  }

  const census = censusFromEvidence(evidence, auditText)
  if (census.botCount !== 100) {
    failures.push({ check: 'census:bots', message: `BotEntity count ${census.botCount}, expected 100 from per-entity nent ids` })
  }
  if (census.playerCount !== 1) {
    failures.push({ check: 'census:player', message: `PlayerEntity count ${census.playerCount}, expected 1` })
  }
  if (census.total !== 101) {
    failures.push({ check: 'census:total', message: `entity total ${census.total}, expected 101` })
  }
  if (census.netEntityIds.some((id) => isLauncherLoopIndex(id) || !isHostNetEntityId(id))) {
    failures.push({ check: 'census:ids', message: 'census ids must be host NetEntityId nent_*, not 1..101 loop indexes' })
  }

  for (let i = 1; i <= 11; i++) {
    const row = scenario(evidence, i)
    if (i === 3) {
      const pw = evidence?.playwright ?? row?.playwright
      if (pw?.injected === true) {
        failures.push({ check: 's3:injected', message: 'must not inject DOM events and mark Browser ok' })
      }
      if (pw?.ran === true) {
        const browser = String(pw.browser ?? '')
        if (!/chromium|firefox|webkit/i.test(browser) || pw.receivedFromNetwork !== true) {
          failures.push({
            check: 's3:playwright-claimed',
            message: 'playwrightRan true requires a real browser network capture, not an injected or in-process fake',
          })
        }
      }
      if (!isObject(row) || row.ok !== true) {
        failures.push({ check: 'scenario-3', message: 'scenario 3 Browser admission missing or not ok' })
      }
      continue
    }
    if (!isObject(row) || row.ok !== true) {
      failures.push({ check: `scenario-${i}`, message: `scenario ${i} missing or not ok` })
    }
  }

  if (!hasRealQueryTraces(evidence)) {
    failures.push({
      check: 's5:traces',
      message: 'scenario 5 requires real query traces (unauthorized/invisible/stale), not ok:true with empty traces',
    })
  }

  const s6 = scenario(evidence, 6)
  const chatAdmitted = s6.ok === true || Number(s6.eventCount ?? evidence?.traces?.chat?.eventCount ?? 0) > 0
  if (chatAdmitted) {
    if (s6.messageType !== 'InputCommand') {
      failures.push({ check: 's6:messageType', message: `scenario 6 messageType=${s6.messageType}, expected InputCommand` })
    }
    if (s6.mappingId !== 'chat.input') {
      failures.push({ check: 's6:mappingId', message: `scenario 6 mappingId=${s6.mappingId}, expected chat.input` })
    }
    if (!sha256Hex(s6.payloadSha256)) {
      failures.push({ check: 's6:payloadSha256', message: 'scenario 6 payloadSha256 must be lowercase sha256 hex' })
    }
    if (!lumioBinHex(s6.payload)) {
      failures.push({ check: 's6:payload', message: 'scenario 6 payload must be lowercase LumioBinV1 hex' })
    }
  }

  const s7 = scenario(evidence, 7)
  if (Number(s7.windowBeforeSnapshot ?? s7.chatEventsBeforeSnapshot ?? 0) <= 0) {
    failures.push({ check: 's7:snapshot-material', message: 'snapshot must exercise material that could have contained history' })
  }
  if (Number(s7.historyCountMax ?? 0) !== 0) {
    failures.push({ check: 's7:history', message: `snapshot historyCount=${s7.historyCountMax}` })
  }
  if (Number(s7.restoredWindow ?? 0) !== 0) {
    failures.push({ check: 's7:window-restore', message: 'Restore chat window must be empty' })
  }

  if (!isReconnectEntityRebound(evidence)) {
    failures.push({
      check: 's8:rebind',
      message: 'scenario 8 must rebind the same host NetEntityId (nent_*), not sessionId or login AccountId',
    })
  }

  const s9 = scenario(evidence, 9)
  const expiry = evidence?.traces?.expiry ?? {}
  const entityA = expiry.entityA ?? s9.entityA
  const entityB = expiry.entityB ?? s9.entityB ?? s9.netEntityIdB
  if (s9.tombstoned !== true && expiry.tombstoned !== true) {
    failures.push({ check: 's9:tombstone', message: 'scenario 9 must tombstone A' })
  }
  if (isHostNetEntityId(entityA) && isHostNetEntityId(entityB) && String(entityA) === String(entityB)) {
    failures.push({ check: 's9:new-id', message: 'scenario 9 entity B must use a different host NetEntityId' })
  }

  const s11 = scenario(evidence, 11)
  if (!Array.isArray(s11.eventOrder) || s11.eventOrder.length !== 101) {
    failures.push({ check: 'event-order', message: `eventOrder length ${s11.eventOrder?.length}` })
  }
  if (!Array.isArray(s11.appliedTicks) || !s11.appliedTicks.every((t) => Number(t) === 1)) {
    failures.push({ check: 'applied-tick', message: 'appliedTicks must all be 1 for the scale wave' })
  }

  const dump = JSON.stringify(evidence)
  if (dump.includes('"123456"') && /password/i.test(dump)) {
    failures.push({ check: 'password-leak', message: 'evidence must not echo the test password' })
  }

  return {
    ok: failures.length === 0,
    failures,
    census,
    eventOrder: (s11.eventOrder ?? []).map(eventOrderKey),
    appliedTicks: s11.appliedTicks ?? [],
  }
}

export function compareRuns(a, b, auditA = '', auditB = '') {
  const left = verifyRun(a, auditA)
  const right = verifyRun(b, auditB)
  const failures = []
  if (!left.ok) failures.push({ check: 'round-1', message: JSON.stringify(left.failures) })
  if (!right.ok) failures.push({ check: 'round-2', message: JSON.stringify(right.failures) })
  if (left.census.botCount !== right.census.botCount
    || left.census.playerCount !== right.census.playerCount
    || left.census.total !== right.census.total) {
    failures.push({ check: 'census-compare', message: 'entity counts differ across runs' })
  }
  if (JSON.stringify(left.eventOrder) !== JSON.stringify(right.eventOrder)) {
    failures.push({ check: 'event-order-compare', message: 'event order differs across runs' })
  }
  if (JSON.stringify(left.appliedTicks) !== JSON.stringify(right.appliedTicks)) {
    failures.push({ check: 'applied-tick-compare', message: 'applied Tick evidence differs across runs' })
  }
  return { ok: failures.length === 0, failures, round1: left, round2: right }
}

export function verifyEvidenceDir(dir) {
  if (!dir || !existsSync(dir)) {
    return { ok: false, failures: [{ check: 'pack:missing', message: `evidence dir missing: ${dir}` }] }
  }
  const r1 = join(dir, 'round-1', 'evidence.json')
  const r2 = join(dir, 'round-2', 'evidence.json')
  if (!existsSync(r1) || !existsSync(r2)) {
    return { ok: false, failures: [{ check: 'pack:rounds', message: 'missing round-1/round-2/evidence.json' }] }
  }
  return compareRuns(
    loadJson(r1),
    loadJson(r2),
    safeReadText(join(dir, 'round-1', 'host-audit.ndjson')),
    safeReadText(join(dir, 'round-2', 'host-audit.ndjson')),
  )
}

function parseArgs(argv) {
  const out = {}
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]
    if (!a.startsWith('--')) continue
    out[a.slice(2)] = argv[i + 1]
    i++
  }
  return out
}

if (!process.env.NODE_TEST_CONTEXT) {
  const args = parseArgs(process.argv.slice(2))
  if (args.dir) {
    const report = verifyEvidenceDir(args.dir)
    process.stdout.write(JSON.stringify(report, null, 2) + '\n')
    process.exit(report.ok ? 0 : 1)
  }
  process.stderr.write('usage: node verify_rust_evidence.mjs --dir <evidenceDir>\n')
  process.exit(2)
}

const test = process.env.NODE_TEST_CONTEXT ? nodeTest : () => {}

function nent(n) {
  return `nent_${String(n).padStart(32, '0')}`
}

function rustAudit() {
  const lines = [
    JSON.stringify({
      seq: 0,
      kind: 'audit',
      process: 'lumio-entity-chat-replay',
      eventId: 'host.start',
    }),
  ]
  for (let i = 1; i <= 100; i++) {
    lines.push(JSON.stringify({
      kind: 'entity_admitted',
      process: 'lumio-entity-chat-replay',
      roomId: 'room-main',
      netEntityId: nent(i),
      entityType: 'bot',
      sessionId: `sess-Bot${String(i).padStart(2, '0')}`,
    }))
  }
  lines.push(JSON.stringify({
    kind: 'entity_admitted',
    process: 'lumio-entity-chat-replay',
    roomId: 'room-main',
    netEntityId: nent(101),
    entityType: 'player',
    sessionId: 'sess-Browser01',
  }))
  return lines.join('\n') + '\n'
}

function currentGapEvidence() {
  const netEntityIds = Array.from({ length: 101 }, (_, i) => String(i + 1))
  const eventOrder = netEntityIds.map((id, i) => `${id}:hello:${i + 1}`)
  const appliedTicks = Array.from({ length: 101 }, () => 1)
  const scenarios = {}
  for (let i = 1; i <= 11; i++) scenarios[String(i)] = { ok: true }
  scenarios['1'] = { ok: true, wrongPasswordCode: 'wrong_password' }
  scenarios['5'] = { ok: true, unauthorized: 'Unauthorized', invisible: 'Invisible', stale: 'StaleGeneration' }
  scenarios['6'] = {
    ok: true,
    messageType: 'InputCommand',
    mappingId: 'chat.input',
    payload: '0b00000068656c6c6f2d426f743031',
    payloadSha256: '13b37ea0310268b2648b6ce23d0558a193952155edaac3d362f9793ad0063d9a',
  }
  scenarios['7'] = { ok: true, historyCountMax: 0, restoredWindow: 0 }
  scenarios['8'] = { ok: true, entityA: '100' }
  scenarios['11'] = { ok: true, eventOrder, appliedTicks, totalEntities: 101 }
  return {
    ok: true,
    census: { botCount: 100, playerCount: 1, total: 101, netEntityIds },
    scenarios,
  }
}

function goodRustEvidence() {
  const netEntityIds = Array.from({ length: 101 }, (_, i) => nent(i + 1))
  const eventOrder = netEntityIds.map((id, i) => `${id}:hello-${i + 1}:${i + 1}`)
  const appliedTicks = Array.from({ length: 101 }, () => 1)
  const scenarios = {}
  for (let i = 1; i <= 11; i++) scenarios[String(i)] = { ok: true }
  scenarios['1'] = { ok: true, wrongPasswordCode: 'wrong_password' }
  scenarios['3'] = { ok: true, total: 101, botCount: 100, playerCount: 1 }
  scenarios['5'] = { ok: true, unauthorized: 'Unauthorized', invisible: 'Invisible', stale: 'StaleGeneration' }
  scenarios['6'] = {
    ok: true,
    eventCount: 101,
    messageType: 'InputCommand',
    mappingId: 'chat.input',
    payload: '0b00000068656c6c6f2d426f743031',
    payloadSha256: '13b37ea0310268b2648b6ce23d0558a193952155edaac3d362f9793ad0063d9a',
    timerManagerInvoked: false,
    cadence: 'tick-batched',
  }
  scenarios['7'] = { ok: true, historyCountMax: 0, restoredWindow: 0, windowBeforeSnapshot: 101 }
  scenarios['8'] = {
    ok: true,
    rebound: true,
    entityA: nent(100),
    netEntityId: nent(100),
    previousNetEntityId: nent(100),
    sessionId: 'sess-Bot100-re',
    previousSessionId: 'sess-Bot100',
    accountId: 'acct_bot100',
    previousAccountId: 'acct_bot100',
  }
  scenarios['9'] = {
    ok: true,
    tombstoned: true,
    staleARejected: true,
    entityA: nent(99),
    entityB: nent(102),
  }
  scenarios['10'] = { ok: true, isoTotal: 2 }
  scenarios['11'] = { ok: true, eventOrder, appliedTicks, totalEntities: 101 }
  return {
    ok: true,
    hostProcess: {
      process: 'lumio-entity-chat-replay',
      pid: 4242,
      command: ['lumio-entity-chat-replay', '--out', 'round-1'],
    },
    playwright: { ran: false, injected: false, receivedFromNetwork: false },
    traces: {
      account: { createAck: true, loadAck: true, wrongPasswordCode: 'wrong_password' },
      queries: { unauthorized: 'Unauthorized', invisible: 'Invisible', stale: 'StaleGeneration' },
      chat: { eventCount: 101, messageType: 'InputCommand', mappingId: 'chat.input' },
      reconnect: scenarios['8'],
      expiry: { tombstoned: true, entityA: nent(99), entityB: nent(102), staleARejected: true },
      handshake: { completed: 101 },
    },
    census: { botCount: 100, playerCount: 1, total: 101, netEntityIds },
    scenarios,
  }
}

test('current-gap evidence: hardcoded 1..101 census is not host NetEntityId', () => {
  const report = verifyRun(currentGapEvidence())
  assert.equal(report.ok, false)
  assert.ok(report.failures.some((f) => f.check.startsWith('census') || f.check === 'host:rust'))
  assert.equal(report.census.total, 0)
})

test('current-gap evidence: missing rust hostProcess fails', () => {
  const report = verifyRun(currentGapEvidence())
  assert.ok(report.failures.some((f) => f.check === 'host:rust'))
  assert.equal(report.failures.some((f) => f.check === 'host:mvp-impersonation'), false)
})

test('impersonating lumio-mvp-host is rejected', () => {
  const ev = goodRustEvidence()
  ev.hostProcess.process = 'lumio-mvp-host'
  const report = verifyRun(ev, rustAudit())
  assert.equal(report.ok, false)
  assert.ok(report.failures.some((f) => f.check === 'host:mvp-impersonation' || f.check === 'host:rust'))
})

test('S8 sessionId-only rebind is not Entity A', () => {
  const ev = goodRustEvidence()
  ev.scenarios['8'] = {
    ok: true,
    rebound: true,
    entityA: 'sess-Bot100',
    netEntityId: 'sess-Bot100',
    previousNetEntityId: 'sess-Bot100',
    sessionId: 'sess-Bot100-re',
    previousSessionId: 'sess-Bot100',
    accountId: 'acct_bot100',
    previousAccountId: 'acct_bot100',
  }
  ev.traces.reconnect = ev.scenarios['8']
  const report = verifyRun(ev, rustAudit())
  assert.equal(report.ok, false)
  assert.ok(report.failures.some((f) => f.check === 's8:rebind'))
})

test('S8 AccountId-only match is not Entity A', () => {
  const ev = goodRustEvidence()
  ev.scenarios['8'] = {
    ok: true,
    rebound: true,
    entityA: 'acct_bot100',
    netEntityId: 'acct_bot100',
    previousNetEntityId: 'acct_bot100',
    sessionId: 'sess-Bot100-re',
    previousSessionId: 'sess-Bot100',
    accountId: 'acct_bot100',
    previousAccountId: 'acct_bot100',
  }
  ev.traces.reconnect = ev.scenarios['8']
  const report = verifyRun(ev, rustAudit())
  assert.equal(report.ok, false)
  assert.ok(report.failures.some((f) => f.check === 's8:rebind'))
})

test('S5 ok:true with empty traces fails', () => {
  const ev = goodRustEvidence()
  ev.traces.queries = {}
  ev.scenarios['5'] = { ok: true }
  const report = verifyRun(ev, rustAudit())
  assert.equal(report.ok, false)
  assert.ok(report.failures.some((f) => f.check === 's5:traces'))
})

test('admitted chat without InputCommand envelope fails', () => {
  const ev = goodRustEvidence()
  ev.scenarios['6'] = { ok: true, eventCount: 101 }
  const report = verifyRun(ev, rustAudit())
  assert.equal(report.ok, false)
  assert.ok(report.failures.some((f) => String(f.check).startsWith('s6')))
})

test('good rust pack: 101 census from per-entity nent ids', () => {
  const report = verifyRun(goodRustEvidence(), rustAudit())
  assert.equal(report.ok, true, JSON.stringify(report.failures))
  assert.equal(report.census.botCount, 100)
  assert.equal(report.census.playerCount, 1)
  assert.equal(report.census.total, 101)
  assert.ok(report.census.netEntityIds.every((id) => isHostNetEntityId(id)))
})

test('compareRuns: counts/event-order/ticks match; drifted order fails', () => {
  const a = goodRustEvidence()
  const audit = rustAudit()
  assert.equal(compareRuns(a, structuredClone(a), audit, audit).ok, true)
  const drifted = goodRustEvidence()
  drifted.scenarios['11'].eventOrder = drifted.scenarios['11'].eventOrder.map((x) => `${x}-x`)
  assert.equal(compareRuns(a, drifted, audit, audit).ok, false)
})
