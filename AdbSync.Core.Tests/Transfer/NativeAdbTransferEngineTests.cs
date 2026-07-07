using AdbSync.Core.Transfer;

namespace AdbSync.Core.Tests.Transfer;

public class NativeAdbTransferEngineTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));

    private sealed class SingleDeviceFactory(FakeRemoteFileSystem fs) : IRemoteFileSystemFactory
    {
        public IRemoteFileSystem Create(string serial) => fs;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task PullMirrorAsync_CopiesRemoteFilesAndDeletesLocalExtras()
    {
        var remote = new FakeRemoteFileSystem();
        remote.AddFile("app/keep.txt", "remote-content", T0);
        var localPath = Path.Combine(_root, "local");
        Directory.CreateDirectory(localPath);
        File.WriteAllText(Path.Combine(localPath, "stale.txt"), "old");

        var engine = new NativeAdbTransferEngine(new SingleDeviceFactory(remote), new MirrorDiffer());
        var result = await engine.PullMirrorAsync("serial-1", "app", localPath, new ExcludeMatcher([]));

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Empty(result.Errors);
        Assert.Equal("remote-content", File.ReadAllText(Path.Combine(localPath, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(localPath, "stale.txt")));
    }

    [Fact]
    public async Task PullMirrorAsync_UnchangedFile_IsNotRePulled()
    {
        var remote = new FakeRemoteFileSystem();
        remote.AddFile("app/a.txt", "same", T0);
        var localPath = Path.Combine(_root, "local");
        Directory.CreateDirectory(localPath);
        File.WriteAllText(Path.Combine(localPath, "a.txt"), "same");
        File.SetLastWriteTimeUtc(Path.Combine(localPath, "a.txt"), T0.UtcDateTime);

        var engine = new NativeAdbTransferEngine(new SingleDeviceFactory(remote), new MirrorDiffer());
        var result = await engine.PullMirrorAsync("serial-1", "app", localPath, new ExcludeMatcher([]));

        Assert.Equal(0, result.FilesCopied);
        Assert.DoesNotContain(remote.Calls, c => c.StartsWith("pull:"));
    }

    [Fact]
    public async Task PushMirrorAsync_CopiesLocalFilesAndDeletesRemoteExtras()
    {
        var remote = new FakeRemoteFileSystem();
        remote.AddFile("app/stale.txt", "old", T0);
        var localPath = Path.Combine(_root, "local");
        Directory.CreateDirectory(localPath);
        File.WriteAllText(Path.Combine(localPath, "keep.txt"), "local-content");

        var engine = new NativeAdbTransferEngine(new SingleDeviceFactory(remote), new MirrorDiffer());
        var result = await engine.PushMirrorAsync("serial-1", localPath, "app", new ExcludeMatcher([]));

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal("local-content", remote.ReadFile("app/keep.txt"));
        Assert.False(remote.FileExists("app/stale.txt"));
    }

    [Fact]
    public async Task PushMirrorAsync_NestedDirectory_CreatesRemoteDirectoryBeforePushingChild()
    {
        var remote = new FakeRemoteFileSystem();
        var localPath = Path.Combine(_root, "local");
        Directory.CreateDirectory(Path.Combine(localPath, "sub"));
        File.WriteAllText(Path.Combine(localPath, "sub", "deep.txt"), "content");

        var engine = new NativeAdbTransferEngine(new SingleDeviceFactory(remote), new MirrorDiffer());
        var result = await engine.PushMirrorAsync("serial-1", localPath, "app", new ExcludeMatcher([]));

        Assert.Empty(result.Errors);
        Assert.True(remote.DirectoryExists("app/sub"));
        Assert.Equal("content", remote.ReadFile("app/sub/deep.txt"));
    }

    [Fact]
    public async Task PushMirrorAsync_RemoteExtraDirectory_DeletesRecursivelyNotFileByFile()
    {
        var remote = new FakeRemoteFileSystem();
        remote.AddFile("app/StaleDir/child.txt", "x", T0);
        var localPath = Path.Combine(_root, "local");
        Directory.CreateDirectory(localPath);

        var engine = new NativeAdbTransferEngine(new SingleDeviceFactory(remote), new MirrorDiffer());
        var result = await engine.PushMirrorAsync("serial-1", localPath, "app", new ExcludeMatcher([]));

        Assert.Equal(1, result.FilesDeleted);
        Assert.Contains(remote.Calls, c => c == "rmrf:app/StaleDir");
        Assert.DoesNotContain(remote.Calls, c => c == "rm:app/StaleDir/child.txt");
        Assert.False(remote.DirectoryExists("app/StaleDir"));
    }

    [Fact]
    public async Task PullMirrorAsync_ExcludedRemoteFile_IsNeverPulledOrDeletedLocally()
    {
        var remote = new FakeRemoteFileSystem();
        remote.AddFile("app/Cache/junk.txt", "x", T0);
        var localPath = Path.Combine(_root, "local");
        Directory.CreateDirectory(localPath);

        var engine = new NativeAdbTransferEngine(new SingleDeviceFactory(remote), new MirrorDiffer());
        var result = await engine.PullMirrorAsync("serial-1", "app", localPath, new ExcludeMatcher(["Cache"]));

        Assert.Equal(0, result.FilesCopied);
        Assert.False(File.Exists(Path.Combine(localPath, "Cache", "junk.txt")));
    }
}
