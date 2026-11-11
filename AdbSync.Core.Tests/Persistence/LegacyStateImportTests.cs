using AdbSync.Core.Models.Merge;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Persistence;

namespace AdbSync.Core.Tests.Persistence;

public class LegacyStateImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public LegacyStateImportTests()
    {
        _paths = new AppPaths(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Backup(string relativePath) => Path.Combine(_root, EngineDatabase.BackupFolderName, relativePath);

    [Fact]
    public async Task FirstOpen_ImportsEveryKindOfLegacyStateAndBacksUpTheFiles()
    {
        Write(Path.Combine("manifests", "Nomad Sculpt", "Mitch S9.manifest.json"),
            """{ "version": 1, "lastMergedAtUtc": "2026-11-10T10:00:00+00:00", "entries": { "a/b.nomad": { "size": 12, "modifiedUtc": "2026-11-10T09:00:00+00:00" } } }""");
        Write(Path.Combine("checkpoints", "Nomad Sculpt.json"),
            """{ "version": 1, "savedAt": "2026-11-10T10:00:00+00:00", "projectIndex": 0, "projectName": "Nomad Sculpt", "phase": 3, "completedDevices": ["Mitch S9"], "deviceSerials": {} }""");
        Write(Path.Combine("push-safety", "Nomad Sculpt.json"), """{ "maxFileCounts": { "Mitch S9": 40 } }""");
        Write(Path.Combine("pending-conflicts", "Nomad Sculpt", "Mitch S9.pending.json"),
            """{ "items": [ { "id": "abc123", "jobName": "Nomad Sculpt", "deviceName": "Mitch S9", "relativePath": "a/b.nomad", "detectedAtUtc": "2026-11-10T10:00:00+00:00" } ] }""");
        var blob = Write(Path.Combine("pending-conflicts", "Nomad Sculpt", "Mitch S9", "abc123.blob"), "device copy");

        var manifest = await new ManifestStore(_paths).TryGetAsync("Nomad Sculpt", "Mitch S9");
        var checkpoint = await new CheckpointManager(_paths).LoadAsync("Nomad Sculpt");
        var conflict = Assert.Single(await new PendingConflictStore(_paths).ListAsync("Nomad Sculpt"));

        Assert.Equal(12, manifest!.Entries["a/b.nomad"].Size);
        Assert.Equal(["Mitch S9"], checkpoint!.CompletedDevices);
        Assert.Equal("a/b.nomad", conflict.RelativePath);
        Assert.Equal(blob, new PendingConflictStore(_paths).GetDeviceCopyPath(conflict));
        await Assert.ThrowsAsync<PushSafetyException>(() =>
            new PushSafetyGuard(_paths).AssertSafeToPushAsync("Nomad Sculpt", MasterWithFiles(1), 25));

        Assert.True(File.Exists(Backup(Path.Combine("manifests", "Nomad Sculpt", "Mitch S9.manifest.json"))));
        Assert.True(File.Exists(Backup(Path.Combine("checkpoints", "Nomad Sculpt.json"))));
        Assert.True(File.Exists(Backup(Path.Combine("push-safety", "Nomad Sculpt.json"))));
        Assert.True(File.Exists(Backup(Path.Combine("pending-conflicts", "Nomad Sculpt", "Mitch S9.pending.json"))));
        Assert.True(File.Exists(blob));
    }

    [Fact]
    public async Task FirstOpen_UnreadableFile_IsSkippedAndLeftInPlace()
    {
        var torn = Write(Path.Combine("checkpoints", "Torn.json"), "");
        Write(Path.Combine("checkpoints", "Good.json"),
            """{ "version": 1, "savedAt": "2026-11-10T10:00:00+00:00", "projectIndex": 0, "projectName": "Good", "phase": 3, "completedDevices": [], "deviceSerials": {} }""");

        var checkpoints = new CheckpointManager(_paths);

        Assert.Null(await checkpoints.LoadAsync("Torn"));
        Assert.NotNull(await checkpoints.LoadAsync("Good"));
        Assert.True(File.Exists(torn));
    }

    [Fact]
    public async Task Keys_IgnoreCaseLikeConfigNames()
    {
        var store = new ManifestStore(_paths);
        await store.SaveAsync("Photos", "Pixel", new SyncManifest { Entries = { ["x"] = new ManifestEntry(1, DateTimeOffset.UtcNow) } });

        Assert.NotNull(await store.TryGetAsync("photos", "PIXEL"));
        Assert.Equal(1, await store.ClearAsync("PHOTOS"));
    }

    private string MasterWithFiles(int count)
    {
        var master = Path.Combine(_root, "master");
        Directory.CreateDirectory(master);
        for (var i = 0; i < count; i++)
            File.WriteAllText(Path.Combine(master, $"f{i}.txt"), "x");
        return master;
    }
}
