# Local dev server

A throwaway server on the dev PC for working on the web UI and the server API without touching the Pi.
Script: `tools/dev-server.ps1` (PowerShell).

## Run it

```powershell
tools/dev-server.ps1 -Seed        # fresh copy of the Pi's data, then run (first time, or to refresh)
tools/dev-server.ps1              # run against the existing dev data
tools/dev-server.ps1 -Fresh       # empty data
tools/dev-server.ps1 -NoWatch     # dotnet run instead of dotnet watch
tools/dev-server.ps1 -NoHotReload # dotnet watch, but restart on every change
tools/dev-server.ps1 -NoBrowser   # don't open the browser
tools/dev-server.ps1 -WriteDelay 2 # seconds each simulated device write waits (default 10)
tools/dev-server.ps1 -AllowRuns   # turn the safety flags off, real device writes included (see below)
```

It builds, prints a one-time pairing code, starts the server on `https://localhost:47616` (accept the certificate
warning the first time) and opens the browser at `/signin?code=...`, which pairs the browser automatically. For
another code: `dotnet run --no-build --project AdbSync.Server -- pair` with the same `ADBSYNC_*` env vars the script
sets. Ctrl+C stops it.

- Data lives in `%LOCALAPPDATA%\AdbSync.Dev` (`data` and `projects`), separate from the desktop app's
  `%LOCALAPPDATA%\AdbSync`. The port (47616), server name and loopback-only listening are set by the script.
- `dotnet watch` runs with hot reload: edits are applied live where possible and the server restarts when they can't
  be. `-NoHotReload` restarts on every change instead. Refresh the browser if the page doesn't update.
- Mobile layout: Chrome DevTools device emulation at about 412px wide (S23+).

## What `-Seed` copies

Over ssh from `raspi-server:~/adbsync/data`, read-only on the Pi: databases through SQLite's backup API (consistent
while the Pi runs), config, manifests, checkpoints, push-safety, app icons. Left out: logs, the lock file, `adb-home`
(adb key), `companion-cert.pfx` (the dev server makes its own certificate), `json-backup`, and the rows in `Clients` /
`PairingCodes`, so no phone or browser token carries over. The project folders (the masters) are not copied.

The seeded settings still hold the Pi's Linux project paths, so job cards may report the project folder as
unavailable on Windows. That is expected.

## Feature flags

`AdbSync.Server/FeatureFlags.cs`, bound from the `Features` config section (appsettings or `Features__Name` env
vars). All default to on. `appsettings.Development.json` sets these, and the dev script runs in Development:

| Flag | Dev | Off means |
|---|---|---|
| `Scheduler` | off | scheduler service isn't registered |
| `ChangeWatch` | off | on-change watch service isn't registered |
| `CompanionBroadcast` | off | phone broadcast service isn't registered |
| `RunJobs` | on | `JobRunService` holds all new runs; runs, run-all, snapshot create/restore, conflict resolve, app open and close-and-sync return 403 `feature_disabled` |
| `DeviceWrites` | off | every device write is simulated (below) |
| `SimulatedWriteDelaySeconds` | 10 | how long each simulated write waits; `-WriteDelay` on the script |

The server logs a warning at startup when any flag is off. The Pi runs in Production, so it is unaffected.
`-AllowRuns` turns them all on, including real device writes; the adb server on this PC sees the real phones and the
seeded jobs point at them, so don't use it with a seed.

### Running jobs in dev

Manual runs work. Reads are real: the device is listed and files are pulled into the dev master. Anything that would
change a device is not done; it is logged (`Simulated device write (not performed): ...`) and replaced by a wait of
`SimulatedWriteDelaySeconds`. That covers push, delete, mkdir (the `IRemoteFileSystem` writes, which include rollback
restores and conflict resolution) and app force-stop / launch (`IDeviceAppControl`). The wait is per write, so a run
that pushes 50 files takes about 50 x 10 s; lower `-WriteDelay` for big runs.

Because the device never receives the writes, a second run sees the same differences again, and an earlier simulated
push can later look like a deletion on the device. Don't read run results across several runs as real behaviour; use
`-Seed` to reset.

On start in Development the server repoints the projects directory (and any job override that doesn't exist here) at
`%LOCALAPPDATA%\AdbSync.Dev\projects`, since the seed carries the Pi's Linux paths. The first run builds each master by
pulling from the devices; the stale-manifest guard discards the seeded manifests because the master starts empty.

## Test, then deploy

1. Make the change, check it on the dev server (`-Seed` first if the data is stale).
2. Run the tests: `dotnet test`.
3. Before deploying, check the Pi has nothing running: the Jobs page and Run history in the Pi's web UI. A deploy
   with `--recreate` restarts the container and stops any run in progress (this has happened once).
4. `tools/deploy-pi.sh --recreate` (add a host argument to override `raspi-server`). It publishes linux-arm64 here,
   ships the files, builds a thin image over `adbsync-server:base` and recreates the container. The image is tagged
   with the commit and `-dirty` when the server projects have uncommitted changes.
5. Confirm the script ends with `container: healthy`, then load the Pi's web UI and check the page you changed.

CSS and Razor changes are baked into the image, so even small CSS edits need a deploy; iterate on the dev server and
deploy once.

## Verified and not verified

Verified: the script builds and starts the server, pairs, seeds from the Pi, the server loads the 3 seeded jobs with no
clients, and run, run-all and snapshot-create return 403 with the flags off.
Also verified: a run of the Test job against the two real phones pulled files and simulated the mkdirs with the wait.
Not verified: the browser auto-open and auto-pair path, `dotnet watch` mode, and `-AllowRuns`.
