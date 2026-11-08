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

## Open questions

- Device model and Android version to target first.
- Whether percent progress is wanted (needs the transfer-engine hook) or phase-level status is enough initially.
- Where the Android project lives (new `AdbSync.Android/` folder in this repo vs. separate repo). Default: in this repo, outside `AdbSync.slnx`, with its own Gradle build.
- Shared secret provisioning: pass via the USB `pm grant` setup step, or enter manually.
