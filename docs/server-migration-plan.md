# Plan: Move AdbSync engine to the Linux server

## Goal

Syncing must keep working when the user's Windows PC is off. The user has an always-on Linux server.

Approach: move the backend (orchestration, logic, persistence) to the Linux server as a headless service. A web UI on the server is the desktop client. The Android companion app talks to the server instead of the PC. The WPF app stays as a standalone Windows edition that runs the same engine locally, for setups without a server.

Explicitly rejected:
- Peer-to-peer / Syncthing-style client-to-client sync. It needs a distributed conflict model (version vectors, tombstones) and a rewrite of the sync core, which is far more than the goal requires.
- Windows containers. They need a Windows host and the server is Linux.
- Big-bang full port to Linux. The WPF UI is Windows-only and the ADB/USB flows need a machine with phones attached.

## Target architecture

- **Server (Linux, Docker or systemd):** job definitions, scheduler, runner, manifests, run history, conflict state, database. Exposes an authenticated HTTP API plus a push channel (SignalR or websockets) for live run progress and the execution graph.
- **Shared engine library (`AdbSync.Engine`, net8.0):** the hosting services that live in `AdbSync.App` today (run service, scheduler, change watch, app gate, companion broadcaster, and the v1 API endpoints if feasible). Used by both the server and the WPF desktop edition.
- **Web UI:** the desktop client of the server, served by the server. Required before the cut-over.
- **Android companion app:** talks to the server API directly (phone initiates, HTTPS). Does not depend on the PC being on.
- **WPF desktop edition (Windows):** runs the engine in-process, with no server. Not a client of the server. See [WPF desktop edition](#wpf-desktop-edition).
- **PC agent:** optional and not planned. Decision 1 (wireless ADB from the server) removes the need for it, and no current job uses USB. The `/agents` API prefix stays reserved.

## Phases

Each phase should ship and be testable independently. Do not start a phase until the previous one is verified.

### Phase 0: Audit (read-only, no code changes)

Produce a short report (`docs/server-migration-audit.md`) covering:
1. **Core portability.** What in `AdbSync.Core` is Windows-specific: P/Invoke, registry, Windows paths and path-separator assumptions, file-attribute and ReadOnly handling (see `DeleteRecursive`), `JsonFileIo` locking, ADB binary location and process spawning, anything WPF/Dispatcher-bound. Can it target plain `net*` and run under Linux unchanged? List each blocker with file and line.
2. **WPF to service coupling.** Which view models/services call Core directly, which rely on in-process events or shared mutable state, which assume synchronous results. This sizes the API-contract work, which is expected to be the largest part of the project.
3. **Persistence.** What is stored where (JSON files, project folders, manifests, history, settings), and how concurrent access is handled today.
4. **Companion app transport.** How the phone currently talks to the PC (protocol, discovery, auth), and what changes if it targets the server.
5. **Job classification.** Which jobs depend on (a) USB/ADB-attached devices, (b) PC-local folders such as the Google Drive projects dir, (c) neither, i.e. could run purely server-side.

Exit criteria: user has read the audit and agreed on the open decisions below.

### Phase 1: Define the API

- Resources: jobs, runs (with execution graph and per-file change history), devices, conflicts and resolutions, manifests, rollback points, settings, projects directory.
- Push channel for run progress, device attach/detach, and conflict prompts ("Ask me" policy).
- Auth: token-based, designed to sit behind Tailscale/WireGuard. Never expose raw to the internet.
- Version the API from day one, since the phone and the PC will not always update in lockstep.
- Deliverable: written contract (OpenAPI plus event schemas) and review before implementation.

### Phase 2: Host Core behind the API on Linux

- Extract or abstract the Windows-specific pieces found in Phase 0 behind interfaces.
- Create a headless host project (ASP.NET Core) that wires up Core and serves the Phase 1 API.
- Dockerfile and a compose file; Docker is the primary deployment (audit decision 5). Persist data on a mounted volume.
- Consider SQLite in place of raw JSON files for transactions and concurrent access. This is also the right place to harden against the empty-master / stale-manifest class of bugs (see memory: nomad_empty_master_stale_manifest).
- Migration path for existing data from the PC to the server.
- Tests run on Linux in CI. The existing flaky 2-device runner tests (see memory) need to be understood before relying on them.

Order (agreed 2026-11-11). Core stays shared with the WPF desktop edition, so every step keeps `AdbSync.App` building and working. One commit per step; review stops after steps 7 and 9.

Stage A, Core groundwork (testable on Windows, no server yet):
1. Linux CI job running `Core.Tests`; understand the flaky 2-device runner tests.
2. Name validation (audit A13, A14).
3. Paths from configuration: data root, adb path, `ANDROID_USER_HOME`; reject paths that aren't fully qualified (A1, A2, A3, A6).
4. Project folder availability and the `.adbsync-root` marker (decision 2). The root is never created on the fly. On upgrade, the marker is written automatically only if the root already contains at least one configured job's folder; otherwise it is set up explicitly.
5. SQLite engine state:
   - 5a: schema, single-writer `.instance.lock`, one-time JSON import that keeps a backup of the JSON files.
   - 5b: config with stable ids, bindings by `deviceId`, runtime state split out of config, persisted scheduler pause.
   - 5c: manifests (recording root id and master path, refusing on mismatch), checkpoints, pending conflicts, push-safety.
6. Runner contract: run id at queue time, typed `ErrorCode`, locks without PIDs (A4), cancellation at file boundaries (A18), safe snapshot restore (audit §3.3 #6).
7. Remaining portability fixes: A7, A8 (`timeZone` setting), A9, A10, A12, A15, A16, A17.

Stage B, the server:

8. `AdbSync.Engine` library: move the hosting services out of `AdbSync.App`; the Windows-only hooks (`SystemEvents`) go behind an interface. The WPF app uses it in-process.
9. `AdbSync.Server` skeleton with `/info` and `/health`, then the Dockerfile and compose file, verified early on the arm64 target.
10. Auth and pairing: certificate with mode 0600, hashed per-client tokens, pairing codes, `adbsync-server pair`. Required before listening beyond localhost.
11. Event bus: `seq`, replay buffer, `/status` and `/events`.
12. API slices: jobs and runs, devices, conflicts with ImageSharp thumbnails, settings/scheduler/preferences, long-running operations, folder browsing, job rename and delete.
13. Data migration from the PC: export with Windows path remap, import on the server, dry run against real data.

The installed WPF exe is only updated from this branch at checkpoints the user chooses.

### Phase 3: Companion app targets the server

- Point the phone at the server API. This is the first phase that delivers the user's goal (sync with the PC off).
- Auth/pairing flow, behaviour on flaky mobile networks, background execution within Android's limits.
- Preserve existing companion features: conflict picker, job sort order following the PC sort, run history graph.

### Phase 4: Web UI

- Consumes the same API, served by the server.
- MVP before the cut-over: status, run and stop, run history with logs and steps, conflicts, and job, device and settings editing.
- Then the rest of the dashboard: execution graph, snapshots and rollback, projects-directory move, device browse and packages.
- Stack still to decide: Blazor WebAssembly (C#, shares DTOs with the server) or React/TypeScript.

### Phase 5: Cut-over

- Import the PC's data on the server, pair the companion apps against v1, and stop the WPF app's engine on the PC.
- The previous plan's "PC agent and WPF as API client" phase is dropped (decided 2026-11-11). Converting the WPF code-behind into an API client would be roughly a rewrite and duplicate the web UI.

## WPF desktop edition

Decided 2026-11-11: the WPF app is kept as an option, not as a client of the server.

- `wpf-v1` tags the last WPF-only version (`main` at 2d08e65) so it can always be rebuilt as it was.
- `AdbSync.App` stays in the repo and runs the engine in-process through `AdbSync.Engine`, so it gets every Core fix.
- **No new features.** It only has to keep building and working on the current Core. New UI work goes to the web UI.
- If keeping it working ever needs more than small adaptations, it is dropped back to the `wpf-v1` tag.
- Open: the companion app talks to the v1 API after the cut-over. Preferably the v1 endpoints live in `AdbSync.Engine` so the desktop edition serves them too; check this when the engine library is extracted.

## To-do

- **Rename the GitHub repo** from `adb-sync-wpf` (for example to `adb-sync`). The repo is kept rather than replaced, because Core, the tests, the Android app, history and PR #4 all carry over. GitHub redirects the old URL; update local remotes afterwards. Nothing in the repo refers to the old name.
- Decide whether `AdbSync.Cli` stays as a separate tool or folds into `adbsync-server`.

## Open decisions for the user

1. Must USB/ADB-attached-phone flows keep working, or can everything go through the companion app? (Decides whether the PC agent is required or optional.)
2. Should PC-local folders (e.g. Google Drive projects dir) be synced through the agent, or should that data move to the server?
3. SQLite vs. keep JSON files for the server database.
4. Remote access model: Tailscale/WireGuard only, or also a public endpoint with mTLS.

## Risks

- **API surface is the bulk of the work**, not the Linux port. In-process assumptions in the view models will surface late.
- **Wireless ADB is unreliable for unattended use** (drops on reboot, needs re-pairing). Do not make the server depend on it. Prefer the companion-app HTTPS path.
- **Server becomes the single source of truth.** Manifest and conflict-state correctness matters more, not less.
- **Security:** the API can read and write the user's files. Auth must be in place before it listens on anything other than localhost.
- Several recent UI areas have never been eyeballed (conflict picker, run history polish, projects dir migration, rollback points UI restore). Verify their behaviour before building the API on top of them, or note them as known-unverified.

## Working agreements for the implementer

- `main` is branch-protected: PR + build-and-test + linear history. Work on a feature branch per phase.
- Do not add Claude as author or co-author on commits or PR descriptions.
- Do not add code comments except for non-obvious workarounds or gotchas.
- Never inject input on the S23+ without a screenshot confirming the companion app is on top.
- Start with Phase 0 only and stop for review before Phase 1.
