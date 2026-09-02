#!/usr/bin/env node
/**
 * Thin CLI over Game `runPlaywrightBrowser`. Does not copy the helper.
 * Never injects DOM chat events.
 */
import { pathToFileURL } from 'node:url'
import { resolve } from 'node:path'

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

function fail(error) {
  process.stdout.write(JSON.stringify({
    ran: false,
    injected: false,
    receivedFromNetwork: false,
    error,
  }) + '\n')
}

const args = parseArgs(process.argv.slice(2))
const gameRoot = process.env.LUMIO_GAME_ROOT
if (!gameRoot) {
  fail('BLOCKED: LUMIO_GAME_ROOT is not set')
  process.exit(2)
}
const spec = resolve(gameRoot, 'integration/entity-chat/scenarios.mjs')

let runPlaywrightBrowser
try {
  ;({ runPlaywrightBrowser } = await import(pathToFileURL(spec).href))
} catch (err) {
  fail(`import ${spec}: ${String(err && err.message ? err.message : err).split('\n')[0]}`)
  process.exit(0)
}

try {
  const result = await runPlaywrightBrowser({
    pageUrl: args['page-url'],
    password: args.password,
    resultPath: args['result-path'],
    consolePath: args['console-path'],
  })
  process.stdout.write(JSON.stringify(result) + '\n')
} catch (err) {
  fail(String(err && err.message ? err.message : err).split('\n')[0])
}
