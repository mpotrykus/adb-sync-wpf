using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Models.Merge;
using AdbSync.Core.Services.Merge;

namespace AdbSync.Core.Tests.Merge;

public class ManifestStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _stagingPath;
    private readonly string _masterPath;
    private readonly ManifestStore _store;

    public ManifestStoreTests()
    {
        _stagingPath = Path.Combine(_root, "staging");
        _masterPath = Path.Combine(_root, "master");
        Directory.CreateDirectory(_stagingPath);
        Directory.CreateDirectory(_masterPath);
        _store = new ManifestStore(new AppPaths(Path.Combine(_root, "appdata")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Write(string root, string relativePath, string content, DateTimeOffset modified)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
    }

    [Fact]
    public async Task GetOrBootstrapAsync_NoExistingManifest_BuildsFromAgreeingFiles()
    {
        Write(_stagingPath, "agree.txt", "same", T0);
        Write(_masterPath, "agree.txt", "same", T0);
        Write(_stagingPath, "staging-only.txt", "x", T0);
        Write(_masterPath, "master-only.txt", "y", T0);

        var manifest = await _store.GetOrBootstrapAsync("Job1", "DeviceA", _stagingPath, _masterPath);

        Assert.True(manifest.Entries.ContainsKey("agree.txt"));
        Assert.False(manifest.Entries.ContainsKey("staging-only.txt"));
        Assert.False(manifest.Entries.ContainsKey("master-only.txt"));
    }

    [Fact]
    public async Task GetOrBootstrapAsync_DisagreeingContent_IsNotIncludedInBaseline()
    {
        Write(_stagingPath, "differs.txt", "staging-version", T0);
        Write(_masterPath, "differs.txt", "master-version-longer", T0);

        var manifest = await _store.GetOrBootstrapAsync("Job1", "DeviceA", _stagingPath, _masterPath);

        Assert.False(manifest.Entries.ContainsKey("differs.txt"));
    }

    [Fact]
    public async Task SaveAsync_ThenGetOrBootstrapAsync_ReturnsPersistedManifestWithoutReBootstrapping()
    {
        var toSave = new SyncManifest
        {
            Entries = new Dictionary<string, ManifestEntry> { ["a.txt"] = new ManifestEntry(42, T0) },
        };
        await _store.SaveAsync("Job1", "DeviceA", toSave);

        // Even though staging/master disagree, a persisted manifest should be returned as-is, not re-bootstrapped.
        Write(_stagingPath, "a.txt", "irrelevant", T0);
        var loaded = await _store.GetOrBootstrapAsync("Job1", "DeviceA", _stagingPath, _masterPath);

        Assert.Equal(42, loaded.Entries["a.txt"].Size);
    }

    [Fact]
    public async Task DifferentJobsAndDevices_HaveIndependentManifests()
    {
        await _store.SaveAsync("JobA", "Device1", new SyncManifest { Entries = new() { ["x"] = new ManifestEntry(1, T0) } });
        await _store.SaveAsync("JobA", "Device2", new SyncManifest { Entries = new() { ["x"] = new ManifestEntry(2, T0) } });

        var manifest1 = await _store.GetOrBootstrapAsync("JobA", "Device1", _stagingPath, _masterPath);
        var manifest2 = await _store.GetOrBootstrapAsync("JobA", "Device2", _stagingPath, _masterPath);

        Assert.Equal(1, manifest1.Entries["x"].Size);
        Assert.Equal(2, manifest2.Entries["x"].Size);
    }
}
