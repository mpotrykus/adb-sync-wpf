using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Orchestration;

namespace AdbSync.Core.Tests.Orchestration;

public class PushSafetyGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _masterPath;
    private readonly PushSafetyGuard _guard;

    public PushSafetyGuardTests()
    {
        _masterPath = Path.Combine(_root, "master");
        Directory.CreateDirectory(_masterPath);
        _guard = new PushSafetyGuard(new AppPaths(Path.Combine(_root, "appdata")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void WriteMasterFiles(int count)
    {
        for (var i = 0; i < count; i++)
            File.WriteAllText(Path.Combine(_masterPath, $"file{i}.txt"), "x");
    }

    [Fact]
    public async Task AssertSafeToPushAsync_EmptyMaster_Throws()
    {
        await Assert.ThrowsAsync<PushSafetyException>(() => _guard.AssertSafeToPushAsync("Job1", _masterPath, minimumPercent: 25));
    }

    [Fact]
    public async Task AssertSafeToPushAsync_NoHistory_AllowsAnyNonEmptyMaster()
    {
        WriteMasterFiles(1);

        await _guard.AssertSafeToPushAsync("Job1", _masterPath, minimumPercent: 25);
    }

    [Fact]
    public async Task AssertSafeToPushAsync_AtOrAboveQuarterOfHistoricalMax_Allows()
    {
        await _guard.RecordDeviceSnapshotAsync("Job1", "DeviceA", 100);
        WriteMasterFiles(25);

        await _guard.AssertSafeToPushAsync("Job1", _masterPath, minimumPercent: 25);
    }

    [Fact]
    public async Task AssertSafeToPushAsync_BelowQuarterOfHistoricalMax_Throws()
    {
        await _guard.RecordDeviceSnapshotAsync("Job1", "DeviceA", 100);
        WriteMasterFiles(24);

        await Assert.ThrowsAsync<PushSafetyException>(() => _guard.AssertSafeToPushAsync("Job1", _masterPath, minimumPercent: 25));
    }

    [Fact]
    public async Task RecordDeviceSnapshotAsync_OnlyRaisesTheMax_NeverLowersIt()
    {
        await _guard.RecordDeviceSnapshotAsync("Job1", "DeviceA", 100);
        await _guard.RecordDeviceSnapshotAsync("Job1", "DeviceA", 10);
        WriteMasterFiles(24);

        await Assert.ThrowsAsync<PushSafetyException>(() => _guard.AssertSafeToPushAsync("Job1", _masterPath, minimumPercent: 25));
    }

    [Fact]
    public async Task RecordDeviceSnapshotAsync_UsesHighestAcrossAllDevicesInTheJob()
    {
        await _guard.RecordDeviceSnapshotAsync("Job1", "DeviceA", 20);
        await _guard.RecordDeviceSnapshotAsync("Job1", "DeviceB", 200);
        WriteMasterFiles(24);

        await Assert.ThrowsAsync<PushSafetyException>(() => _guard.AssertSafeToPushAsync("Job1", _masterPath, minimumPercent: 25));
    }

    [Fact]
    public async Task ForcePushAsync_RebasesBaselineToCurrentMasterCount_WithoutThrowing()
    {
        await _guard.RecordDeviceSnapshotAsync("Job1", "DeviceA", 100);
        WriteMasterFiles(2);

        await _guard.ForcePushAsync("Job1", _masterPath);

        await _guard.AssertSafeToPushAsync("Job1", _masterPath, minimumPercent: 25);
    }

    [Fact]
    public async Task ForcePushAsync_NoHistory_IsANoOp()
    {
        WriteMasterFiles(1);

        await _guard.ForcePushAsync("Job1", _masterPath);
        await _guard.AssertSafeToPushAsync("Job1", _masterPath, minimumPercent: 25);
    }

    [Fact]
    public async Task DifferentJobs_HaveIndependentHistories()
    {
        await _guard.RecordDeviceSnapshotAsync("JobA", "DeviceA", 100);
        WriteMasterFiles(1);

        await _guard.AssertSafeToPushAsync("JobB", _masterPath, minimumPercent: 25);
    }
}
