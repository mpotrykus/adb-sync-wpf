using AdbSync.Core.Models.Devices;
using AdbSync.Core.Services.Devices;

namespace AdbSync.Core.Tests.Orchestration.Fakes;

public sealed class FakeAppRunningGuard(bool isRunning = false) : IAppRunningGuard
{
    public Task<string?> FindRunningSerialAsync(string appPackage, IEnumerable<string> deviceSerials, CancellationToken ct = default) =>
        Task.FromResult(isRunning ? deviceSerials.FirstOrDefault() : null);

    public Task WaitUntilStoppedAsync(string appPackage, string serial, CancellationToken ct = default) =>
        Task.CompletedTask;
}
