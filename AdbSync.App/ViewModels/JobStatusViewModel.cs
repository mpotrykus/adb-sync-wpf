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
