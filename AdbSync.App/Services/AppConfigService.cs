using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;

namespace AdbSync.App.Services;

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
