// A shared ledger of requests to the Happy Wheels server, so tools stay under its daily quota.
//
// The server allows only a few hundred requests a day per IP address (not per account): after about 500 in a day
// (2026-10-25/26) it answered every request, the game's own level list included, with HTTP 429 and
// Retry-After: 86336 (24 hours), and a request sent during the block came back with a fresh 24 hours. So:
//   - every tool that talks to the server records each request here (serve.mjs records the game's own requests too,
//     since your play spends the same quota), in ~/.cache/hw-server/requests.jsonl (HW_LEDGER to move it);
//   - guard() refuses a request once the last 24 hours hold `budget` requests (default 300, HW_DAILY_BUDGET), which
//     leaves room for playing, and refuses everything while a 429 block is in force, since asking again can extend it;
//   - noteResponse() records a 429's Retry-After as the end of the block.
// The window is a rolling 24 hours: whether the server's is rolling or a calendar day is not known, and rolling is the
// safer guess.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const LEDGER = process.env.HW_LEDGER ?? path.join(os.homedir(), '.cache', 'hw-server', 'requests.jsonl');
const DAY = 86400000;
export const DEFAULT_BUDGET = Number(process.env.HW_DAILY_BUDGET ?? 300);

export class QuotaError extends Error {}

function read() {
  if (!fs.existsSync(LEDGER)) return [];
  return fs.readFileSync(LEDGER, 'utf8').split('\n').filter(Boolean).map((line) => { try { return JSON.parse(line); } catch { return null; } }).filter(Boolean);
}

function append(entry) {
  fs.mkdirSync(path.dirname(LEDGER), { recursive: true });
  fs.appendFileSync(LEDGER, JSON.stringify(entry) + '\n');
  // Keep the file small: drop entries older than two days once it passes 5,000 lines.
  const entries = read();
  if (entries.length > 5000) {
    const keep = entries.filter((e) => e.t > Date.now() - 2 * DAY || (e.blockedUntil ?? 0) > Date.now());
    fs.writeFileSync(LEDGER, keep.map((e) => JSON.stringify(e)).join('\n') + '\n');
  }
}

const when = (ms) => new Date(ms).toLocaleString();

// How much of the budget the last 24 hours used, when the next request frees up, and whether a block is in force.
export function status(budget = DEFAULT_BUDGET) {
  const now = Date.now(), entries = read();
  const recent = entries.filter((e) => e.request && e.t > now - DAY).map((e) => e.t).sort((a, b) => a - b);
  const blockedUntil = Math.max(0, ...entries.map((e) => e.blockedUntil ?? 0));
  const over = recent.length - budget + 1;
  return {
    used: recent.length, budget, remaining: Math.max(0, budget - recent.length),
    blockedUntil: blockedUntil > now ? blockedUntil : 0,
    freesAt: over > 0 ? recent[over - 1] + DAY : now,
  };
}

export function describe(s = status()) {
  if (s.blockedUntil) return `the server blocked this IP until about ${when(s.blockedUntil)}; don't send anything before then`;
  return `${s.used} of ${s.budget} requests used in the last 24 hours${s.remaining ? '' : `; the next one frees up at about ${when(s.freesAt)}`}`;
}

// Call before every request a tool makes; throws QuotaError instead of sending one it shouldn't.
export function guard(tool, what, budget = DEFAULT_BUDGET) {
  const s = status(budget);
  if (s.blockedUntil) throw new QuotaError(describe(s));
  if (s.remaining <= 0) throw new QuotaError(`daily request budget spent: ${describe(s)} (HW_DAILY_BUDGET or --daily-budget to change it)`);
  append({ t: Date.now(), request: true, tool, what });
}

// Record a request that was made without guard() (the game's own, through serve.mjs): counted, never refused.
export function note(tool, what) {
  append({ t: Date.now(), request: true, tool, what });
}

// Call with every response's status and Retry-After header (seconds).
export function noteResponse(statusCode, retryAfter) {
  if (statusCode !== 429) return;
  const seconds = Number(retryAfter) || 3600;
  append({ t: Date.now(), blockedUntil: Date.now() + seconds * 1000, status: 429 });
}

// node tools/hw-quota.mjs: print the ledger's state.
if (import.meta.url === `file://${process.argv[1]}`) {
  console.log(describe());
}
