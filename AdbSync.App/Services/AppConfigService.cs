using AdbSync.Core.Config;

namespace AdbSync.App.Services;

/// <summary>
/// Single in-memory source of truth for config across the app: every window/viewmodel reads/mutates the same
/// <see cref="AppConfig"/> instance and calls <see cref="SaveAsync"/> to persist and notify other listeners.
/// </summary>
public sealed class AppConfigService(IAppConfigStore store)
{
    private AppConfig? _current;

    public event EventHandler? ConfigChanged;

    public async Task<AppConfig> GetAsync()
    {
        _current ??= await store.LoadAsync();
        return _current;
    }

    public async Task SaveAsync()
    {
        if (_current is not null)
            await store.SaveAsync(_current);
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }
}
