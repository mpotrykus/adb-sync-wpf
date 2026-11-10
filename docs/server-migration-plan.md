# Plan: Move AdbSync engine to the Linux server

## Goal

Syncing must keep working when the user's Windows PC is off. The user has an always-on Linux server.

Approach: move the backend (orchestration, logic, persistence) to the Linux server as a headless service. Keep the WPF app as a control client. Follow up with a web UI on the same API. The Android companion app talks to the server instead of the PC.

Explicitly rejected:
- Peer-to-peer / Syncthing-style client-to-client sync. It needs a distributed conflict model (version vectors, tombstones) and a rewrite of the sync core, which is far more than the goal requires.
- Windows containers. They need a Windows host and the server is Linux.
- Big-bang full port to Linux. The WPF UI is Windows-only and the ADB/USB flows need a machine with phones attached.

## Target architecture

- **Server (Linux, Docker or systemd):** job definitions, scheduler, runner, manifests, run history, conflict state, database. Exposes an authenticated HTTP API plus a push channel (SignalR or websockets) for live run progress and the execution graph.
- **PC agent (Windows):** registers with the server, reports attached USB/ADB devices, and executes ADB and local-path work on request. Needed because the server cannot see USB devices or `E:\` folders.
- **WPF app:** thin control client of the API. No state of its own.
- **Android companion app:** talks to the server API directly (phone initiates, HTTPS). Does not depend on the PC being on.
- **Web UI (later):** consumes the same API. Once it exists, the whole app is effectively on Linux and Windows is only needed for USB/local-path agent work.

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
- Dockerfile and a compose file. Persist data on a mounted volume.
- Consider SQLite in place of raw JSON files for transactions and concurrent access. This is also the right place to harden against the empty-master / stale-manifest class of bugs (see memory: nomad_empty_master_stale_manifest).
- Migration path for existing data from the PC to the server.
- Tests run on Linux in CI. The existing flaky 2-device runner tests (see memory) need to be understood before relying on them.

### Phase 3: Companion app targets the server

- Point the phone at the server API. This is the first phase that delivers the user's goal (sync with the PC off).
- Auth/pairing flow, behaviour on flaky mobile networks, background execution within Android's limits.
- Preserve existing companion features: conflict picker, job sort order following the PC sort, run history graph.

### Phase 4: PC agent and WPF as client

- PC agent: register with the server, report devices, execute ADB and local-path work.
- Convert WPF to a client of the API. Remove direct Core usage from view models.
- Decide behaviour when the PC is off: jobs that need the agent are queued or skipped, visibly, never silently dropped.
- Retire the in-process engine in the WPF app once parity is verified.

### Phase 5: Web UI

- Consume the same API. Start with Run History and conflict resolution, then job editing.

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
