using CommunityToolkit.Mvvm.ComponentModel;

namespace AdbSync.App.ViewModels;

public sealed partial class JobStatusViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextRunText))]
    private bool _enabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(DisplayStatusText))]
    private string _phaseText = "Idle";

    /// <summary>This run's current (prefix, suffix) per device, e.g. ("Pull @ ", "") - since a job's devices
    /// run concurrently, more than one can be active at once, but never more than one phase at a time per
    /// device. <see cref="PhaseText"/> groups devices that share the same prefix/suffix (i.e. the same phase)
    /// so two devices both mid-pull read as one clause - "Pull @ A and B" - instead of "Pull @ A, Pull @ B".</summary>
    private readonly Dictionary<string, (string Prefix, string Suffix)> _devicePhases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets this run's current phase for one device - <paramref name="prefix"/> and
    /// <paramref name="suffix"/> sandwich where the device name(s) go, e.g. ("Pull @ ", "") or
    /// ("Waiting for ", " (in use by another job)") - and recomputes <see cref="PhaseText"/> by grouping every
    /// device on file that shares the same prefix/suffix into one clause.</summary>
    public void SetDevicePhase(string deviceName, string prefix, string suffix = "")
    {
        _devicePhases[deviceName] = (prefix, suffix);
        PhaseText = string.Join(", ",
            _devicePhases
                .GroupBy(kv => kv.Value, kv => kv.Key)
                .OrderBy(g => g.Key.Prefix, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key.Prefix}{JoinNaturally(g.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}{g.Key.Suffix}"));
    }

    /// <summary>Drops all per-device phase text - called at the start of a new run (so a device this run hasn't
    /// touched yet doesn't show stale text from the last run) and whenever the job goes idle.</summary>
    public void ClearDevicePhases() => _devicePhases.Clear();

    private static string JoinNaturally(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count switch
        {
            <= 1 => list.Count == 1 ? list[0] : "",
            2 => $"{list[0]} and {list[1]}",
            _ => $"{string.Join(", ", list.Take(list.Count - 1))}, and {list[^1]}",
        };
    }

    [ObservableProperty]
    private string? _lastOutcome;

    /// <summary>True when the last run failed and hasn't been superseded by a later success - drives the row's attention badge.</summary>
    [ObservableProperty]
    private bool _needsAttention;

    /// <summary>True while a sync is actively in progress for this job - drives the row's running shimmer.</summary>
    public bool IsRunning => PhaseText != "Idle";

    /// <summary>True from the moment Stop is clicked until the run actually finishes - disables the Stop button
    /// so a second click can't fire while the current file/device is still finishing up.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatusText))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    private bool _isStopping;

    public bool CanStop => !IsStopping;

    /// <summary>True when this job has a saved checkpoint from an interrupted run (crash, shutdown, or a
    /// deliberate Stop) - the next "Run Now" will resume from it instead of starting over.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatusText))]
    private bool _hasCheckpoint;

    /// <summary>Human-readable description of the saved checkpoint, e.g. "Interrupted during Push @ Pixel7 -
    /// saved 2 hours ago" - shown as the checkpoint badge's tooltip.</summary>
    [ObservableProperty]
    private string? _checkpointSummary;

    /// <summary>The single line shown in the STATUS column - "Stopping..." once a stop has been requested,
    /// otherwise the active phase while a sync is running, otherwise the watch status (e.g. "Watching (live)")
    /// in place of "Idle" when the job's watcher is active, with a "resume available" note appended while a
    /// checkpoint is on file.</summary>
    public string DisplayStatusText
    {
        get
        {
            var baseText = IsStopping ? "Stopping..." : IsRunning ? PhaseText : (WatchStatusText ?? PhaseText);
            return !IsRunning && !IsStopping && HasCheckpoint ? $"{baseText} - resume available" : baseText;
        }
    }

    /// <summary>True when the failure was a <see cref="AdbSync.Core.Orchestration.PushSafetyException"/>, which can be resolved via the Force Push action.</summary>
    [ObservableProperty]
    private bool _canForcePush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatusText))]
    private string? _watchStatusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastRunText))]
    private DateTimeOffset? _lastRunAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextRunText))]
    private DateTimeOffset? _nextRunAt;

    /// <summary>Accumulates across the current run's devices; reset to 0 at the start of each new run.</summary>
    public int ConflictCountThisRun { get; set; }

    /// <summary>Raised only when a run just finished live, as opposed to <see cref="LastOutcome"/> also being set
    /// when the dashboard hydrates it from persisted history on load - lets the tray notify on the former only.</summary>
    public event Action<JobStatusViewModel>? OutcomeReported;

    public void ReportOutcome(string outcome)
    {
        LastOutcome = outcome;
        OutcomeReported?.Invoke(this);
    }

    public string LastRunText => LastRunAt is { } t ? FormatRelative(t) : "Never";

    /// <summary>Re-raises <see cref="LastRunText"/> change notification so the dashboard's ticking clock can
    /// advance "n seconds/minutes ago" text even though <see cref="LastRunAt"/> itself hasn't changed.</summary>
    public void RefreshLastRunText() => OnPropertyChanged(nameof(LastRunText));

    private static readonly TimeSpan RelativeTimeCutoff = TimeSpan.FromHours(24);

    /// <summary>Shared with DashboardWindow's checkpoint-summary text so both use the same "n minutes/hours
    /// ago" phrasing as LAST RUN.</summary>
    internal static string FormatRelative(DateTimeOffset t)
    {
        var elapsed = DateTimeOffset.Now - t;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        return elapsed switch
        {
            { TotalSeconds: < 60 } e => Pluralize(Math.Max(1, (int)e.TotalSeconds), "second"),
            { TotalMinutes: < 60 } e => Pluralize((int)e.TotalMinutes, "minute"),
            { } e when e < RelativeTimeCutoff => Pluralize((int)e.TotalHours, "hour"),
            _ => t.LocalDateTime.ToString("g"),
        };
    }

    private static string Pluralize(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")} ago";

    public string NextRunText => Enabled
        ? NextRunAt switch
        {
            null => "-",
            { } t when t <= DateTimeOffset.Now => "Due now",
            { } t => t.LocalDateTime.ToString("g"),
        }
        : "-";
}
