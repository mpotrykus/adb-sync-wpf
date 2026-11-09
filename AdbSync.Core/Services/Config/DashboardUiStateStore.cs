using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Common;

namespace AdbSync.Core.Services.Config;

/// <summary>Loads/saves <see cref="DashboardUiState"/> to its own file, separate from the synced job/device
/// config, so it can be written on every sort click without triggering <see cref="AppConfigService.ConfigChanged"/>.</summary>
public sealed class DashboardUiStateStore(AppPaths paths)
{
    public async Task<DashboardUiState> LoadAsync(CancellationToken ct = default) =>
        await JsonFileIo.ReadAsync(paths.LocalStateFile, AppConfigJsonContext.Default.DashboardUiState, ct)
            ?? new DashboardUiState();

    public event Action? Saved;

    public async Task SaveAsync(DashboardUiState state, CancellationToken ct = default)
    {
        await JsonFileIo.WriteAtomicAsync(paths.LocalStateFile, state, AppConfigJsonContext.Default.DashboardUiState, ct);
        Saved?.Invoke();
    }
}
