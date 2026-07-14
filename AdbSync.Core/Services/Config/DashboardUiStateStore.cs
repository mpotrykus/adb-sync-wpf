using AdbSync.Core.Models.Config;

namespace AdbSync.Core.Services.Config;

/// <summary>Loads/saves <see cref="DashboardUiState"/> to its own file, separate from the synced job/device
/// config, so it can be written on every sort click without triggering <see cref="AppConfigService.ConfigChanged"/>.</summary>
public sealed class DashboardUiStateStore(AppPaths paths)
{
    public async Task<DashboardUiState> LoadAsync(CancellationToken ct = default) =>
        await JsonFileIo.ReadAsync(paths.LocalStateFile, AppConfigJsonContext.Default.DashboardUiState, ct)
            ?? new DashboardUiState();

    public Task SaveAsync(DashboardUiState state, CancellationToken ct = default) =>
        JsonFileIo.WriteAtomicAsync(paths.LocalStateFile, state, AppConfigJsonContext.Default.DashboardUiState, ct);
}
