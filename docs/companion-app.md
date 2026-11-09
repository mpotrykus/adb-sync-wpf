# Android companion app: design notes

Status: phase 1 built (see "Phase 1: as built" below). Sideload only (no Play Store).

## Goals

1. Show running sync progress on the phone.
2. Auto-enable wireless debugging on the device and report its `ip:port` to the PC.
3. Keep the phone's Wi-Fi alive during syncs (wake lock + Wi-Fi lock) to reduce sleep/Doze socket drops.
4. Extras: completion/failure notifications, "sync now"/cancel from the phone, storage readout.

## Feasibility (Android side)

- Android does not block or penalize a companion app because the PC runs adb. adb talks to `adbd`; the app is an independent normal app.
- `WRITE_SECURE_SETTINGS` is grantable once over USB: `adb shell pm grant <pkg> android.permission.WRITE_SECURE_SETTINGS`. After that the app can set `Settings.Global.adb_wifi_enabled = 1` (Android 11+).
- Wireless debugging turns itself off when the phone leaves a trusted Wi-Fi network, and the port changes every time it is enabled. The app can discover its own `_adb-tls-connect._tcp` service via `NsdManager` and report `ip:port` to the PC.
- First-time pairing (6-digit code) is manual. Automating it (Shizuku-style: discover `_adb-tls-pairing._tcp`, take the code from a notification RemoteInput) is possible but optional and last.
- Android revokes adb keys after 7 days of non-use unless "Disable adb authorization timeout" is on. Surface a check/reminder.
- Android 14+: foreground services need a declared type (`dataSync` is the likely fit; time-limited on Android 15). Verify that a shell-originated `am broadcast` to an explicit manifest receiver still works on the target device. OEM skins (Samsung) vary; test on the real device.

## Transport options for progress

- **HTTP/SSE pull (preferred):** PC hosts a small server on the LAN; the app subscribes. Works when adb is down. Enables two-way features (sync now, cancel). Needs a firewall rule and a shared-secret token.
- **adb broadcast push (fallback):** `adb shell am broadcast -n <pkg>/.ProgressReceiver --ei pct 42 --es job X` drives a foreground-service notification with a progress bar. Only works while adb is connected.
- Without an app, `adb shell cmd notification post` gives plain-text notifications.

## PC-side findings (from codebase exploration; verify line numbers before editing)

Structure: `AdbSync.slnx` with `AdbSync.Core` (net8.0), `AdbSync.App` (WPF, net8.0-windows, Generic Host + Serilog), `AdbSync.Cli`, `AdbSync.Core.Tests` (xunit + NSubstitute, fakes under `Orchestration/Fakes/`), `AdbSync.Core.IntegrationTests`. Style: primary constructors, file-scoped namespaces, `IFoo` + `Foo` per service. There was no `docs/` folder before this file; `JOB_RUN_CALL_GRAPH.md` at the root documents the run call graph.

Progress model:
- The only progress abstraction is `ISyncEventSink` (`AdbSync.Core/Services/Orchestration/ISyncEventSink.cs`), keyed by job name: `PhaseChanged(jobName, SyncPhase, deviceName?)`, `JobQueued/Skipped/Completed/Failed/Cancelled`, `MergeConflictsDetected`, `WatchStarted/Degraded/Stopped`, `ChangeDetected`.
- `SyncPhase`: PreConnect, Pull, Push, WaitingForAppClose, Merge, WaitingForDevice.
- No per-file or byte-level progress exists. `TransferResult` only reports totals after completion, and `IAdbTransferEngine.PullMirrorAsync/PushMirrorAsync` take no progress callback. Real percent progress needs a new hook through `NativeAdbTransferEngine`, `MirrorPlanApplier`, `AdbSyncRemoteFileSystem`.
- `DashboardViewModel` (`AdbSync.App/ViewModels/`) is the sole registered sink (resolved in `App.xaml.cs`). `ILiveRunLogSink` (`Core/Services/Logging/`) exposes pollable in-progress log text per job via `TryGet`.
- `JobRunService` (`AdbSync.App/Services/`) runs jobs and exposes `CancelAllRunning`.

adb handling:
- Main path is AdvancedSharpAdbClient (`IAdbClient`, `IAdbServer` singletons). `AdbProcessRunner` (adb.exe wrapper) is only used by the older, unregistered `AdbExeTransferEngine`.
- `AdbDeviceResolver.EnsureConnectedAsync` (`Core/Services/Devices/`) resolves Wi-Fi devices by cached `ip:port`, then mDNS `_adb-tls-connect._tcp` (Tmds.MDns, 5s). adb's own built-in mDNS is deliberately disabled (commit 8b95e5f). Reported `ip:port` from the phone could become a more reliable source than mDNS.
- Suspend/shutdown: `ChangeWatchHostedService` handles `SystemEvents.PowerModeChanged` and `SessionEnding`; teardown cancels jobs, disconnects Wi-Fi devices, then `adbServer.StopServerAsync`. Any new service must not hold adb sockets across that and should clean up in `StopAsync` (host stop timeout is 5s).

Hosting and config:
- No HTTP/IPC code exists. Add a `BackgroundService` in `AdbSync.App/Services/`, registered with the same two-line `AddSingleton` + `AddHostedService` pattern in `App.xaml.cs` `ConfigureServices`.
- `HttpListener` needs no new dependency. Kestrel needs a `Microsoft.AspNetCore.App` FrameworkReference on the net8.0-windows project.
- Fan out events with a composite `ISyncEventSink` wrapping `DashboardViewModel` rather than replacing it.
- Settings: add properties to `GlobalSettings` (`Core/Models/Config/`) for port, token, enabled flag; check `AppConfigJsonContext` (source-gen). Per-device companion info (reported `ip:port`, last seen) would go on `DeviceConfig`, which today has only `Name`, `Ip`, `Serial`, `CachedHostPort`, `CachedAt`.
- UI: `SettingsWindow`, `DeviceEditorWindow` in `AdbSync.App/Views`.

## Proposed phases

1. **Companion skeleton + locks.** Android project (Kotlin, Gradle) with a foreground service holding a partial wake lock and `WIFI_MODE_FULL_LOW_LATENCY` lock, a battery-optimization exemption prompt, and an explicit-manifest `ProgressReceiver` that posts a progress notification. PC side: composite sink that sends `am broadcast` on `PhaseChanged`/`Job*` for connected devices. Validates the riskiest assumptions on the real device.
2. **HTTP/SSE feed.** `BackgroundService` serving job state (phase, outcome, live log tail) with token auth; app subscribes and renders. Add sync-now/cancel endpoints. Optionally add per-file progress hook in the transfer engine.
3. **Wireless debugging auto-enable.** One-time `pm grant`, app toggles `adb_wifi_enabled`, watches connectivity, discovers its own `_adb-tls-connect._tcp` service and POSTs `ip:port` to the PC; PC stores it in `CachedHostPort`.
4. **Pairing automation (optional).** NSD pairing service discovery + notification RemoteInput for the code.

## Phase 1: as built (2026-11-08)

Android project: `AdbSync.Android/` (outside `AdbSync.slnx`), package `com.adbsync.companion`, minSdk 30, targetSdk 36, no library dependencies (framework APIs only).

- `ProgressReceiver`: explicit, exported, guarded by `android.permission.DUMP`. The adb shell uid holds DUMP but third-party apps can't get it, so only `adb shell am broadcast` (or the system) can drive it.
- `SyncService`: `dataSync` foreground service. Holds a partial wake lock (6h cap) and a `WIFI_MODE_FULL_LOW_LATENCY` Wi-Fi lock while any job is active. Stops 10s after the last job ends, after 15 min with no event/heartbeat from the PC, or on the Android 15+ `onTimeout`.
- Result notifications for completed/failed (not cancelled/skipped).
- `MainActivity`: notification permission, battery-exemption prompt, adb authorization timeout check, "simulate a 10s sync" button.

PC side:
- `CompanionEventSink` (Core/Services/Companion) maps `ISyncEventSink` calls to `CompanionEvent`s and tracks active jobs. `WaitingForAppClose` releases the phone (user is on the phone; the wait can last hours). A `JobSkipped` with `SyncJobRunner.AlreadyRunningReason` is ignored so a duplicate trigger doesn't end the real run.
- `CompanionBroadcastService` (App/Services, hosted): queue + single sender, heartbeats active jobs every 5 min. Sends only to the job's devices that adb already reports online (matches `CachedHostPort` or `Serial`), never connects anything, 5s timeout per send, failures logged at Debug.
- `CompositeSyncEventSink` fans out to `DashboardViewModel` + the companion sink.
- `GlobalSettings.CompanionBroadcastsEnabled` (default true; no UI yet).

Broadcast protocol (`CompanionBroadcast.cs` ↔ `Protocol.kt`):
`am broadcast -n com.adbsync.companion/.ProgressReceiver -a com.adbsync.companion.EVENT --es event <phase|queued|heartbeat|completed|failed|cancelled|skipped> --es job <name> [--es phase <SyncPhase>] [--es device <name>] [--es message <text>] [--ez pushed true]`

Verified on SM-S916U (Android 17): shell broadcast starts the foreground service, wake lock and low-latency Wi-Fi lock are held, both are released ~10s after the terminal event. **Without the battery-optimization exemption Android blocks the service start** (`ForegroundServiceStartNotAllowedException`, caught; result notifications still post). The exemption is required.

Build and setup (per device):

```
cd AdbSync.Android
$env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"   # Gradle 8.14 can't run on JDK 25
.\gradlew.bat assembleDebug
adb -s <serial> install -r app\build\outputs\apk\debug\app-debug.apk
adb -s <serial> shell pm grant com.adbsync.companion android.permission.POST_NOTIFICATIONS
adb -s <serial> shell "dumpsys deviceidle whitelist +com.adbsync.companion"
```

End-to-end verified 2026-11-08 with the dev build: "Run Now" on the Test job lit up the S23's ongoing notification ("Test: Pushing · Mitch S9"), posted "Synced Test / Changes pushed to device", and the service stopped ~10s later with no wake or Wi-Fi lock held. Nomad sitting in WaitingForAppClose did not start the service.

Not yet done: Tab S9 (Android 16), a settings UI toggle, and confirming the DUMP guard rejects a non-shell sender. Both phones in a multi-device job get the same message, so the device label is whichever device last changed phase.

## Phase 2: as built (2026-11-08)

PC:
- `JobStatusTracker` (Core/Services/Companion): thread-safe per-job snapshot (running, phase, per-device phases, last result), seeded from run history at server start.
- `CompanionServerService` (App/Services): Kestrel (`Microsoft.AspNetCore.App` framework reference) on `0.0.0.0:CompanionServerPort` (default 47615), HTTPS only. Endpoints, all needing `Authorization: Bearer <CompanionServerToken>`:
  - `GET /api/status`: `{pc, jobs:[{name, enabled, running, phase, devices:[{device, phase}], lastResult, lastFailed, lastResultAt}]}`
  - `GET /api/events`: SSE. `snapshot` event (same shape as status) on connect, then a `job` event per change, `: ping` every 20s.
  - `GET /api/jobs/{name}/log`: last 200 lines (live log while running, otherwise the latest run's).
  - `POST /api/jobs/{name}/run` (202) and `POST /api/jobs/{name}/stop` (202, or 409 if not running).
- TLS: `CompanionCertificateStore` creates a self-signed ECDSA P-256 cert once (`%LOCALAPPDATA%\AdbSync\companion-cert.pfx`, 10 years, regenerated if missing, corrupt or within 30 days of expiry).
- Provisioning: `CompanionBroadcastService` sends `--es event config --es url https://<pc-ip>:<port> --es token <t> --es fp <cert sha256> --es pc <machine>` to every configured device adb has online, 10s after start and every 5 min. The PC IP is the local address of the route to the device.
- Token generated on first start into `GlobalSettings.CompanionServerToken`.

Phone:
- `PcConfig` stores url/token/fingerprint (https only). `PcClient` pins the exact leaf certificate (SHA-256) and skips hostname checks (the cert can't name a DHCP IP). Cleartext is disabled.
- `MainActivity` streams `/api/events` while in the foreground (reconnect with 1s→30s backoff) and shows each job with Run now / Stop (confirms when pushing) / Log.

Verified from the PC: 401 without token, TLS 1.3, plain HTTP refused, served cert fingerprint equals the one provisioned to the S23, SSE shows per-device phases through a full run, run/log endpoints work, stop returns 409 for an idle job. On the S23 over pinned HTTPS: "Connected to MITCH-PC", live job list, "Run now" on Test ran it (PC log: "Companion app requested a run of 'Test'") and the row flipped to "Success", "Log" showed the run's log. Stop from the phone wasn't exercised on a live run (Test finishes in ~1.5s; Nomad is a real job).

## Phase 3: as built (2026-11-08)

- Provisioning now also sends the AdbSync device name (`--es device`) and, once per device per session, runs `pm grant com.adbsync.companion android.permission.WRITE_SECURE_SETTINGS` over adb. No manual USB step.
- `POST /api/devices/{name}/endpoint {port}`: the IP is taken from the connection, not the body. Updates `DeviceConfig.Ip`/`CachedHostPort`/`CachedAt` and saves (no-op if unchanged).
- `AdbDeviceResolver` now tries `adb connect` to the cached `ip:port` before falling back to mDNS, so a reported port is actually used.
- Phone (`CompanionJobService`, `WirelessDebugging`):
  - Job 1 (requires Wi-Fi): enable `adb_wifi_enabled` if allowed and off, then find our own `_adb-tls-connect._tcp` port via `NsdManager` (matched on our Wi-Fi IPv4) and report it.
  - Job 3: content trigger on `Settings.Global adb_wifi_enabled`, re-armed every run. Off while not on Wi-Fi schedules job 1 (re-enable on rejoin); off while on Wi-Fi is treated as the user's choice; on schedules a port report.
  - Job 4: periodic (15 min), Wi-Fi, persisted; reports the port.
  - `BootReceiver` re-arms on boot and app update; `MainActivity` re-arms on resume. Toggle "Turn wireless debugging back on when Wi-Fi connects" (default on).

Findings on the S23 (Android 17):
- **Android 17 re-enables wireless debugging by itself** on trusted networks (`AdbWifiNetworkMonitor ... Attempting to start adb over Wi-Fi`), on a new port each time, and keeps `adb_wifi_enabled=1` throughout, so job 3 never fires there. The PC's own mDNS rediscovery found the new port within ~20-40s. The re-enable path (job 3 → job 1) is for Android ≤16 and is untested.
- A `ConnectivityManager.registerNetworkCallback(PendingIntent)` registration did not survive the app process being killed on this device, so it was replaced with the jobs above.
- Port reports verified (HTTP 204) after install, on resume, and when wireless debugging toggled.

## Phase 4: as built (2026-11-08)

- "Pair with PC over Wi-Fi" posts a notification with a RemoteInput and opens developer options (highlighting `toggle_adb_wireless`). The user opens "Pair device with pairing code" and types the code into the notification.
- `PairingReceiver` validates the 6 digits and schedules job 2: find our own `_adb-tls-pairing._tcp` port via NSD, `POST /api/devices/{name}/pair {port, code}`; the PC runs `IAdbDeviceResolver.PairAtAsync` (new: explicit port, no mDNS) with a 30s timeout and returns 422 with adb's message on failure. On success the phone reports its connect port.
- Needs the phone to have been provisioned once (it needs the PC URL/token/fingerprint), so it covers re-pairing after the 7-day key revocation or a cleared key, not a brand-new phone with no adb at all.
- Verified on the S23: a wrong code returned "Wrong password or connection was dropped"; the retry paired ("Paired with 'Mitch S23+'").

Known flaky test (pre-existing, unrelated): `SyncJobRunnerTests.RunAsync_FileFromOneDevicePushedToTwoOtherDevices_ReportsUniqueFileCountNotPerDeviceSum` failed once in a full run and passed on 4 reruns.

## Open questions

- Device model and Android version to target first.
- Whether percent progress is wanted (needs the transfer-engine hook) or phase-level status is enough initially.
- Where the Android project lives (new `AdbSync.Android/` folder in this repo vs. separate repo). Default: in this repo, outside `AdbSync.slnx`, with its own Gradle build.
- Shared secret provisioning: pass via the USB `pm grant` setup step, or enter manually.

## App gate: warning before opening an out-of-sync app

A job's `AppPackage` already blocks syncs while the app is open. The gate adds the other direction: warn before the app is opened on a device that is behind.

- **Staleness** (`SyncStaleness`): a device is behind when another device has changes the PC hasn't synced yet (`ChangeDetected`, cleared by a real run completing), when the app is open on another device and blocking the sync (`JobBlockedByApp`), or when an interrupted run's checkpoint hasn't reached it. Nothing is reported while a sync is actively transferring. It is derived from `JobStatusTracker` plus the checkpoint, so it is lost on restart except for the checkpoint part.
- **PC**: the dashboard shows an out-of-sync icon on the job (tooltip says why). The overflow menu has "Open App on Device" (warns first if that device is behind) and "Close App and Sync" (force-stops the app everywhere it's running, then syncs; unsaved work is lost).
- **API**: `/api/status` and the SSE feed carry `appPackage` and `staleDevices`. `POST /api/jobs/{name}/close-and-sync` `{ "device": ... }` force-stops the app on that device, runs the job, and returns when it finishes.
- **Phone**: `AppGateService` is an accessibility service that only reads which package is in front. When a linked app comes to the foreground it asks the PC; if this device is behind it draws an overlay offering Sync first / Open anyway / Not now. Android can't stop another app opening, so this is a warning over the app, not a block. The service is enabled from the Setup card through `WRITE_SECURE_SETTINGS` (falls back to the accessibility settings screen). Fails open if the PC is unreachable.
