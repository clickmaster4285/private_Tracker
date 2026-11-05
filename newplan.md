# Scalability — ~1000 concurrent employee agents without lag / overload

> **Status:** Plan only (not implemented).
> **Written:** 2026-11-05
> **Owns:** capacity to keep tracking, attendance, journey, presence, and admin web responsive with ~1000 desktop agents online at once (including a 09:00 login storm), without treating Redis as a sync cache or claiming live stream for all 1000 screens.
> **Does not own:** Security & DLP roadmap (`plan.md`), Terms removal, tracking-mode deletion, Linux/macOS lockdown, or changing collector semantics.

Execute with `@prompt.md`. Read `AGENTS.md` before each phase. Ship one phase at a time. A phase is done only when its checklist, its lab/load evidence, and the documentation handoff in `@prompt.md` are done.

---

## 1. Locked decisions

1. **Target is ~1000 concurrent logged-in agents** doing normal tracking + sync + presence + attendance + journey admin reads. Not “10k smooth.” `PRESENCE_WS_MAX_CONNECTIONS=10000` is a socket ceiling, not a capacity guarantee.
2. **Live stream stays a small concurrent media budget.** Default remains `LIVE_STREAM_MAX_STREAMS=25` (and watchers-per-employee). Scaling tracking to 1000 does **not** mean watching 1000 screens. Media may later move to a dedicated SFU tier; that is a separate phase.
3. **PostgreSQL remains the system of record for telemetry.** Redis stays for one-time login secrets, presence mirror, watch tickets, and publisher→instance registry. Redis is **not** a query cache for journey/logs and **not** the thing that absorbs sync volume by itself.
4. **09:00 login storms must be absorbed, not hoped away.** Today every login fires `RequestImmediateSync` with no server rate limit and a shared `DB_MAX_OPEN_CONNS=25`. At 1000 simultaneous logins that will queue/lag on the current stack.
5. **Prove with load tests before calling a phase done.** Code-derived estimates are not a substitute for measured p95 latency / error rate under N agents + 1–2 admin tabs.
6. **Current “comfortable” baseline without this plan:** roughly **100–150** hard-working agents on today’s single API + pool 25; **~300–500** is stress; **>500** is expected overload under your current defaults. This plan exists to move the smooth line to **~1000**.

## 2. Out of scope

- Raising live stream to “watch the whole fleet at once.”
- Using Redis as a general HTTP response cache for admin list pages (unless a later measured phase proves a specific cache key).
- Rewriting the client collector cadence as the primary scale fix (collection is local SQLite; the network path is SyncService).
- Multi-region active-active Postgres.
- Claiming Installer-Parity for server-only phases.

## 3. Current baseline (evidence from code / config)

### Client (per online PC)

| Knob / behavior | Value | Effect |
| --- | --- | --- |
| Collect | `ALPHA_COLLECT_INTERVAL_SEC=30` | Local only |
| Sync idle interval | `ALPHA_SYNC_INTERVAL_SEC=60` | Drain pass cadence |
| Heartbeat | stamped every authenticated sync pass → usually ≥1 `POST /app-status/sync` / min | Steady DB write load |
| Chunk | `ALPHA_SYNC_MAX_ROWS=1000`, `ALPHA_SYNC_MAX_BYTES≈1MB`, `CHUNK_DELAY_MS=150` | Backlog = burst POSTs |
| Empty tables | no HTTP if zero unsynced rows | Steady state ≠ full 12-endpoint fan-out |
| Login | `RequestImmediateSync` immediately | Storm amplifier |
| Presence | `ALPHA_WS_ENABLED` (on in current `client/.env`) | 1 long-lived `/api/v1/ws` per PC |
| Stream publish | `ALPHA_STREAM_ENABLED` (on in current `client/.env`) | Media only while watched |
| DLP | policy ~300s, alerts ~60s when enabled | Extra DeviceAuth traffic |

Sync pass can touch up to **12** DeviceAuth sync endpoints (hardware, network, storage, hardware-devices, location, session-events, app-status, permission-status, installed-apps, installed-packages, app-sessions, app-items). Empty fetches skip HTTP.

### Server

| Knob / behavior | Value | Effect |
| --- | --- | --- |
| Postgres pool | `DB_MAX_OPEN_CONNS=25` | Shared by sync + web + jobs |
| Body limit | 20MB | Per request |
| Sync / login rate limit | **none** | Storm accepted then queued |
| Presence cap | `PRESENCE_WS_MAX_CONNECTIONS=10000` | Socket ceiling only |
| Live streams | `LIVE_STREAM_MAX_STREAMS=25` | Hard media cap |
| Jobs | in-process tickers (session lifecycle ~60s, retention hourly, staleness hourly) | **No job queue** |
| Redis | secrets + presence/tickets/publisher registry | Not sync ingest |
| Deploy shape | one API process | SFU process-local |
| Retention (current `.env`) | `RETENTION_DAYS=168` | Larger journey/scan cost than 30-day default |

DeviceAuth on each sync: token hash lookup + async `TouchLastSeen` + machine `last_track` touch → extra DB ops beyond the upsert.

### Web

| Surface | Behavior | Scale risk |
| --- | --- | --- |
| `/live-stream` | `refetchInterval: 2000`, **no limit** | Full `ListAll` employees + heartbeats + consent every 2s per open tab |
| Dashboard presence | every 12s, `limit: 24` | Bounded |
| Journey / employees | infinite scroll on demand | Grows with admin browsing |

### Honest capacity bands (pre-plan, single API + pool 25)

| Concurrent agents | Tracking / attendance / journey | Live stream |
| --- | --- | --- |
| ≤ ~100–150 | Expected smooth | ≤25 publishers |
| ~150–300 | Usually OK; 09:00 spike slows briefly | ≤25 |
| ~300–500 | Stress / intermittent overload risk | ≤25 |
| > ~500 | Overload likely (timeouts, sync lag, slow admin) | ≤25 |
| Streaming specifically | — | Hard refuse above 25 |

---

## 4. Target

```
~1000 agents online
  presence WS + periodic sync (60s) + DLP polls
        │
        ▼
Load balancer → N API instances (REST / DeviceAuth / presence)
        │                    │
        │                    └── Redis (secrets, presence, tickets, pub registry)
        ▼
PostgreSQL (sized pool + IOPS; telemetry SoR)
        │
        ├── Sync ingest: rate-limited and/or queued (storm absorbed)
        ├── Admin JWT reads: paginated / bounded (no full-fleet poll every 2s)
        └── Live stream SFU: still small concurrent budget (25 default) or dedicated media tier
```

**Success criteria (must measure):**

- Steady state (1000 agents, normal work): sync p95 &lt; 2s; admin list p95 &lt; 1s; error rate &lt; 0.1%.
- 09:00 storm (1000 logins within ~60s): no cascade failure; backlog drains within a bounded window (target &lt; 10–15 min); admin UI remains usable (degraded OK, hung not OK).
- Live stream: still correct at ≤ `LIVE_STREAM_MAX_STREAMS`; overload of media must not take down sync.

---

## 5. Phases

### P0 — Measure and instrument (do first)

**Goal:** Stop guessing. Establish a repeatable load profile and dashboards.

**Work:**

1. Define load profile scripts/tools (can be external): N device tokens, login + immediate sync, then steady 60s heartbeat/app-status (+ optional session/item churn).
2. Server metrics (minimum): request rate/latency by route, pgx pool wait/acquire, WS count, sync 5xx/429, goroutine/CPU/RSS.
3. Capture baseline on current hardware: N=100, 300, 500, storm at N=100.

**Done when:**

- [ ] Baseline numbers recorded in this file’s §8 Results table (fill after lab).
- [ ] Clear bottleneck identified (pool wait vs CPU vs disk vs WS).

**Lab:** Run against a non-prod DB copy or isolated stack. Do not load-test production blind.

---

### P1 — Cheap wins (config + web hot path + client jitter)

**Goal:** Raise headroom without a queue rewrite. Enough for mid-hundreds; prerequisite for 1000.

**Server:**

1. Size Postgres (CPU/RAM/IOPS) for 1000 writers; raise `DB_MAX_OPEN_CONNS` / idle only after Postgres `max_connections` and memory math allow (document the formula used).
2. Revisit `RETENTION_DAYS=168` vs journey query cost; prefer shorter retention or partitioned/archival strategy if P0 shows scan pain.
3. Add **soft rate limits** on DeviceAuth sync and employee-login (per-IP and/or per-employee) that return honest 429; clients already back off.

**Client:**

1. **Login sync jitter:** after login / boot restore, delay `RequestImmediateSync` by a random 0–N seconds (env-tunable, e.g. `ALPHA_SYNC_LOGIN_JITTER_SEC=120`) so 1000 PCs do not align on the second.
2. Keep Installer-Parity: re-bake `config.enc` for new knobs.

**Web:**

1. Fix `/live-stream` (and theater) employee list: never unbounded `ListAll` every 2s at fleet scale.
   - Prefer `onlineOnly` + pagination/`limit`, or presence-driven incremental updates.
   - Keep URL-synced filters; keep infinite-scroll rules for list pages.
2. Ensure dashboard stays on bounded presence (`limit` already 24).

**Done when:**

- [ ] Live-stream poll no longer full-scans the fleet every 2s.
- [ ] Login jitter shipped in installer config path.
- [ ] P0 re-run at N=300 and N=500 shows pool wait / p95 improved vs baseline.
- [ ] Docs: `AGENTS.md` + `server/ARCHITECTURE.md` + `web/ARCHITECTURE.md` + client arch if knobs change.

---

### P2 — Horizontal API + Redis presence path verified

**Goal:** Multiple API instances share auth/presence state; REST/sync scale out.

**Work:**

1. Run **≥2** API processes behind a load balancer (HTTP/WS upgrade sticky optional; prefer Redis-backed presence so sticky is not required for Online).
2. Set `INSTANCE_ID` / `INSTANCE_PUBLIC_URL` correctly for live-stream publisher registry / watch redirect.
3. Confirm employee secrets and watch tickets work across instances (Redis required — already true for secrets).
4. Health checks that fail the instance out of LB when DB/Redis are down.

**Done when:**

- [ ] Kill one API: sync and presence recover on the other without wiping Redis secrets incorrectly.
- [ ] Watch ticket with publisher on instance A can be redirected/handled per existing `watchBaseUrl` design.
- [ ] Load test N=500 across 2 instances beats single-instance p95.

**Note:** Live SFU media remains process-local until P4. Do not claim multi-instance media without P4.

---

### P3 — Sync storm absorption (queue or equivalent)

**Goal:** 1000 simultaneous logins do not pin the DB pool.

Pick **one** primary design (document choice in §1 when implementing):

**Option A — Ingest queue (preferred for hard 1000):**

1. DeviceAuth sync endpoints enqueue validated batches (Redis/Asynq/NATS/Postgres `LISTEN` — choose one stack and stick to it).
2. Workers drain with bounded concurrency tied to DB pool.
3. Response contract: either synchronous “accepted for apply” with idempotent client retry, **or** keep sync HTTP synchronous but push heavy catalog work async — do not break DeviceAuth wipe/`rejectedIds`/idempotent upsert contracts without a migration plan.

**Option B — No queue, strict admission control:**

1. Global + per-employee concurrency semaphores on sync handlers.
2. 429 + `Retry-After` when saturated.
3. Client jitter (P1) mandatory and larger during storm.

**Done when:**

- [ ] Storm lab: 1000 logins in ≤60s; API stays up; backlog bound documented; no unbounded goroutine growth.
- [ ] Contract tests for sync acceptance / rejection / wipe action still pass.
- [ ] `AGENTS.md` + `server/ARCHITECTURE.md` updated; if client retry semantics change, client arch + installer notes.

---

### P4 — Live stream isolation (optional for tracking-at-1000; required if media grows)

**Goal:** Media load cannot starve sync/DB.

1. Keep product default **≤25** concurrent publishers unless product explicitly raises it after media sizing.
2. If raising streams: dedicated SFU/media nodes, TURN capacity, bandwidth math (kbps × publishers × watchers), separate process from sync API.
3. Admin UI tile limits stay (console 4 / theater 16) unless product changes them deliberately.

**Done when:**

- [ ] Chaos: saturate stream cap; sync heartbeat p95 for non-streaming agents stays within success criteria.
- [ ] Env/docs updated (`LIVE_STREAM_*`, TURN).

---

### P5 — Soak and release gate

**Goal:** Sign off “~1000 agents, no lag/overload” with evidence.

**Matrix:**

| Scenario | N | Duration | Pass rule |
| --- | --- | --- | --- |
| Steady tracking | 1000 | ≥2h | success criteria §4 |
| Login storm | 1000 in 60s | drain window | §4 storm rules |
| Admin browsing | 2 live-stream tabs + dashboard + journey | during steady | no full-fleet melt |
| Stream cap | 25 publishers | 30m | sync unaffected |
| Instance kill | 2 APIs | during steady | recovery &lt; 30s for new syncs |

**Done when:**

- [ ] §8 Results filled with dates, hardware, and numbers.
- [ ] Release notes call out capacity assumptions and new env knobs.
- [ ] No phase marked done without measured evidence.

---

## 6. Suggested env / ops knobs (to introduce in phases)

| Knob | Phase | Intent |
| --- | --- | --- |
| `DB_MAX_OPEN_CONNS` / Postgres `max_connections` | P1 | Raise only with hardware math |
| `RETENTION_DAYS` | P1 | Control table growth / query cost |
| Sync/login rate limit + `Retry-After` | P1/P3 | Admission control |
| `ALPHA_SYNC_LOGIN_JITTER_SEC` | P1 | Desynchronize 09:00 |
| `INSTANCE_ID` / `INSTANCE_PUBLIC_URL` | P2 | Multi-API live-stream registry |
| Queue concurrency / worker count | P3 | Bound DB apply rate |
| `LIVE_STREAM_MAX_STREAMS` | P4 | Media budget (default 25) |
| `PRESENCE_WS_MAX_CONNECTIONS` | ops | Keep ≥ online fleet; not a performance cure |

---

## 7. Non-goals disguised as scale

- Turning Redis into a journey cache without measuring read load.
- Raising presence max to 10k and calling the product “10k ready.”
- Raising `LIVE_STREAM_MAX_STREAMS` to 1000.
- Dropping unsynced client rows to “help the server” (violates retention / unsynced rules).

---

## 8. Results (fill during labs — leave blank until measured)

| Date | Phase | Hardware | N agents | Scenario | Sync p95 | Admin p95 | Pool wait | Errors | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| | P0 baseline | | 100 | steady | | | | | |
| | P0 baseline | | 100 | storm | | | | | |
| | P0 baseline | | 300 | steady | | | | | |
| | P1 | | 500 | steady | | | | | |
| | P2 | | 500 | multi-API | | | | | |
| | P3 | | 1000 | storm | | | | | |
| | P5 | | 1000 | soak 2h | | | | | |

---

## 9. Documentation handoff (when implementing)

Per `@prompt.md` / `AGENTS.md`:

- Always prepend `AGENTS.md` changelog for shipped behavior.
- Server phases → `server/ARCHITECTURE.md` (scalability §, env table, queue if added).
- Web poll fix → `web/ARCHITECTURE.md`.
- Client jitter / retry → `client/ARCHITECTURE.md` + Installer-Parity / `config.enc` note.
- Update this file’s **Status** and §8 when a phase completes.

---

## 10. One-line summary

**Today ~100–150 agents are the honest smooth band; ~1000 needs measurement (P0), cheap wins + live-stream list fix (P1), multi-API (P2), storm absorption (P3), optional media isolation (P4), and a signed soak (P5) — Redis alone will not get you there.**
