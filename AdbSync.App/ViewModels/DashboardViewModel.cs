using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Scheduling;
using System.Collections.ObjectModel;
using System.Windows;

namespace AdbSync.App.ViewModels;

/// <summary>
/// Backs the dashboard's live job list and doubles as the app's <see cref="ISyncEventSink"/> - phase/outcome
/// events arrive on whatever thread the runner is executing on, so every mutation is marshaled to the UI thread.
/// </summary>
public sealed class DashboardViewModel : ISyncEventSink
{
    public ObservableCollection<JobStatusViewModel> Jobs { get; } = [];

    public bool AnyJobRunning => Jobs.Any(j => j.IsRunning);

    /// <summary>Warning shown before exiting the app while a job is running - notes the push-specific risk
    /// (partially-updated devices) only when it actually applies, same condition StopNow_Click checks.</summary>
    public string ExitWarningMessage
    {
        get
        {
            var pushNote = Jobs.Any(j => j.IsRunning && j.PhaseText.Contains("Push", StringComparison.Ordinal))
                ? " A job that's mid-push may leave some devices updated and others not until it runs again."
                : "";
            return $"One or more jobs are still running.\n\n{pushNote} Exiting now will stop them.\n\nEach resumes from its last checkpoint the next time it runs.";
        }
    }

    public void SyncFrom(AppConfig config)
    {
        RunOnUi(() =>
        {
            var now = DateTimeOffset.Now;
            var existingByName = Jobs.ToDictionary(j => j.Name);
            var seen = new HashSet<string>();

            foreach (var job in config.Jobs)
            {
                seen.Add(job.Name);
                var nextRun = ScheduleCalculator.NextDueUtc(job.Schedule, now, config.Settings.RunMissedSchedules);

                if (existingByName.TryGetValue(job.Name, out var vm))
                {
                    vm.Enabled = job.Enabled;
                    vm.LastRunAt = job.Schedule.LastRunAt;
                    vm.NextRunAt = job.Enabled ? nextRun : null;
                }
                else
                {
                    Jobs.Add(new JobStatusViewModel(job.Name)
                    {
                        Enabled = job.Enabled,
                        LastRunAt = job.Schedule.LastRunAt,
                        NextRunAt = job.Enabled ? nextRun : null,
                    });
                }
            }

            foreach (var stale in Jobs.Where(j => !seen.Contains(j.Name)).ToList())
                Jobs.Remove(stale);
        });
    }

    public void PhaseChanged(string jobName, SyncPhase phase, string? deviceName = null) =>
        UpdateJob(jobName, vm =>
        {
            if (deviceName is null)
            {
                vm.ClearDevicePhases();
                vm.PhaseText = phase.ToString();
            }
            else
            {
                var (prefix, suffix) = phase switch
                {
                    SyncPhase.WaitingForAppClose => ("Waiting for app to close on ", ""),
                    SyncPhase.WaitingForDevice => ("Waiting for ", " (in use by another job)"),
                    _ => ($"{phase} @ ", ""),
                };
                vm.SetDevicePhase(deviceName, prefix, suffix);
            }

            if (phase == SyncPhase.PreConnect)
                vm.ConflictCountThisRun = 0;
        });

    public void JobQueued(string jobName, string reason) =>
        UpdateJob(jobName, vm => vm.PhaseText = reason);

    public void JobSkipped(string jobName, string reason) =>
        UpdateJob(jobName, vm =>
        {
            vm.ClearDevicePhases();
            vm.PhaseText = "Idle";
            vm.IsStopping = false;
            vm.ReportOutcome($"Skipped: {reason}");
        });

    public void JobCompleted(string jobName, bool pushed) =>
        UpdateJob(jobName, vm =>
        {
            vm.ClearDevicePhases();
            vm.PhaseText = "Idle";
            vm.IsStopping = false;
            var outcome = pushed ? "Success" : "No changes";
            vm.ReportOutcome(vm.ConflictCountThisRun > 0 ? $"{outcome} ({vm.ConflictCountThisRun} conflict(s) resolved)" : outcome);
            vm.LastRunAt = DateTimeOffset.Now;
            vm.NeedsAttention = false;
            vm.CanForcePush = false;
        });

    public void JobFailed(string jobName, Exception exception) =>
        UpdateJob(jobName, vm =>
        {
            vm.ClearDevicePhases();
            vm.PhaseText = "Idle";
            vm.IsStopping = false;
            vm.ReportOutcome($"Error: {exception.Message}");
            vm.LastRunAt = DateTimeOffset.Now;
            vm.NeedsAttention = true;
            vm.CanForcePush = exception is PushSafetyException;
        });

    public void JobCancelled(string jobName) =>
        UpdateJob(jobName, vm =>
        {
            vm.ClearDevicePhases();
            vm.PhaseText = "Idle";
            vm.IsStopping = false;
            vm.ReportOutcome("Stopped");
            vm.LastRunAt = DateTimeOffset.Now;
        });

    public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) =>
        UpdateJob(jobName, vm => vm.ConflictCountThisRun += conflictCount);

    public void WatchStarted(string jobName, string deviceName, bool liveWatch) =>
        UpdateJob(jobName, vm => vm.WatchStatusText = liveWatch ? "Watching (live)" : "Watching (polling)");

    public void WatchDegraded(string jobName, string deviceName, string reason) =>
        UpdateJob(jobName, vm => vm.WatchStatusText = $"Watching (polling) - {reason}");

    public void WatchStopped(string jobName, string deviceName) =>
        UpdateJob(jobName, vm => vm.WatchStatusText = null);

    public void ChangeDetected(string jobName, string deviceName) { }

    private void UpdateJob(string jobName, Action<JobStatusViewModel> apply) =>
        RunOnUi(() =>
        {
            var vm = Jobs.FirstOrDefault(j => j.Name == jobName);
            if (vm is null)
            {
                vm = new JobStatusViewModel(jobName);
                Jobs.Add(vm);
            }
            apply(vm);
        });

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }
}
