# Server migration audit (Phase 0)

Read-only audit for [server-migration-plan.md](server-migration-plan.md). Paths are relative to the repo root. Findings marked **(verified)** were confirmed by reading the code; nothing here has been reproduced at runtime.

## TL;DR

1. **Core ports easily.** `AdbSync.Core` is plain `net8.0` with no P/Invoke, registry, WPF, `SystemEvents` or named mutexes. The Linux problems are environmental: paths, timezone, PID-based locks, mDNS in Docker, adb keys, and cross-device moves.
2. **The WPF app is the engine host.** The runner, scheduler, change watcher, concurrency gating, companion HTTPS server and broadcaster all live in `AdbSync.App`. About 1,500 lines of services move to the server mostly unchanged. The work is in replacing in-process coupling (a shared mutable `AppConfig`, the view model acting as the engine's event sink, jobs keyed by list index) with an API.
3. **The goal can't be met through the companion app alone.** All file data moves over ADB. Two of the three jobs (Nomad Sculpt, Infinite Painter) sync another app's `Android/data` folder, which only the adb shell uid can read on Android 11+. **The server must reach the phones over wireless ADB** (same LAN or Tailscale), or the phone needs privileged access (Shizuku/root). This reverses the plan's assumption in Phase 3 and its "don't depend on wireless ADB" risk, so open decision 1 has to be reframed (see the end).
4. **There are two data-loss bugs in the sync core, independent of Linux.** Fix them before the server becomes the single source of truth (see §3.3).

---

## 1. Core portability

### 1.1 Blockers

| # | Location | Problem | Fix |
|---|---|---|---|
| A1 | `AdbSync.Core/Services/Config/AppPaths.cs:19-20` | `LocalApplicationData` resolves to `~/.local/share` (`/root/...` in a container), which is lost on rebuild. With `HOME` unset it becomes a relative path. | Inject the data root from config (`ADBSYNC_DATA_DIR`, default `/data`). Drop `AppPaths.Default` from server paths. |
| A2 | `Models/Config/GlobalSettings.cs:5-6`, `Services/Config/AppConfigStore.cs:13-14` | `MyDocuments` resolves to `$HOME`, so the default projects dir is `/root/AdbSync Projects` and gets saved back to settings. | Default from server config (`/data/projects`). |
| A3 | `EffectiveJobSettings.cs:31`, `SyncJobRunner.cs:68-70,83` | A carried-over Windows path (`D:\Documents\...`) is a *relative filename* on Linux. `Directory.CreateDirectory(masterPath)` silently makes an empty master in the working directory, feeding the empty-master bugs in §3.3. | Reject non-fully-qualified paths on load. Add an import step that remaps Windows roots. |
| A4 | `Services/Orchestration/SyncLockManager.cs:20-30` | The lock is considered alive if `pid=` names a running process. In a container the app is PID 1 on every start, so a crash leaves a lock that blocks the job for `StaleLockHours` (4h). Acquisition is also check-then-write (racy). | Hold an exclusive `FileStream` (`CreateNew`/`FileShare.None`) for the run, or an in-process lock if the server is the sole writer. Record instance ID plus start time. |
| A5 | `Services/Devices/MdnsBrowser.cs:21-33`, `AdbDeviceResolver.cs:78,131` | Docker bridge networks don't pass multicast, so mDNS returns nothing. Pair and connect throw once `CachedHostPort` goes stale. mDNS also doesn't cross Tailscale. | `network_mode: host`. Add a manual/static `ip:port` fallback and a "connect to ip:port" API. |
| A6 | `Services/Transfer/AdbProcessRunner.cs:6`, `AdbDeviceResolver.cs:13,49,129,147` | `"adb"` is portable, but the image must ship platform-tools. AdvancedSharpAdbClient's path validation needs checking on Linux. `StartServerAsync` forks a daemon, which needs `--init`/tini. **The adb RSA key in `~/.android` is outside `AppPaths`**, so without a volume every rebuild needs re-authorising and re-pairing on every phone. | Configurable adb path. Persist `ANDROID_USER_HOME`. Use tini. |

### 1.2 Behaviour changes

| # | Location | Problem | Fix |
|---|---|---|---|
| A7 | `Services/Config/ProjectsDirectoryMigrator.cs:42-45` | `GetPathRoot` is always `/`, so it always uses `Directory.Move`, which throws EXDEV across volumes. There's no copy fallback and a partial run leaves a half-moved state. | Catch the cross-device error, then copy-then-delete. |
| A8 | `RollbackPointStore.cs:25,71`, `DeviceSnapshotService.cs:19,55,121`, `ScheduleCalculator.cs:7`, `GlobalSettings.cs:92`, run log/step timestamps | Folder names and schedules use local time with no offset. Containers default to UTC, so DailyAt and quiet hours shift, and ordering and pruning of existing points break. DST fall-back can make `Prune` delete the newest point. | Set `TZ` and install tzdata. Longer term, use UTC names and an injected `TimeProvider`. |
| A9 | `LocalFileTreeScanner.cs:19`, `ExcludeMatcher.cs:23,39`, `PathSetExcludeMatcher.cs:11` | `Replace('\\','/')` corrupts legal Linux filenames that contain `\`. | Normalise only when the separator is `\`. |
| A10 | `LocalFileTreeScanner.cs:17-27` | Follows directory symlinks, so a loop causes a stack overflow. | Skip `LinkTarget != null`. |
| A11 | `Services/Common/JsonFileIo.cs:15,26-46` | There's no fsync before rename, so power loss can leave a zero-length JSON file. A corrupt checkpoint throws in `SyncOrchestrator.cs:23` outside any try and aborts the whole `RunAll`. | `Flush(true)` plus directory fsync. Quarantine corrupt JSON and fail safe. |
| A12 | `Services/Common/DirectoryUtil.cs:15,28-36` | Clearing ReadOnly is a `chmod u+w` on Unix. It can throw on CIFS or rootless mounts, and the Google Drive reason doesn't apply. | Best-effort on non-Windows. Run as matching PUID/PGID. |
| A13 | `PendingConflictStore.cs:28,37`, `RollbackPointStore.cs:20,77`, `SyncJobRunner.cs:232,356,374`, `DeviceAccessGate.cs:7`, `AdbDeviceResolver.cs:31` | Job and device names are compared case-insensitively but used as case-sensitive path segments on ext4. `Pixel` and `pixel` then share a gate but have separate manifests. | Enforce case-insensitive uniqueness at config validation; keep Ordinal on disk. |
| A14 | `ManifestStore.cs:69`, `PendingConflictStore.cs:136-140`, `CheckpointManager.cs:38`, `PushSafetyGuard.cs:59`, `RollbackPointStore.cs:44` | Names are raw path segments with no validation, so `..` or `/` is a traversal risk once names arrive over HTTP. | `SafeName` validator at the API and config boundary. |
| A15 | `AdbExeTransferEngine.cs:15` | The legacy engine pulls whole trees into `/tmp`, which may be a small tmpfs. | Temp under the data volume. |
| A16 | `NativeAdbTransferEngine.cs:15,76` | The `IAsyncDisposable` remote FS is never disposed, so a socket leaks per call. A long-running server will exhaust file descriptors. | `await using`. |
| A17 | `CompanionCertificateStore.cs:39-41` | The PFX has no password, is written non-atomically, and gets 0644 permissions, so the private key is world-readable on the volume. | Atomic write plus `SetUnixFileMode(0600)`. |
| A18 | `NativeAdbTransferEngine.cs:106,125-127`, `AdbExeTransferEngine.cs:66,111,123` | Per-file ops use `CancellationToken.None`. `docker stop` SIGKILLs after 10s, leaving half-done pushes and a stale lock (A4). | Wire `ApplicationStopping` into run cancellation. Raise `stop_grace_period`. |

The Windows-only code is all in `AdbSync.App` and stays there or gets replaced:
- single-instance mutex (`App.xaml.cs:52`)
- `SystemEvents` power and session hooks (`ChangeWatchHostedService.cs:32-33`)
- Run-key autostart (`StartupRegistration.cs`)
- WPF-imaging conflict thumbnails (`ThumbnailRenderer.cs:35`); the server needs ImageSharp or SkiaSharp

---

## 2. WPF to service coupling

### 2.1 Hosting logic that moves to the server
These all live in `AdbSync.App/Services`:

| Service | Role |
|---|---|
| `JobRunService` | Global semaphore, per-device gate, LastRunAt/LastSuccessAt, cancellation map. Not in Core. |
| `SchedulerHostedService` | 1-minute timer. The `Paused` flag is in-memory only (toggled from the tray). |
| `ChangeWatchHostedService` | Watch reconcile on config change, wait-for-app-close retry, suspend/resume teardown. |
| `AppGateService` | Staleness check, open app, close-and-sync. |
| `CompanionServerService` | Kestrel HTTPS + SSE, about 20 endpoints. This is the seed of the server API. |
| `CompanionBroadcastService` | adb broadcasts, heartbeat, phone provisioning. |
| `AppConfigService` | The cached config. |

The composition root is `App.xaml.cs:128-188`; it becomes the server `Program.cs`. The tray, the single-instance check, `--dashboard` and autostart stay client-side.

### 2.2 Coupling that won't survive an API boundary (verified)

- **Shared mutable `AppConfig`** (`AppConfigService.cs:8-15`). One cached instance is changed in place:
  - by the editors before Save. `DeviceEditorWindow.xaml.cs:110-125,145` edits `config.Devices` live, so the engine sees edits before Save and Cancel doesn't undo them.
  - by the engine: `JobRunService.cs:54,62`; the companion server writing token, enabled flag and device IP; `EnsureConnectedAsync` writing device caches.

  Runtime caches (`CachedHostPort`) are mixed into user config, so an API edit racing a run's save loses updates. **This is the biggest refactor:** DTOs, explicit CRUD, and splitting runtime state out of config.
- **Jobs keyed by list index.** `JobRunService.cs:32-41` maps `_runningJobs: ConcurrentDictionary<int, CTS>` and `RunJobAsync(int jobIndex)`. Callers look up the index by name first, so a delete or reorder in between hits the wrong job. The API must key jobs by a stable ID.
- **The view model is the engine's event sink.** `DashboardViewModel` implements `ISyncEventSink` (`App.xaml.cs:167-172`) and marshals through a blocking `Dispatcher.Invoke` (`DashboardViewModel.cs:178`), so the engine thread stalls on the UI.
- **The exception type crosses the boundary.** `CanForcePush = exception is PushSafetyException` (`DashboardViewModel.cs:129`). After a reload it falls back to matching the text "Safety check blocked push" (`DashboardWindow.xaml.cs:217`). This needs a typed error code.
- **The UI fakes status.** Snapshot and restore write `PhaseText` in the UI with no engine event (`DashboardWindow.xaml.cs:581,596,623,639`), so remote clients wouldn't see them.
- **Long blocking calls.** Run Now awaits the whole run; snapshot and restore are awaited for minutes; close-and-sync holds an HTTP request open for up to 20–25 minutes. These should become 202 plus push events.
- **The client assumes local disk.**
  - Thumbnails are loaded from `MasterPath` on disk (`ConflictsWindow.xaml.cs:85`).
  - "Open folder" actions use Explorer (project, logs, snapshot).
  - `OpenFolderDialog` is used for paths that become server paths.
- **Service locator.** `App.Services` is a static (`App.xaml.cs:35`) used from `DashboardWindow`. `DeviceEditorWindow` and `PairDeviceWindow` `new` up their own `AdbClient` and `AdbDeviceResolver` outside DI.

There is no `IProgress<T>` or `IObservable` to deal with. Cancellation is already a separate "stop" call.

### 2.3 Sizing
- **About 41 operations** the UI needs as endpoints. About 15 already exist in some form in `CompanionServerService`. Missing:
  - config CRUD
  - device management: list, test, browse folder, list packages
  - snapshots and rollback
  - run files and full logs
  - scheduler pause
  - force-push
  - the projects-directory move
  - watch test
- **About 16 live event types.** Proposed shape: keep the extended `JobStatus` as the main SSE message, and add `steps`, `log-append`, `config-changed`, and a one-shot `outcome` message for tray balloons.
- **Hotspots:**
  - `DashboardWindow.xaml.cs`: 726 lines and 14 dependencies, effectively a rewrite into a real view model.
  - The job, device and settings editors: move from live-object editing to DTOs.
  - `RunHistoryWindow`: move from in-process sinks and 1s polling to the API plus a stream.

---

## 3. Persistence

### 3.1 Under the data root (`%LOCALAPPDATA%\AdbSync`)

| Item | Path | Format | Concurrency |
|---|---|---|---|
| Settings, devices, jobs | `config/{settings,devices,projects}.json` | JSON | Temp-then-rename. The three files are written one after another, not as a transaction. No cross-process lock. The CLI rewrites the whole config after each run. |
| Dashboard UI state | `local-state.json` | JSON | UI-only. The companion's sort reads it. |
| Manifests | `manifests/{job}/{device}.manifest.json` | JSON | Atomic rename. Guarded only by the project lock. **Keyed by job name, independent of the master path.** |
| Pending conflicts | `pending-conflicts/{job}/{device}.pending.json` + `.blob` | JSON + file copies | In-process `SemaphoreSlim` only. Blob copy isn't atomic. |
| Resume checkpoint | `checkpoints/{job}.json` | JSON | Atomic. |
| Push-safety baseline | `push-safety/{job}.json` (not in `AppPaths`) | JSON | Read-modify-write with no lock. |
| Run history | `run-history.db` | **SQLite** (`Runs`, `RunLogs`, `RunSteps`, `RunFiles`) | One transaction per run. `Pooling=False`, no WAL, no `busy_timeout`, and the schema gate is in-process only. Unsafe on network mounts. |
| Transcript logs | `logs/transcript-*.log` | Serilog | Single writer (`shared` not set). |
| Companion TLS cert and token | `companion-cert.pfx`; token in plain text in `settings.json` | | See A17. |
| adb keys | `~/.android/` (outside the app) | | See A6. |

### 3.2 Under each project folder (`{ProjectsDirectory}/{job}/`)

| Item | Path | Notes |
|---|---|---|
| Master | `master/` | Merge writes temp-then-rename per file. Rollback restore is delete-all then copy, which isn't atomic. |
| Staging | `.sync_staging/{device}/` | Deleted after every merge, so every run re-pulls everything. The whole root is wiped on stale-lock reclaim. |
| Lock | `.sync_staging/.sync_lock` | See A4. |
| Conflict backups | `.sync_conflicts/{device}/{fileName}.{stamp}.conflict` | The directory part is dropped, so same-named files in different folders can collide. |
| Rollback points | `rollback/{local timestamp}/` | Under the project lock. |
| Device snapshots | `checkpoints/{local timestamp}/{device}/` | **No project lock**, so they can run against a live job. |

There's one SQLite DB already. Settling decision 3 (SQLite vs JSON) means moving manifests, checkpoints, conflicts, push-safety and config into it, with real transactions. That also lets the server enforce single-writer.

### 3.3 Empty-master / stale-manifest risks

These are the most important findings. Items 1 and 2 are **(verified)** by reading the code.

1. **A resumed pull checkpoint silently skips the push.** `SyncJobRunner.cs:121` sets `anyChange = true` only when resuming the Push phase. With a Pull checkpoint where all devices are done, `RunPullPhaseAsync` has nothing pending and returns `AnyChange=false`. The run then reports "no changes" and **clears the checkpoint** (`:155-160`), so the push never happens. It's triggered by:
   - a crash after the last device's pull checkpoint (`:337`)
   - a `PushSafetyException` (`:170`), after which the retry "succeeds" without pushing
2. **The manifest is committed before the push, so a stale device can overwrite a newer master.**
   - For master→device changes the merge records the new version in the manifest (`TwoWayMergeEngine.cs:72-78`), and the manifest is saved at merge time (`SyncJobRunner.cs:297`).
   - If the push then fails per file (errors swallowed at `NativeAdbTransferEngine.cs:111-114`), is cancelled, or is skipped (#1), the device still has the old file but the manifest says it has the new one.
   - The next run classifies that as device-changed / master-unchanged, which is `CopyToMaster` (`TwoWayMergeEngine.cs:168-172`). **The old device copy overwrites the newer master**, then spreads to the other devices.
   - **Fix:** commit manifest entries for to-device changes only after a confirmed push.
3. **The empty-master guard only catches a completely empty master** (`SyncJobRunner.cs:124-138`). A *partially* populated master keeps its stale manifests. Device files that are missing from master but match the manifest become `DeleteFromStaging`, and the push then deletes them from the phone. Causes include:
   - an interrupted rollback restore
   - an interrupted migration (A7)
   - a half-mounted volume
   - a case mismatch (A13)

   The push-safety check (`PushSafetyGuard.cs:29-35`) only catches drops below 25% of the historical maximum.
4. **Manifests are keyed by job name, not master location.** A changed projects dir, an unmounted volume, or a Windows path on Linux (A3) all give a fresh master next to old manifests. **Fix:** store the master path or ID in the manifest and refuse to run on a mismatch.
5. **Push-safety bypasses.**
   - `forcePush` skips the master-empty check entirely (`SyncJobRunner.cs:163-166`).
   - `RollbackService.RestoreAsync` pushes with no check (`RollbackService.cs:71-79`) and rebases the baseline down.
6. **Snapshot restore leaves manifests stale** (`DeviceSnapshotService.RestoreSnapshotAsync`), unlike rollback, and runs unlocked. The next sync treats the restored device as "changed": it overwrites master, and files missing from the snapshot become `DeleteFromMaster` on every device.
7. **Cross-process races.** Several stores are guarded only in-process (pending conflicts, SQLite schema, config, push safety), and the project lock is racy (A4). The WPF app and the server must never share a data or projects volume. Enforce a single writer with an exclusive lock on `{Root}/.instance.lock`.
8. **Torn JSON after power loss** (A11). A zero-length checkpoint aborts the whole `RunAll`.

Bugs 1 and 2 together plausibly explain part of the Nomad empty-master / stale-manifest history and are worth checking against it.

---

## 4. Companion app transport

### 4.1 Today
- **PC → phone (push):** `adb shell am broadcast` to `ProgressReceiver`, guarded by `android.permission.DUMP` so only the shell uid can send.
  - It carries phase, queued, heartbeat, completed, failed, cancelled and skipped (`CompanionBroadcast.cs:38-52`, `CompanionBroadcastService.cs:81-110`).
  - **Provisioning goes the same way.** Every 5 minutes, `event=config` pushes `url`, `token`, cert `fp`, PC name and device name, plus `pm grant WRITE_SECURE_SETTINGS`.
  - The URL is `https://<local IP routed to the device>:47615`.
  - There's no mDNS discovery, QR code or manual entry for the server.
- **Phone → PC (pull):** Kestrel HTTPS on `0.0.0.0:47615` with a bearer token (16 random bytes, plain text in `settings.json`).
  - The cert is a self-signed ECDSA P-256 that the phone pins by SHA-256 with hostname checks skipped (`PcClient.kt:353-399`).
  - Endpoints: status, SSE events, logs, history, steps, run/stop/dryrun, enabled, close-and-sync, conflicts (list, thumbnails, resolve, resolve-all), device endpoint report, adb pair. The full table is in `CompanionServerService.cs:155-173`.
  - SSE runs only while `MainActivity` is in the foreground.
- **File data:** **only over ADB** (AdvancedSharpAdbClient sync protocol, `AdbSyncRemoteFileSystem.cs`).
  - Change watch, the app-running guard and app open/close are also adb shell commands: `inotifyd`, `pidof`, `am force-stop`.
  - The only file bytes over HTTPS are conflict thumbnails.
- **Android background:**
  - `SyncService`: a `dataSync` foreground service started by the adb broadcast.
  - JobScheduler jobs: enable wireless debugging and report its port, pairing, and a 15-minute Wi-Fi-only refresh.
  - All of these are constrained to `TRANSPORT_WIFI`.

### 4.2 What changes if the phone targets the server
1. **Provisioning.** A server can only provision phones it can reach over adb. Needs a QR code or manual pairing path, and a stable URL (MagicDNS or a 100.x address) instead of a route-probed LAN IP.
2. **Push.** Without adb broadcasts the phone needs a background SSE connection in a foreground service, or periodic polling.
3. **TLS.** Pinning works unchanged. `tailscale cert` would allow real hostname verification.
4. **Device endpoint reports.** These take the IP from `RemoteIpAddress`. Over Tailscale that's the phone's 100.x address, which would overwrite `DeviceConfig.Ip`. **Unverified:** whether adbd's TLS listener and `adb pair` are reachable over the Tailscale interface. This is the key experiment (see the decisions below).
5. **Wi-Fi-only jobs** need variants that work over Tailscale or cellular.
6. **Hosting.** The companion server, broadcaster, app gate, change watch and thumbnails all have to move out of the `net8.0-windows` project.

---

## 5. Job classification

There's only one endpoint shape: a local `master/` under the projects dir, plus `JobDeviceBinding {DeviceName, RemotePath}` → `DeviceConfig` with either `Ip` (wireless ADB) or `Serial` (USB). No "companion" or "server storage" endpoint type exists.

| Job | Enabled | Schedule | Device path | App gate | (a) ADB | (b) local folder | (c) server-only |
|---|---|---|---|---|---|---|---|
| Nomad Sculpt | yes | OnChange | `/sdcard/Android/data/com.stephaneginier.nomad` (S23+, S9) | yes | wireless | projects dir | **no**: `Android/data` needs the shell uid |
| Infinite Painter | no | OnChange | `/sdcard/Android/data/com.brakefield.painter` (both) | yes | wireless | projects dir | **no**: same reason |
| Test | no | OnChange | `/storage/emulated/0/Test`, `/Test1` | none | wireless | projects dir | only with `MANAGE_EXTERNAL_STORAGE` plus a new transfer protocol |

- Both devices are **wireless ADB**, with no USB serials in use.
- "Local folder" (b) is just the projects dir. It moves to the server's disk with a path remap. The PC-local aspect only matters if the user wants the masters to stay in Google Drive on the PC.
- `OnChange` scheduling needs a live adb shell `inotifyd` watcher on each device, so the server holds long-lived adb shell sessions to the phones.

---

## Revised open decisions

The audit changes the shape of the plan's decisions:

1. **How does the server reach the phones?** The companion app can't replace ADB for `Android/data` jobs. Options:
   - **a) Server runs adb and connects to the phones over wireless ADB on the LAN** (needs `network_mode: host`, persisted adb keys and a manual `ip:port` fallback). Works when the phones are on home Wi-Fi, which matches the current Wi-Fi-only design. Wireless-debugging reliability (port changes, toggling off after reboot) is already handled by the companion's enable-and-report jobs, and those can report to the server instead.
   - **b) Same, but over Tailscale.** Needs an experiment first: can the server `adb connect` and `adb pair` to a phone's 100.x address?
   - **c) Shizuku or a privileged companion** that reads `Android/data` and uploads over HTTPS. Big new transfer protocol and a new Android dependency.
   - **d) Keep a PC agent** for these jobs. Doesn't meet the goal (PC off).

   Recommendation: **(a)**, with (b) as a follow-up experiment. The PC agent becomes optional: only needed for USB, and no current job uses USB.
2. **Masters on the server or in Google Drive on the PC?** If they move to the server, the projects dir is just `/data/projects`. Google Drive backup would then need a separate mechanism, such as rclone on the server.
3. **SQLite vs JSON.** Recommend **SQLite for all engine state**: manifests, checkpoints, conflicts, push-safety and config. It's already a dependency, gives transactions, and makes fixing §3.3 easier.
4. **Remote access.** Unchanged: Tailscale only. Note the current token is stored in plain text and the cert PFX is world-readable on Linux (A17).
5. **New: fix §3.3 bugs 1–2 first, on Windows, before Phase 1.** They're independent of the migration, cause real data loss, and get worse once the server is the single source of truth.

## Decisions (2026-11-10)

1. **Wireless ADB.** The server runs adb and connects to the phones over wireless ADB. The companion's enable-and-report jobs report to the server instead of the PC.
2. **Masters live on the server**, at a configurable projects directory. The user's own install will point it at a folder on one of the server's external USB drives, but nothing in the code may assume USB drives or this environment, because the tool should stay usable by others. Phase 2 rule: **if a job's project folder isn't reachable, refuse to run that job.** Report it visibly as "project folder unavailable", not as a failure that retries forever, and never as an empty master.
   - "Reachable" means the configured projects directory (or per-job override) exists and is readable and writable. An unreachable folder must never be created on the fly. Today `SyncJobRunner` calls `Directory.CreateDirectory(masterPath)`, which turns an unmounted drive into a fresh empty master next to old manifests (§3.3 #4).
   - Only the root has to exist already. The job's folder and `master/` underneath it may still be created, which is the normal path for a new job. The root is either the projects directory or the job's `ProjectDirectory` override.
   - I/O errors reading master mid-run (for example, a drive dropping out) fail the run, and are never interpreted as missing files.
   - This applies on Windows as well, so it can land before the server work.
   - Existence alone isn't enough, because an unmounted drive leaves its empty mount point behind (see decision 5). The root carries a marker file, `.adbsync-root`, holding a root id. It is written when the projects directory is first set up or chosen, and a run is refused with `project_folder_unavailable` if it's missing. Manifests record the root id, which also closes §3.3 #4.
3. **SQLite** for all engine state: manifests, checkpoints, pending conflicts, push-safety and config.
4. **Fix §3.3 bugs 1–2 first** on a separate branch and PR, before Phase 1. Done in PR #3, which also fixes a third bug found along the way: files that failed to pull were merged as deletions and removed from every device.
5. **Docker is the primary deployment** (Linux container, compose file in the repo). Running the host directly (for example under systemd) stays possible but isn't the tested path. What that requires:
   - **Host networking** (`network_mode: host`) in the shipped compose file. mDNS doesn't cross a bridge network (A5), and wireless adb connects out to a random port on each phone. Bridge mode is documented as degraded: no discovery, so devices need a static port or a companion-reported endpoint.
   - **Paths from configuration, never from `HOME`** (A1, A2). One data volume (`/data`: config, SQLite, certificate, adb keys, logs, temp files) and the projects directory as a separate bind mount (for example a folder on a USB drive mounted at `/projects`).
   - **adb keys persist** in the data volume. Otherwise every recreated container is a new adb host, and every phone has to authorize it again.
   - **Unmounted drives.** If the drive behind a bind mount isn't mounted, the container sees the empty mount point directory, which passes an existence check. The `.adbsync-root` marker (decision 2) catches this. The compose file uses `bind.propagation: rslave` so a drive mounted after the container starts becomes visible without a restart.
   - **Clean shutdown.** `init: true` so signals reach the host, SIGTERM cancels runs at file boundaries (A18), and `stop_grace_period` is long enough for that. Lock liveness can't rely on PIDs, because the host is PID 1 on every start (A4).
   - **Non-root user** with configurable `PUID`/`PGID`, so files on the projects drive are owned by a host user. Timezone from `TZ` plus the `timeZone` setting (A8).
   - **Image:** the ASP.NET 8 runtime with pinned platform-tools, built for amd64 and arm64, with a healthcheck on `/api/v1/health`. The first client pairs with `docker exec adbsync adbsync-server pair`.
   - **USB phones** need `/dev/bus/usb` passed through. Documented, but not in the default compose file.
