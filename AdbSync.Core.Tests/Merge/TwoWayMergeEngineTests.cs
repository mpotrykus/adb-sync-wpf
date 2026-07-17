using AdbSync.Core.Models.Merge;
using AdbSync.Core.Services.Merge;

namespace AdbSync.Core.Tests.Merge;

/// <summary>
/// Exhaustively covers the merge engine's classification table (presence in staging/master/manifest, and
/// changed-since-baseline for each side). This is the highest-risk component in the project - a wrong
/// decision here means silent data loss - so every row gets its own test rather than relying on end-to-end coverage.
/// </summary>
public class TwoWayMergeEngineTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _stagingPath;
    private readonly string _masterPath;
    private readonly TwoWayMergeEngine _engine = new();

    public TwoWayMergeEngineTests()
    {
        _stagingPath = Path.Combine(_root, "staging");
        _masterPath = Path.Combine(_root, "master");
        Directory.CreateDirectory(_stagingPath);
        Directory.CreateDirectory(_masterPath);
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

    private long SizeOf(string root, string relativePath) => new FileInfo(Path.Combine(root, relativePath)).Length;

    private static SyncManifest ManifestWith(string relativePath, long size, DateTimeOffset modified) => new()
    {
        Entries = new Dictionary<string, ManifestEntry> { [relativePath] = new ManifestEntry(size, modified) },
    };

    private static SyncManifest EmptyManifest() => new();

    private static string Read(string root, string relativePath) => File.ReadAllText(Path.Combine(root, relativePath));
    private static bool Exists(string root, string relativePath) => File.Exists(Path.Combine(root, relativePath));

    [Fact]
    public async Task NewInStagingOnly_IsCopiedToMaster()
    {
        Write(_stagingPath, "a.txt", "from-staging", T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, EmptyManifest(), new MergeOptions());

        Assert.Equal(1, result.Created);
        Assert.True(result.AnyChange);
        Assert.Equal("from-staging", Read(_masterPath, "a.txt"));
        Assert.True(result.UpdatedManifest.Entries.ContainsKey("a.txt"));
    }

    [Fact]
    public async Task NewInMasterOnly_IsCopiedToStaging()
    {
        Write(_masterPath, "a.txt", "from-master", T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, EmptyManifest(), new MergeOptions());

        Assert.Equal(1, result.Created);
        Assert.Equal("from-master", Read(_stagingPath, "a.txt"));
    }

    [Fact]
    public async Task CreatedIndependentlyOnBothSides_NewerStagingWins_BacksUpMaster()
    {
        Write(_masterPath, "a.txt", "master-version", T0);
        Write(_stagingPath, "a.txt", "staging-version", T0.AddMinutes(5));

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, EmptyManifest(), new MergeOptions());

        Assert.Single(result.Conflicts);
        Assert.Equal("staging", result.Conflicts[0].WinningSide);
        Assert.Equal("staging-version", Read(_masterPath, "a.txt"));
        Assert.NotNull(result.Conflicts[0].BackupPath);
        Assert.Equal("master-version", File.ReadAllText(result.Conflicts[0].BackupPath!));
    }

    [Fact]
    public async Task CreatedIndependentlyOnBothSides_NewerMasterWins_BacksUpStaging()
    {
        Write(_masterPath, "a.txt", "master-version", T0.AddMinutes(5));
        Write(_stagingPath, "a.txt", "staging-version", T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, EmptyManifest(), new MergeOptions());

        Assert.Single(result.Conflicts);
        Assert.Equal("master", result.Conflicts[0].WinningSide);
        Assert.Equal("master-version", Read(_stagingPath, "a.txt"));
        Assert.Equal("staging-version", File.ReadAllText(result.Conflicts[0].BackupPath!));
    }

    [Fact]
    public async Task PresentOnBothSidesWithNoBaseline_ButAlreadyIdentical_NoConflictAndManifestSeeded()
    {
        Write(_masterPath, "a.txt", "same-version", T0);
        Write(_stagingPath, "a.txt", "same-version", T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, EmptyManifest(), new MergeOptions());

        Assert.Empty(result.Conflicts);
        Assert.False(result.AnyChange);
        Assert.Equal("same-version", Read(_masterPath, "a.txt"));
        Assert.Equal("same-version", Read(_stagingPath, "a.txt"));
        Assert.True(result.UpdatedManifest.Entries.TryGetValue("a.txt", out var entry));
        Assert.Equal(SizeOf(_stagingPath, "a.txt"), entry!.Size);
    }

    [Fact]
    public async Task DeletedFromMaster_StagingUnchanged_PropagatesDeleteToStaging()
    {
        Write(_stagingPath, "a.txt", "original", T0);
        var manifest = ManifestWith("a.txt", SizeOf(_stagingPath, "a.txt"), T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, manifest, new MergeOptions());

        Assert.Equal(1, result.Deleted);
        Assert.False(Exists(_stagingPath, "a.txt"));
        Assert.False(result.UpdatedManifest.Entries.ContainsKey("a.txt"));
    }

    [Fact]
    public async Task DeletedFromMaster_ButStagingModifiedAfter_ConflictStagingWinsWithNoBackup()
    {
        Write(_stagingPath, "a.txt", "modified-after-delete", T0.AddMinutes(10));
        var manifest = ManifestWith("a.txt", 999, T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, manifest, new MergeOptions());

        Assert.Single(result.Conflicts);
        Assert.Equal("staging", result.Conflicts[0].WinningSide);
        Assert.Null(result.Conflicts[0].BackupPath);
        Assert.Equal("modified-after-delete", Read(_masterPath, "a.txt"));
    }

    [Fact]
    public async Task DeletedFromStaging_MasterUnchanged_PropagatesDeleteToMaster()
    {
        Write(_masterPath, "a.txt", "original", T0);
        var manifest = ManifestWith("a.txt", SizeOf(_masterPath, "a.txt"), T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, manifest, new MergeOptions());

        Assert.Equal(1, result.Deleted);
        Assert.False(Exists(_masterPath, "a.txt"));
    }

    [Fact]
    public async Task DeletedFromStaging_ButMasterModifiedAfter_ConflictMasterWinsWithNoBackup()
    {
        Write(_masterPath, "a.txt", "modified-after-delete", T0.AddMinutes(10));
        var manifest = ManifestWith("a.txt", 999, T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, manifest, new MergeOptions());

        Assert.Single(result.Conflicts);
        Assert.Equal("master", result.Conflicts[0].WinningSide);
        Assert.Null(result.Conflicts[0].BackupPath);
        Assert.Equal("modified-after-delete", Read(_stagingPath, "a.txt"));
    }

    [Fact]
    public async Task DeletedFromBothSides_RemovesManifestEntryAsNoOp()
    {
        var manifest = ManifestWith("a.txt", 5, T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, manifest, new MergeOptions());

        Assert.False(result.AnyChange);
        Assert.False(result.UpdatedManifest.Entries.ContainsKey("a.txt"));
    }

    [Fact]
    public async Task UnchangedOnBothSides_IsNoOp()
    {
        Write(_stagingPath, "a.txt", "same", T0);
        Write(_masterPath, "a.txt", "same", T0);
        var manifest = ManifestWith("a.txt", SizeOf(_stagingPath, "a.txt"), T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, manifest, new MergeOptions());

        Assert.False(result.AnyChange);
        Assert.True(result.UpdatedManifest.Entries.ContainsKey("a.txt"));
    }

    [Fact]
    public async Task OnlyStagingChangedSinceBaseline_PropagatesToMaster()
    {
        Write(_masterPath, "a.txt", "original", T0);
        var manifest = ManifestWith("a.txt", SizeOf(_masterPath, "a.txt"), T0);
        Write(_stagingPath, "a.txt", "staging-edited", T0.AddMinutes(5));

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, manifest, new MergeOptions());

        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Conflicts);
        Assert.Equal("staging-edited", Read(_masterPath, "a.txt"));
    }

    [Fact]
    public async Task OnlyMasterChangedSinceBaseline_PropagatesToStaging()
    {
        Write(_stagingPath, "a.txt", "original", T0);
        var manifest = ManifestWith("a.txt", SizeOf(_stagingPath, "a.txt"), T0);
        Write(_masterPath, "a.txt", "master-edited", T0.AddMinutes(5));

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, manifest, new MergeOptions());

        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Conflicts);
        Assert.Equal("master-edited", Read(_stagingPath, "a.txt"));
    }

    [Fact]
    public async Task BothChangedDifferentlySinceBaseline_NewerWinsAndOlderIsBackedUp()
    {
        Write(_stagingPath, "a.txt", "original", T0);
        var manifest = ManifestWith("a.txt", SizeOf(_stagingPath, "a.txt"), T0);
        Write(_stagingPath, "a.txt", "staging-edit", T0.AddMinutes(5));
        Write(_masterPath, "a.txt", "master-edit-newer", T0.AddMinutes(10));

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, manifest, new MergeOptions());

        Assert.Single(result.Conflicts);
        Assert.Equal("master", result.Conflicts[0].WinningSide);
        Assert.Equal("master-edit-newer", Read(_stagingPath, "a.txt"));
        Assert.Equal("staging-edit", File.ReadAllText(result.Conflicts[0].BackupPath!));
    }

    [Fact]
    public async Task DryRun_ReportsWouldBeChanges_ButTouchesNoFiles()
    {
        Write(_stagingPath, "a.txt", "from-staging", T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, EmptyManifest(), new MergeOptions(DryRun: true));

        Assert.Equal(1, result.Created);
        Assert.False(Exists(_masterPath, "a.txt"));
    }

    [Fact]
    public async Task BackupConflictLosersDisabled_StillRecordsConflictButWritesNoBackupFile()
    {
        Write(_masterPath, "a.txt", "master-version", T0);
        Write(_stagingPath, "a.txt", "staging-version", T0.AddMinutes(5));

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, EmptyManifest(), new MergeOptions(BackupConflictLosers: false));

        Assert.Single(result.Conflicts);
        Assert.Null(result.Conflicts[0].BackupPath);
    }

    [Fact]
    public async Task BothLookChangedVsStaleBaseline_ButAlreadyAgree_ReconcilesWithoutConflict()
    {
        Write(_stagingPath, "a.txt", "same-content", T0.AddDays(1));
        Write(_masterPath, "a.txt", "same-content", T0.AddDays(1));
        var manifest = ManifestWith("a.txt", 999, T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, manifest, new MergeOptions());

        Assert.Empty(result.Conflicts);
        Assert.False(result.AnyChange);
        Assert.Equal("same-content", Read(_masterPath, "a.txt"));
        Assert.Equal("same-content", Read(_stagingPath, "a.txt"));
        Assert.True(result.UpdatedManifest.Entries.TryGetValue("a.txt", out var entry));
        Assert.Equal(SizeOf(_stagingPath, "a.txt"), entry!.Size);
        Assert.Equal(T0.AddDays(1), entry.ModifiedUtc);
    }

    [Fact]
    public async Task PresentOnBothSidesWithNoBaseline_SameSizeAndMtimeButDifferentContent_StillConflicts()
    {
        Write(_masterPath, "a.txt", "aaaaaaaaaa", T0);
        Write(_stagingPath, "a.txt", "bbbbbbbbbb", T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, EmptyManifest(), new MergeOptions());

        Assert.Single(result.Conflicts);
    }

    [Fact]
    public async Task StaleBaseline_SameSizeAndMtimeButDifferentContent_StillTreatedAsConflict()
    {
        Write(_stagingPath, "a.txt", "aaaaaaaaaa", T0.AddDays(1));
        Write(_masterPath, "a.txt", "bbbbbbbbbb", T0.AddDays(1));
        var manifest = ManifestWith("a.txt", 999, T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, manifest, new MergeOptions());

        Assert.Single(result.Conflicts);
    }

    [Fact]
    public async Task MultipleIndependentFiles_EachClassifiedSeparately()
    {
        Write(_stagingPath, "new-in-staging.txt", "x", T0);
        Write(_masterPath, "new-in-master.txt", "y", T0);
        Write(_stagingPath, "unchanged.txt", "z", T0);
        Write(_masterPath, "unchanged.txt", "z", T0);
        var manifest = ManifestWith("unchanged.txt", SizeOf(_stagingPath, "unchanged.txt"), T0);

        var result = await _engine.MergeAsync(_stagingPath, _masterPath, manifest, new MergeOptions());

        Assert.Equal(2, result.Created);
        Assert.Empty(result.Conflicts);
        Assert.True(Exists(_masterPath, "new-in-staging.txt"));
        Assert.True(Exists(_stagingPath, "new-in-master.txt"));
    }
}
