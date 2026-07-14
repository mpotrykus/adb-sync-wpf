namespace AdbSync.Core.Models.Config;

/// <summary>Local-only UI state (e.g. the dashboard's chosen sort column) that isn't part of the synced
/// job/device config, so changing it doesn't raise <see cref="Services.Config.AppConfigService.ConfigChanged"/>
/// or show up in the Settings UI.</summary>
public sealed class DashboardUiState
{
    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }
}
