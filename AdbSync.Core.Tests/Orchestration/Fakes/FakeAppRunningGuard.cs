using AdbSync.Core.Devices;

namespace AdbSync.Core.Tests.Orchestration.Fakes;

public sealed class FakeAppRunningGuard(bool isRunning = false) : IAppRunningGuard
{
    public Task<bool> IsRunningAnywhereAsync(string appPackage, IEnumerable<string> deviceSerials, CancellationToken ct = default) =>
        Task.FromResult(isRunning);
}
