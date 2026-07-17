using AdbSync.Core.Services.Transfer;
using AdvancedSharpAdbClient;

namespace AdbSync.Core.IntegrationTests;

/// <summary>
/// Exercises the native ADB sync-protocol engine against a real, currently-connected device or emulator.
/// Not run automatically - set ADBSYNC_TEST_SERIAL (e.g. "emulator-5554" or "192.168.0.40:5555") and run with
/// `dotnet test --filter Category=RequiresDevice`. This is exactly the empirical validation the project plan
/// calls out as needed before trusting the native engine over the proven adb.exe-shelling v1 engine.
/// </summary>
[Trait("Category", "RequiresDevice")]
public class AdbSyncRemoteFileSystemTests : IAsyncLifetime
{
    private const string RemoteTestRoot = "/sdcard/adbsync-integration-test";
    private string? _serial;
    private AdbSyncRemoteFileSystem? _fs;

    public Task InitializeAsync()
    {
        _serial = Environment.GetEnvironmentVariable("ADBSYNC_TEST_SERIAL");
        if (_serial is not null)
            _fs = new AdbSyncRemoteFileSystem(new AdbClient(), _serial);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_fs is not null)
            await _fs.DisposeAsync();
    }

    [Fact]
    public async Task PushThenPullThenDelete_RoundTripsAFile()
    {
        if (_serial is null)
            return; // set ADBSYNC_TEST_SERIAL to a connected device/emulator serial to actually run this

        var remotePath = $"{RemoteTestRoot}/roundtrip.txt";
        var localSource = Path.GetTempFileName();
        var localDest = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(localSource, "hello from AdbSync integration test");

            await _fs!.CreateDirectoryAsync(RemoteTestRoot);
            await _fs.PushFileAsync(localSource, remotePath, DateTimeOffset.UtcNow);

            var listing = await _fs.ListDirectoryAsync(RemoteTestRoot);
            Assert.Contains(listing, e => e.Name == "roundtrip.txt" && !e.IsDirectory);

            await _fs.PullFileAsync(remotePath, localDest);
            Assert.Equal("hello from AdbSync integration test", await File.ReadAllTextAsync(localDest));

            await _fs.DeleteFileAsync(remotePath);
            var afterDelete = await _fs.ListDirectoryAsync(RemoteTestRoot);
            Assert.DoesNotContain(afterDelete, e => e.Name == "roundtrip.txt");
        }
        finally
        {
            File.Delete(localSource);
            File.Delete(localDest);
            if (_fs is not null)
                await _fs.DeleteDirectoryRecursiveAsync(RemoteTestRoot);
        }
    }

    [Fact]
    public async Task NestedDirectory_IsReportedWithDirectoryFileMode()
    {
        if (_serial is null)
            return; // set ADBSYNC_TEST_SERIAL to a connected device/emulator serial to actually run this

        var nestedDir = $"{RemoteTestRoot}/nested";
        try
        {
            await _fs!.CreateDirectoryAsync(nestedDir);

            var listing = await _fs.ListDirectoryAsync(RemoteTestRoot);
            Assert.Contains(listing, e => e.Name == "nested" && e.IsDirectory);
        }
        finally
        {
            if (_fs is not null)
                await _fs.DeleteDirectoryRecursiveAsync(RemoteTestRoot);
        }
    }
}
