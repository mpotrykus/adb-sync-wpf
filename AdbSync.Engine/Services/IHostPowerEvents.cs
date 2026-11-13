namespace AdbSync.Engine.Services;

/// <summary>
/// The host's sleep, wake and logoff notifications, so long-lived adb connections can be closed before the machine
/// suspends. The Windows app raises them from SystemEvents; an always-on server has none.
/// </summary>
public interface IHostPowerEvents
{
    event EventHandler? Suspending;
    event EventHandler? Resumed;
    event EventHandler<string>? SessionEnding;
}

public sealed class NoHostPowerEvents : IHostPowerEvents
{
    public event EventHandler? Suspending { add { } remove { } }
    public event EventHandler? Resumed { add { } remove { } }
    public event EventHandler<string>? SessionEnding { add { } remove { } }
}
