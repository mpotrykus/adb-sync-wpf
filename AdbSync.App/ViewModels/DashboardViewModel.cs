using System.Collections.ObjectModel;
using System.Windows;
using AdbSync.Core.Config;
using AdbSync.Core.Orchestration;
using AdbSync.Core.Scheduling;

namespace AdbSync.App.ViewModels;

/// <summary>
/// Backs the dashboard's live job list and doubles as the app's <see cref="ISyncEventSink"/> - phase/outcome
/// events arrive on whatever thread the runner is executing on, so every mutation is marshaled to the UI thread.
/// </summary>
public sealed class DashboardViewModel : ISyncEventSink
{
    public ObservableCollection<JobStatusViewModel> Jobs { get; } = [];

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
                var nextRun = ScheduleCalculator.NextDueUtc(job.Schedule, now);

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
            vm.PhaseText = deviceName is null ? phase.ToString() : $"{phase} @ {deviceName}";
            if (phase == SyncPhase.PreConnect)
                vm.ConflictCountThisRun = 0; // start of a fresh run - reset the counter from any previous run
        });

    public void JobSkipped(string jobName, string reason) =>
        UpdateJob(jobName, vm =>
        {
            vm.PhaseText = "Idle";
            vm.LastOutcome = $"Skipped: {reason}";
        });

    public void JobCompleted(string jobName, bool pushed) =>
        UpdateJob(jobName, vm =>
        {
            vm.PhaseText = "Idle";
            var outcome = pushed ? "Success" : "No changes";
            vm.LastOutcome = vm.ConflictCountThisRun > 0 ? $"{outcome} ({vm.ConflictCountThisRun} conflict(s) resolved)" : outcome;
            vm.LastRunAt = DateTimeOffset.Now;
        });

    public void JobFailed(string jobName, Exception exception) =>
        UpdateJob(jobName, vm =>
        {
            vm.PhaseText = "Idle";
            vm.LastOutcome = $"Error: {exception.Message}";
            vm.LastRunAt = DateTimeOffset.Now;
        });

    public void MergeConflictsDetected(string jobName, string deviceName, int conflictCount) =>
        UpdateJob(jobName, vm => vm.ConflictCountThisRun += conflictCount);

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

    // Application.Current.Dispatcher is always the UI thread's dispatcher, regardless of which thread
    // constructs/resolves this singleton (a background hosted service may resolve it first via DI).
    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }
}
