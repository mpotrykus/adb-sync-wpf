# Incremental pull

By default every run pulls each device's whole folder into `.sync_staging/{device}/`, merges, then deletes staging. With
**incremental pull** on, a run only transfers device files that differ from the last successful sync.

## Settings

| Setting | Where | Default |
|---|---|---|
| `incrementalPull` | Global settings, job override (`overrides.incrementalPull`: on / off / inherit) | off |
| `fullPullIntervalDays` | Global settings, job override | 7 (0 = every run is a full pull) |

Web UI: Settings page (switch + interval) and the job editor's "Pulling" section. API: `SettingsDto`, `JobOverridesDto`,
`GET /jobs/{id}/effective-settings`; see `docs/api/openapi.yaml`.

## Rules

- A device file is skipped only when the device copy **and** the master copy both match the manifest baseline from the last
  successful sync (same size, mtime within 2 s). Anything new, changed or unknown is pulled.
- The merge is unchanged. Skipped files reach it as synthetic staging entries (`MergeOptions.UnchangedOnDevice`); it never
  reads their content, and they classify as no-ops. Device and master deletions propagate exactly as before.
- A run is a **full pull** when: incremental is off; the run is a dry run; it resumes a checkpoint; the device has no
  manifest (first run, cleared, project root changed); or `SyncManifest.LastFullPullAtUtc` is missing or older than
  `fullPullIntervalDays`. Only full pulls refresh that stamp. The stamp is saved with the manifest, i.e. after a
  successful push.
- When nothing changed on any device and the merge produced nothing, an incremental run skips the push phase (full pulls
  always went on to push).

## Known limits

- An edit that keeps the same size and mtime is not noticed. The merge has the same blind spot, and the periodic full pull
  does not hash content either.
- Rollback points capture the device folder from staging, so for incremental runs `devices/{name}/` holds only the files that
  were pulled. `master/` in the point is still complete.
- Files whose mtime changed but whose content did not (Nomad Sculpt rewrites about half its folder when it saves) are still
  pulled, merged and pushed. A content check was considered and deferred: the pull cost is already paid, the push cost can't
  be fully avoided because adb has no mtime-only update, and it would change the "changed file is copied" rule the earlier
  data-loss fixes rely on. If real runs show large waste, a narrow version is: only one side changed, sizes equal, hashes
  equal, so update the baseline and skip the copy. Measure first (a run-log counter for content-identical copies is the
  suggested start).

## Where it lives

`IncrementalPullPlanner` (eligibility + skip predicate), `TransferPolicy.SkipPull` and `TransferResult.SkippedUnchanged`
(`NativeAdbTransferEngine` honours them; `AdbExeTransferEngine` always pulls everything), `MergeOptions.UnchangedOnDevice`,
`SyncManifest.LastFullPullAtUtc`, and the pull phase in `SyncJobRunner.RunPullPhaseAsync`.

## Tests

- `IncrementalPullTests.cs`: planner, and end-to-end runner cases (change/new/delete on device, master changes, two devices,
  every full-pull fallback).
- `IncrementalPullDifferentialTests.cs`: the same random edit history through a full-pull world and an incremental world,
  requiring identical master, device trees and (single device) manifests after every run. CI runs 3 seeds per test; set
  `ADBSYNC_DIFF_SEEDS=25` for the full sweep (~3.5 min). 55 scenarios passed.
- Real phones (2026-11-12, Nomad Sculpt, pushes blocked, scratch master): full pull 63-89 s for 146 files / 2.0 GB per phone,
  incremental with no changes 1 s; hash-identical to the phones; after a real edit on the S23+ only the 85 changed files were
  pulled and the merged master matched the phone. Harness: a throwaway console app using `AdbSync.Core` with a transfer
  wrapper whose push only scans and reports.
