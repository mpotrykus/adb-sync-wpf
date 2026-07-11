# AdbSync — Job Run Call Graph

What code a job hits when it runs, from any of the four trigger points down through the sync engine to the tray notification.

```
                    ┌──────────────────────────┐  ┌───────────────────────────┐
                    │ SchedulerHostedService   │  │ ChangeWatchHostedService  │
                    │  .ExecuteAsync (1m tick) │  │  .ReconcileAsync          │
                    └────────────┬─────────────┘  └─────────────┬─────────────┘
                                 │                              │ starts
                                 │                              ▼
                                 │                 ┌─────────────────────────────┐
                                 │                 │ ChangeWatchCoordinator      │
                                 │                 │  .Start → RunBindingLoop    │
                                 │                 │  → RunLiveLoop/RunPollLoop  │
                                 │                 │  → change! → SignalChange   │
                                 │                 │    (debounce) → onTriggered │
                                 │                 └────────────┬────────────────┘
                                 │                              │
     ┌─────────────────────────┐ │                              │
     │ DashboardWindow.xaml.cs │ │                              │
     │  RunNow_Click /         │ │                              │
     │  ForcePush_Click        │ │                              │
     └───────────┬─────────────┘ │                              │
                 │               │                              │
     ┌─────────────────────────┐ │                              │
     │ TrayIconService         │ │                              │
     │  "Run All Now"/"Run Job"│ │                              │
     └───────────┬─────────────┘ │                              │
                 │               │                              │
                 ▼               ▼                              ▼
           ┌──────────────────────────────────────────────────────────┐
           │            JobRunService.RunJobAsync(index, ...)         │
           │  (SemaphoreSlim gate → 1 run at a time)                  │
           │  configService.GetAsync/SaveAsync (LastRunAt/Success)    │
           └───────────────────────────┬──────────────────────────────┘
                                       │
                                       ▼
           ┌──────────────────────────────────────────────────────────┐
           │        SyncJobRunner.RunAsync(job, devices, ...)         │  (AdbSync.Core)
           │                                                          │
           │  1. lockManager.TryAcquireAsync ─────── ISyncLockManager │
           │  2. events.PhaseChanged(PreConnect)                      │
           │  3. deviceResolver.EnsureConnectedAsync (per device) ──  │
           │        └─ IAdbDeviceResolver → AdbDeviceResolver         │
           │  4. appGuard.IsRunningAnywhereAsync ── IAppRunningGuard  │
           │                                                          │
           │  5. RunPullPhaseAsync (per device) ─┐                    │
           │     events.PhaseChanged(Pull)       │                    │
           │     transfer.PullMirrorAsync        │  IAdbTransferEngine│
           │        → NativeAdbTransferEngine ───┤   (device→staging) │
           │           scan local + remote trees │                    │
           │           differ.Diff (IMirrorDiffer)│                   │
           │     pushSafety.RecordDeviceSnapshot │  IPushSafetyGuard  │
           │     manifests.GetOrBootstrapAsync   │  IManifestStore    │
           │     merge.MergeAsync ───────────────┤  ITwoWayMergeEngine│
           │        (staging vs master vs        │  TwoWayMergeEngine │
           │         baseline manifest)          │                    │
           │     manifests.SaveAsync             │                    │
           │     events.MergeConflictsDetected?  │                    │
           │     checkpoints.SaveAsync ──────────┘  ICheckpointManager│
           │                                                          │
           │  6. no changes? → JobCompleted(false) → return           │
           │                                                          │
           │  7. pushSafety.AssertSafeToPushAsync/ForcePushAsync      │
           │                                                          │
           │  8. RunPushPhaseAsync (per device) ─┐                    │
           │     events.PhaseChanged(Push)       │                    │
           │     transfer.PushMirrorAsync ───────┤  IAdbTransferEngine│
           │        → NativeAdbTransferEngine    │   (master→device)  │
           │     checkpoints.SaveAsync ──────────┘                    │
           │                                                          │
           │  9. events.JobCompleted(true) / JobFailed(ex) on catch   │
           │ 10. FinishAsync → runHistory.SaveRunAsync ── IRunHistoryStore
           └───────────────────────────┬──────────────────────────────┘
                                       │  events fire throughout (ISyncEventSink)
                                       ▼
           ┌──────────────────────────────────────────────────────────┐
           │   DashboardViewModel (the ISyncEventSink singleton)      │
           │   updates JobStatusViewModel: PhaseText, LastOutcome,    │
           │   ConflictCountThisRun, WatchStatusText                  │
           │   → ReportOutcome() raises OutcomeReported               │
           └───────────────────────────┬──────────────────────────────┘
                                       │
                                       ▼
           ┌──────────────────────────────────────────────────────────┐
           │  TrayIconService: OutcomeReported handler                │
           │   → ShowOutcomeNotificationAsync (Windows tray toast)    │
           └──────────────────────────────────────────────────────────┘
```

Four trigger paths (scheduler tick, live change-watch, manual "Run Now"/"Force Push" in the dashboard, tray menu) all funnel into `JobRunService.RunJobAsync`, which serializes runs and delegates to `SyncJobRunner.RunAsync` in `AdbSync.Core` — the pull→merge→push-safety→push pipeline. Events fire back up to `DashboardViewModel` for live UI updates and finally to `TrayIconService` for the completion toast.

Note: `SyncOrchestrator.cs` exists as a second entry point but isn't wired into the WPF app's DI — likely used only by a CLI/test harness (worth confirming against `AdbSync.Cli` if needed).
