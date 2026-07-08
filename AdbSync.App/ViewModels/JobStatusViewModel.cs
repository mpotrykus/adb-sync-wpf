using CommunityToolkit.Mvvm.ComponentModel;

namespace AdbSync.App.ViewModels;

public sealed partial class JobStatusViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextRunText))]
    private bool _enabled = true;

    [ObservableProperty]
    private string _phaseText = "Idle";

    [ObservableProperty]
    private string? _lastOutcome;

    /// <summary>True when the last run failed and hasn't been superseded by a later success - drives the row's attention badge.</summary>
    [ObservableProperty]
    private bool _needsAttention;

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

    public string LastRunText => LastRunAt is { } t ? t.LocalDateTime.ToString("g") : "Never";

    public string NextRunText => Enabled
        ? NextRunAt switch
        {
            null => "-",
            { } t when t <= DateTimeOffset.Now => "Due now",
            { } t => t.LocalDateTime.ToString("g"),
        }
        : "-";
}
