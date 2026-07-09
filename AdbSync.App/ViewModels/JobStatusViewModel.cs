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
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private string _phaseText = "Idle";

    [ObservableProperty]
    private string? _lastOutcome;

    /// <summary>True when the last run failed and hasn't been superseded by a later success - drives the row's attention badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private bool _needsAttention;

    /// <summary>True while a sync is actively in progress for this job - drives the row's running shimmer.</summary>
    public bool IsRunning => PhaseText != "Idle";

    /// <summary>True when the row should draw attention to itself (running or needing attention) - drives the row's shimmer.</summary>
    public bool IsActive => IsRunning || NeedsAttention;

    /// <summary>True when the failure was a <see cref="AdbSync.Core.Orchestration.PushSafetyException"/>, which can be resolved via the Force Push action.</summary>
    [ObservableProperty]
    private bool _canForcePush;

    [ObservableProperty]
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

    private static string FormatRelative(DateTimeOffset t)
    {
        var elapsed = DateTimeOffset.Now - t;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero; // guard against clock skew making this negative

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
