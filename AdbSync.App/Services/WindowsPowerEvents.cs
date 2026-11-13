using AdbSync.Engine.Services;
using Microsoft.Win32;

namespace AdbSync.App.Services;

public sealed class WindowsPowerEvents : IHostPowerEvents, IDisposable
{
    public event EventHandler? Suspending;
    public event EventHandler? Resumed;
    public event EventHandler<string>? SessionEnding;

    public WindowsPowerEvents()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            Suspending?.Invoke(this, EventArgs.Empty);
        else if (e.Mode == PowerModes.Resume)
            Resumed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e) => SessionEnding?.Invoke(this, e.Reason.ToString());

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
    }
}
