# AdbSync Server API, v1 (draft for review)

Phase 1 deliverable of [the migration plan](../server-migration-plan.md). The endpoints and schemas are in [`openapi.yaml`](openapi.yaml). This document covers the decisions behind them: conventions, auth, the event stream, how today's code maps onto the contract, and what the contract requires of the engine in Phase 2.

Nothing here is implemented yet. Review comments are most useful on the [open questions](#open-questions) at the end.

## Scope

In scope: everything the WPF dashboard, the companion app, and a future web UI need to operate the engine:
- jobs, runs and history (including the execution graph and per-file changes)
- devices, conflicts, snapshots and rollback points
- settings, the scheduler, and the live event stream

Out of scope for v1:
- **File content transfer.** All file data still moves over ADB between the server and the phones. The only bytes served over the API are conflict thumbnails.
- **A PC agent.** Decision 1 (wireless ADB from the server) makes it optional. The `/agents` prefix is reserved for it.
- **Multi-user permissions.** Every paired client has full control. Tokens are per client, so scopes can be added later without re-pairing.

## Conventions

| Topic | Rule |
|---|---|
| Base path | `/api/v1`. The major version is in the path. |
| Versioning | Within v1, changes are additive only: new endpoints, new optional fields, new enum values. Clients must ignore unknown fields and treat unknown enum values as "other" (unknown `ErrorCode` means a generic error). Breaking changes go to `/api/v2`, served side by side for at least one release. `GET /info` lists the served versions, the minimum supported client version per client kind, and a `features` list for feature detection. |
| Client identification | Clients send `User-Agent: AdbSync-<kind>/<version>`, for example `AdbSync-android/1.6.0`. |
| JSON | camelCase. Enums are lowerCamelCase strings. Nullable fields are always present (as `null`), never omitted, so "unset" and "not sent by an old server" can't be confused. |
| IDs | Jobs, devices and clients get stable opaque ids (UUIDs). Names stay unique, case-insensitively, but are display values. This replaces today's list-index keys (audit §2.2), so renames and reorders can't hit the wrong job. Run ids are assigned when a run is queued, not when it starts. |
| Names | Job names are also folder names, so they are validated at the boundary: 1–100 characters, no path separators or reserved characters, not `.` or `..` (audit A14). |
| Time | RFC 3339 timestamps in UTC. Schedule times (`dailyTimes`) and quiet hours are local times in the server's `timeZone` setting (an IANA name), never the host's local time (audit A8). |
| Durations | Integer seconds (`intervalSeconds`), never .NET `TimeSpan` strings. |
| Paths | Server paths and device paths are opaque strings, shown for display only. Clients never open them locally; the "open folder in Explorer" actions go away for remote clients. |
| Errors | [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) `application/problem+json` with a required `code` from `ErrorCode`. Clients branch on `code`, never on message text. This replaces today's matching of "Safety check blocked push" in error strings (audit §2.2). |
| Concurrency | Every config resource (job, device, settings) returns an `ETag`. Editors send `If-Match`, and a stale write gets `412 precondition_failed`, so two clients can't silently overwrite each other. Quick toggles (for example enabling a job) may omit `If-Match`, because merge-patching one field is safe. |
| Updates | `PATCH` with `application/merge-patch+json` ([RFC 7396](https://www.rfc-editor.org/rfc/rfc7396)). Arrays are replaced wholesale. |
| Config vs runtime state | Config resources carry config only. Runtime state (last run time, resolved device endpoint, connection state) lives on `JobStatus` and `Device.connection`, is written only by the engine, and is read-only in the API. This removes the shared-mutable-`AppConfig` problem (audit §2.2). |
| Long-running work | Nothing blocks for more than a few seconds. Sync runs are resources (`POST /jobs/{id}/runs` → `202` + `Run`). Other long actions return `202` + `Operation`: close-and-sync, snapshot, restore, and moving the projects directory. Progress arrives as events, and `GET` works as a fallback. |
| Pagination | Cursor-based (`limit`, `cursor` → `nextCursor`) on runs and run files. |
| Device-touching calls | Calls that need a device return `502 device_unreachable` / `device_command_failed` when adb fails. The exception is a connection test, which reports failure as data (`connected: false`). |

## Auth and pairing

The API can read and write the user's files, so it never listens without auth. It is designed to sit behind Tailscale or WireGuard, not on the open internet.

- **Transport.** HTTPS only. By default the server generates a self-signed ECDSA certificate (as today). Clients pin its SHA-256 fingerprint, which arrives in the pairing QR or the adb provisioning broadcast. Deployments can supply their own certificate instead (for example from `tailscale cert`), and clients then verify normally. The private key is written atomically with mode 0600 (audit A17).
- **Tokens.** Every client has its own bearer token, created at pairing, stored hashed on the server, and revocable from any client (`DELETE /auth/clients/{id}`). Revoking closes that client's event stream. This replaces today's single shared token, which is stored in plain text in `settings.json`.
- **Pairing a client.**
  1. An already-paired client calls `POST /auth/pairing-codes`, or the operator runs `adbsync-server pair` on the server host. The CLI path is how the first client gets in.
  2. The client being paired scans the QR code (`adbsync://pair?url=…&code=…&fp=…`) or types the URL and code.
  3. It calls `POST /auth/pair` and gets its token.

  Codes are single-use, expire after 10 minutes, and `/auth/pair` is rate-limited.
- **Zero-touch phone provisioning, kept.** When the server reaches a phone over adb, it can do what the PC does today: create a pairing code itself and push `url` + `code` + `fp` to the companion app with the `am broadcast` config event. The phone then pairs like any other client. Phones never need the QR path unless adb can't reach them.
- **Unauthenticated endpoints:** only `GET /info`, `GET /health` and `POST /auth/pair`.

## Events

One Server-Sent Events stream: `GET /api/v1/events`. SSE was chosen over SignalR or WebSockets because:
- the companion already uses it
- OkHttp and browsers support it with no extra library
- it passes through any HTTP proxy or tunnel
- every message flows server-to-client; commands are plain REST calls

### Envelope
```
id: 1042
event: job.status
data: {"seq":1042,"type":"job.status","at":"2026-11-10T14:03:22Z","data":{ ...JobStatus... }}
```
`seq` increases strictly within one server instance. The server keeps the last 2,000 events (or 15 minutes, whichever is smaller) for replay. A `: ping` comment is sent every 20 seconds so dead connections are noticed.

### Client algorithm
1. `GET /status`. This returns the full snapshot and the `seq` it reflects.
2. Open `/events` with `Last-Event-ID: <seq>`.
3. Apply events in order. If a `resync` event arrives (replay window exceeded, or the server restarted), go back to step 1.
4. On disconnect, reconnect with the last `seq` you saw, backing off from 1 to 30 seconds. This is what the companion does today.

### Event types

| Event | Payload | Default | Replaces today's |
|---|---|---|---|
| `job.status` | `JobStatus` | ✓ | `JobStatusTracker.Changed`; SSE `job` |
| `job.changed` / `job.deleted` | `Job` / `{id}` | ✓ | `AppConfigService.ConfigChanged` |
| `device.changed` / `device.deleted` | `Device` / `{id}` | ✓ | `ConfigChanged` |
| `device.connection` | `{deviceId, connection}` | ✓ | new: attach, detach and endpoint changes |
| `run.queued` / `run.started` | `Run` | ✓ | `JobQueued`, `RunStarted` |
| `run.steps` | `{runId, jobId, steps}` | ✓ | `ILiveRunStepSink.Changed`; `PhaseChanged` |
| `run.log` | `{runId, fromLine, lines}` | opt-in (`topics=run.log:<runId>`) | 1 s polling of `ILiveRunLogSink` |
| `run.finished` | `Run` (with `status`, `errorCode`) | ✓ | `JobCompleted` / `JobFailed` / `JobCancelled` / `JobSkipped` |
| `run.conflicts` | `{runId, jobId, deviceId, autoResolved, held, totalPending}` | ✓ | `MergeConflictsDetected`, `ConflictsPending` |
| `conflict.added` / `conflict.resolved` | `Conflict` / `{id, jobId, choice}` | ✓ | new |
| `watch.changed` | `{jobId, watch}` | ✓ | `WatchStarted` / `WatchDegraded` / `WatchStopped` |
| `scheduler.changed` | `SchedulerState` | ✓ | new: pause is in memory today |
| `settings.changed` | `Settings` | ✓ | `ConfigChanged` |
| `preferences.changed` | `DashboardPreferences` | ✓ | `DashboardUiStateStore.Saved` |
| `operation.updated` | `Operation` | ✓ | new: snapshot and restore progress the UI fakes locally today |
| `client.revoked` | `{clientId}` | ✓ (to that client) | new |
| `resync` | `{reason}` | ✓ | new |

`ChangeDetected` and `JobBlockedByApp` are not separate events: they show up as `JobStatus.state`, `blockedByAppOn` and `staleDevices`.

Tray balloons are driven from `run.finished`. Each client applies the shared notification rules (`settings.notifications` plus the job's overrides) itself.

## How today's code maps onto the contract

### Companion app (today's `/api/*` → v1)

| Today | v1 |
|---|---|
| `GET /api/status` | `GET /status` (`pc` → `server.name`; `lastResult` and `lastFailed` → `lastRun.status` / `errorCode`) |
| `GET /api/events` | `GET /events` |
| `GET /api/jobs/{name}/log` | `GET /runs/{activeRunId or last runId}/log?tail=200` |
| `GET /api/jobs/{name}/history` | `GET /runs?jobId=…` |
| `…/history/{runId}/log`, `/steps`, `/steps/live` | `GET /runs/{runId}/log`, `GET /runs/{runId}` (steps included), `run.steps` events |
| `POST /api/jobs/{name}/run`, `/dryrun` | `POST /jobs/{id}/runs` with `mode` |
| `POST /api/jobs/{name}/stop` | `POST /jobs/{id}/stop` |
| `POST /api/jobs/{name}/enabled` | `PATCH /jobs/{id}` `{enabled}` |
| `POST /api/jobs/{name}/checkpoint/discard` | `DELETE /jobs/{id}/checkpoint` |
| `POST /api/jobs/{name}/close-and-sync` (blocks up to 25 minutes) | `POST /jobs/{id}/app/close-and-sync` → `202 Operation` |
| `GET /api/conflicts`, `/thumb/{pc\|device}`, `/resolve`, `/resolve-all` | `/conflicts…`. The `pc` choice and side become `master`, because the master is no longer on a PC. |
| `POST /api/devices/{name}/endpoint`, `/pair` | `POST /devices/{id}/endpoint` (now with an optional explicit `host`, so a VPN address doesn't overwrite the LAN host), `POST /devices/{id}/pair` |

**Transition:** the server also serves today's `/api/*` routes as a thin compatibility layer on top of v1, so phones on the current build keep working while the new companion build rolls out. The layer accepts the legacy shared token and is removed after Phase 3. Without it, the server and the phone app would have to update in lockstep, which is what the plan wants to avoid.

### WPF dashboard
About 41 UI operations were counted in the audit (§2.3). Every one maps to an endpoint above. The ones that are new compared with the companion API:
- config CRUD for jobs, devices and settings
- device test, browse and packages, plus discovered devices
- watch test
- snapshots and restore
- run files and the full log
- scheduler pause
- force-push (`mode: forcePush`)
- moving the projects directory (plan, then move)
- server folder browsing for path pickers

## What the contract requires of the engine (Phase 2 work list)

- **Stable ids.** A one-time config migration assigns ids to existing jobs and devices, and job bindings switch from `deviceName` to `deviceId`. Runs keep `jobName` as recorded history.
- **Runtime state moves out of config.**
  - Out of `SyncJobConfig`: `Schedule.LastRunAt` and `LastSuccessAt`.
  - Out of `DeviceConfig`: `CachedHostPort` and `CachedAt`.
  - Both move into engine state (SQLite, decision 3).
- **Typed outcomes.** Run failures carry an `ErrorCode`, for example `PushSafetyException` → `push_safety_blocked`. `JobStatus.canForcePush` is derived from that code, not from message text.
- **Run ids at queue time.** The runner accepts a run id instead of generating one, so `POST …/runs` can return it immediately.
- **Project folder availability** (decision 2). Before a run, the server checks that the job's projects root is reachable. If it isn't, the run is skipped with `project_folder_unavailable`, and `JobStatus.availability` shows it. The server never creates the root.
- **Manifests remember their master.** Each manifest records the master path it was built against. On a mismatch the server refuses to run instead of merging (audit §3.3 #4).
- **Snapshot restore is safe.** It takes the job lock and resets manifests, like rollback restore already does (audit §3.3 #6).
- **The scheduler pause is persisted**, along with a `timeZone` setting.
- **Server-side thumbnails** (ImageSharp or SkiaSharp), replacing WPF imaging.
- **The event bus.** A single in-process publisher assigns `seq` and keeps the replay buffer. `ISyncEventSink`, the status tracker, live steps and live logs all feed it.
- **Name validation** at the API and config boundary (audit A13, A14).

## Open questions

1. **Legacy `/api/*` compatibility layer:** keep it through Phase 3 as proposed, or require the new companion build at the same moment the server goes live? Keeping it costs about a day and removes the lockstep risk.
2. **First-client pairing:** is `adbsync-server pair` on the server host (an SSH step) acceptable for the very first client? Alternatives:
   - the server prints a pairing code to its log on first start
   - an unauthenticated pairing window that stays open until the first client pairs (weaker)
3. **Job rename:** the contract allows renaming (`PATCH name`), which moves the project folder and engine state. Is renaming worth supporting in v1, or should names be immutable and renaming come later?
4. **`deleteProjectFolder` on job delete:** offering to delete the master is convenient but dangerous. Keep it as an opt-in flag, or leave folder deletion to the user entirely?
5. **USB devices:** `transport: usb` is kept so other users with phones plugged into the server can use it. Confirm that's wanted, even though your own setup is all wireless.
6. **Server folder browsing** (`/server-fs/browse`): by default it is limited to configured roots (`ADBSYNC_BROWSE_ROOTS`, default the data volume's mount points). Acceptable, or should path entry be text-only?
