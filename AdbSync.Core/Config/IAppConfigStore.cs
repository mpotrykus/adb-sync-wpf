namespace AdbSync.Core.Config;

public interface IAppConfigStore
{
    Task<AppConfig> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppConfig config, CancellationToken ct = default);
}
