# AdbSync

AdbSync keeps folders in sync between Android devices and a Windows PC over ADB — including keeping **multiple Android devices in sync with each other**, mediated by a local "master" copy. It ships as a WPF tray application (scheduled, background sync) and a CLI (scripted/one-off sync).

## Solution layout

| Project | Purpose |
|---|---|
| `AdbSync.Core` | The sync engine. Config model, device resolution, ADB transfer, diffing/merging, orchestration, scheduling, logging. No UI dependencies. |
| `AdbSync.Cli` | Console entry point (`adbsync ...`). Thin wrapper around `AdbSync.Core`, no DI container. |
| `AdbSync.App` | WPF tray application. Runs continuously, schedules jobs, and provides Dashboard / Device / Job / Settings windows. |
| `AdbSync.Core.Tests` | Unit tests for config, devices, merge, orchestration, scheduling, transfer. |
| `AdbSync.Core.IntegrationTests` | Integration tests exercising `AdbSyncRemoteFileSystem` against a real ADB device. |

> **Naming note:** internally a sync target is called a **job** (`SyncJobConfig`), but the on-disk config file and parts of the UI call it a **project** (`config/projects.json`, `GlobalSettings.ProjectsDirectory`). They're the same thing.

## Topology: hub-and-spoke through a local master

AdbSync never syncs two Android devices directly. Every job has a **master folder** on the PC at:

```
<ProjectsDirectory>\<jobName>\master
```

(default `ProjectsDirectory` is `Documents\AdbSync Projects`). A job binds one or more devices, each with its own remote path on that device. Every run:

1. **Pulls** each bound device down into the master (merging, not overwriting).
2. **Pushes** the resulting master back out to every bound device.

With a single device this is effectively "keep this folder in sync with this device." With multiple devices bound to the same job, the master acts as the mediator, so device A's changes flow to master, then out to device B, and vice versa on the next run.

## Sync pipeline (per job)

Implemented in `AdbSync.Core/Orchestration/SyncJobRunner.cs`, run by `SyncOrchestrator` (jobs run **strictly sequentially**; one job's failure doesn't block the others):

1. **Lock** — a cooperative file lock at `<projectRoot>\.sync_staging\.sync_lock`. If a live process already holds it, the job is skipped. Stale locks (owning PID dead, or older than `StaleLockHours`, default 4h) are reclaimed and the staging directory is wiped.
2. **PreConnect** — resolve/connect every bound device via ADB (see below).
3. **App-running guard** — if the job specifies an `AppPackage`, AdbSync checks (`pidof <package>` over `adb shell`) whether that app is running on *any* bound device. If so, the whole job is skipped, to avoid syncing files an app currently has open.
4. **Pull phase** — for each bound device, sequentially:
   - Mirror the device's remote folder down into a per-device staging directory (`.sync_staging\<deviceName>`).
   - Load (or bootstrap) that device's sync manifest — its last-known-synced baseline.
   - **Three-way merge** the freshly-pulled staging content against the master, using the manifest as the common ancestor (see below).
   - Save the updated manifest, delete the staging directory, and write a resumable checkpoint.
5. If no device produced any change, the job finishes as `CompletedNoChanges` — no push phase runs at all.
6. **Push safety check** — before pushing anything back out, AdbSync refuses to push if the master is empty, or if the master's file count has dropped below 25% of the historical maximum ever seen for that job. This guards against propagating a mass-deletion (typo'd remote path, unmounted SD card, merge bug) out to every device.
7. **Push phase** — mirror the (merged) master out to every bound device, sequentially, checkpointing after each.
8. Any exception is caught per-job and recorded as `Failed`; it doesn't stop other jobs in the run.

### Crash resume

After every device/phase transition, a checkpoint (`.sync_checkpoint.json`: job index, phase, device index, resolved serials) is saved. The next `adbsync run` (no job name) picks it up and resumes from where it left off instead of starting over. Running a specific job by name (`adbsync run <jobName>`) always ignores the checkpoint. The GUI's scheduled/manual runs currently do not use checkpoint-resume.

## How files are compared

AdbSync compares files by **size and modified-time**, not content hashes. Two files are considered identical if their sizes match and their modified timestamps agree within a **2-second tolerance** — this absorbs the mtime-precision differences between NTFS, ext4/FAT, and `adb push`/`pull` rounding.

## One-way mirroring (device ⇄ staging, master ⇄ device)

Both the pull leg (device → staging) and the push leg (master → device) are one-way mirror operations:

- **Copy**: any source entry missing from the destination, or present but different (size/mtime).
- **Delete**: any destination entry not present in the source. Deletions are collapsed to the topmost doomed ancestor, so a removed directory's entire subtree is deleted as one unit.
- Copies are atomic: written to a `.tmp-<guid>` sibling, timestamp-matched to the source, then renamed into place.

## Two-way merge (staging ⇄ master)

This is the actual bidirectional sync logic, implemented in `AdbSync.Core/Merge/TwoWayMergeEngine.cs` — a three-way merge using the stored **manifest** as the common ancestor between staging (`s`, freshly pulled from the device) and master (`m`). It is file-only: directories are never deleted here (an empty directory husk left behind is a deliberate tradeoff to avoid delete-vs-recreate race ordering bugs).

For every relative path across the union of staging, master, and the manifest baseline:

| Staging | Master | Baseline | Result |
|---|---|---|---|
| new | — | — | copy to master |
| — | new | — | copy to staging |
| new | new | — | **conflict** — newer `ModifiedUtc` wins |
| present, unchanged | deleted | had baseline | delete propagates to staging |
| present, edited | deleted | had baseline | **conflict** — edit wins |
| deleted | present, unchanged | had baseline | delete propagates to master |
| deleted | present, edited | had baseline | **conflict** — edit wins |
| deleted | deleted | had baseline | dropped from manifest, no-op |
| unchanged both sides | | present | no-op |
| changed one side only | | present | copy the changed side over |
| changed both sides | | present | **conflict** — newer `ModifiedUtc` wins |

**Conflict resolution is last-write-wins by modification time.** The losing file's content is backed up first (default on) to a `.conflicts` folder as `<filename>.<UTC timestamp>.conflict`, so a conflict never silently destroys data. Conflicts are counted and reported ("N conflict(s) resolved") through both the CLI and the Dashboard.

**First sync for a device**: if no manifest exists yet, one is bootstrapped from whatever staging and master files already agree on (same size+mtime) — so a first-time sync doesn't manufacture spurious conflicts out of files that already happen to match; files that differ are treated as newly created on one side.

## Exclusions

Each job has an `Exclude` list, matched identically during pull and push:

- A bare name with no `/` (e.g. `Cache`) excludes anything with that name **at any depth**.
- A path-shaped pattern with a `/` (e.g. `Painter/Cache`) is anchored to the sync root and excludes that path and everything under it.

There's no glob support (`*`, `?`) — matching is literal path-segment/path comparison. Excluded directories are never even listed/recursed into during the remote/local tree walk.

## ADB device handling

`AdbSync.Core/Devices/AdbDeviceResolver.cs` resolves a configured device to a live ADB serial/host:port before every job run:

1. Ensures the local `adb` server daemon is running.
2. **USB devices** (configured with a static `Serial`): used directly, no discovery — assumes it's already connected.
3. **WiFi devices** (configured with an `Ip`, using Android 11+ wireless debugging over mDNS):
   - Reuse a cached `host:port` from a previous run if that exact device still reports itself online.
   - Otherwise look for an already-connected `adb devices` entry whose serial starts with that IP.
   - Otherwise browse mDNS for `_adb-tls-connect._tcp` (5s timeout), match the configured IP against an announcement, `connect` to it, and verify it comes online.
   - Failure at every step raises a connection error and the job's PreConnect phase (and thus the whole job) fails.
   - A successful resolution is cached back onto the device's config and persisted.

This uses Android's wireless-debugging-over-mDNS pairing flow, not classic manual `adb tcpip`/IP:port pairing, and not MTP.

### Pairing a new WiFi device

A device has to be **paired** once before it can be connected to — connecting only works against a device that already trusts this PC's ADB key. AdbSync can do the pairing handshake itself: `AdbDeviceResolver.PairAsync` browses mDNS for `_adb-tls-pairing._tcp` (only advertised while the device's Settings → Developer options → Wireless debugging → "Pair device with pairing code" screen is open), matches it against the device's configured IP, and sends the 6-digit code shown on-device via `adb`'s `PairAsync`. Exposed as the "Pair..." button in the Device editor (WiFi devices only) and `adbsync device pair <deviceName> <code>` on the CLI. A `DevicePairException` is thrown if no pairing announcement is found or the code is rejected.

## Transfer engines

Two interchangeable implementations of the mirror/transfer step:

- **Native (default)** — talks the ADB sync protocol directly (no shelling to `adb.exe` for data transfer). Diffs per-file on **both** pull and push. Deletes and directory creation go through `adb shell` (the sync protocol itself has no delete verb). Pushed files carry the source file's mtime.
- **Legacy** (`--legacy-transfer` CLI flag) — shells out to `adb.exe`. Pull diffs locally against the existing mirror; **push is a brute-force full re-upload** of everything non-excluded (no push-side diffing) — kept only for compatibility with the earlier tool this replaces.

Neither engine has built-in retry/backoff. Per-file failures are captured into a result's error list rather than aborting the whole mirror operation, so one bad file doesn't sink the rest of the sync.

## Configuration

Config lives under `%LocalAppData%\AdbSync\config\` as three JSON files, composed by `AppConfigStore`:

- **`devices.json`** — `DeviceConfig`: `Name`, and either `Ip` (WiFi/mDNS) or `Serial` (static USB), plus a cached resolved connection.
- **`projects.json`** — `SyncJobConfig` per job: `Name` (also the master-folder name — locked after creation), optional `AppPackage` (skip sync while running), `Exclude` patterns, one or more `{ DeviceName, RemotePath }` bindings, `Enabled`, and a `Schedule` (`Manual`, `Interval` in hours from the previous run, or `DailyAt` a list of local times).
- **`settings.json`** — `GlobalSettings`: `ProjectsDirectory`, `StartAtLogin`, notification toggles, `StaleLockHours`, `LogRetentionDays`, `ConflictRetentionDays`, and `MaxConcurrentJobs` (currently unused — job execution is serialized regardless of this value).

A legacy importer (`adbsync config import <devices.json> <projects.json>`) converts an older tool's config files into this format.

## CLI usage

```
adbsync config import <legacyDevices.json> <legacyProjects.json>   # import from the legacy tool's config
adbsync device test <deviceName>                                   # resolve/connect to a device and print the result
adbsync run                                                         # run every enabled job, resuming a checkpoint if one exists
adbsync run --legacy-transfer                                       # same, using the legacy adb.exe-based transfer engine
adbsync run <jobName>                                                # run one job by name, ignoring any pending checkpoint
adbsync run <jobName> --legacy-transfer
```

The CLI prints one line per job (`name: Outcome[ - error]`) and exits with code `1` if any job failed.

## The tray application

`AdbSync.App` runs as a background tray app (single instance, enforced via a named mutex):

- **Scheduler** ticks once a minute, running any job whose schedule is due (or all jobs via "Run All Now" from the tray menu / Dashboard).
- **Dashboard** — job list with status, last/next run time, per-job "Run Now", and entry points to the Device, Job, and Settings editors. Closing it just hides it back to the tray.
- **Device editor** — add/edit/remove devices (WiFi IP or USB serial), "Test Connection" against a live device.
- **Job editor** — name, app package, exclude list, device bindings, enabled flag, and schedule.
- **Settings** — projects directory, start-at-login, notification toggles, stale-lock threshold, log retention.

## Logging

Serilog writes a daily rolling transcript to `%LocalAppData%\AdbSync\logs\`, retained for `LogRetentionDays` (default 30). Live progress (phase changes, skips, completions, failures, conflict counts) is reported through a shared event interface, surfaced as console lines in the CLI and as Dashboard/tray-tooltip updates in the App.
