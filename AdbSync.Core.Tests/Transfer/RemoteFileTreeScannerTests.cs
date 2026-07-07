using AdbSync.Core.Transfer;

namespace AdbSync.Core.Tests.Transfer;

public class RemoteFileTreeScannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ScanAsync_FlatDirectory_ReturnsAllFiles()
    {
        var remote = new FakeRemoteFileSystem();
        remote.AddFile("app/a.txt", "a", T0);
        remote.AddFile("app/b.txt", "b", T0);

        var entries = await RemoteFileTreeScanner.ScanAsync(remote, "app", new ExcludeMatcher([]));

        Assert.Equal(["a.txt", "b.txt"], entries.Select(e => e.RelativePath).OrderBy(x => x));
    }

    [Fact]
    public async Task ScanAsync_NestedDirectories_RecursesAndIncludesDirectoryEntries()
    {
        var remote = new FakeRemoteFileSystem();
        remote.AddFile("app/sub/deep.txt", "x", T0);

        var entries = await RemoteFileTreeScanner.ScanAsync(remote, "app", new ExcludeMatcher([]));

        Assert.Contains(entries, e => e.RelativePath == "sub" && e.IsDirectory);
        Assert.Contains(entries, e => e.RelativePath == "sub/deep.txt" && !e.IsDirectory);
    }

    [Fact]
    public async Task ScanAsync_ExcludedDirectory_IsNeverListed()
    {
        var remote = new FakeRemoteFileSystem();
        remote.AddFile("app/Cache/thumb.png", "x", T0);
        remote.AddFile("app/keep.txt", "y", T0);

        var entries = await RemoteFileTreeScanner.ScanAsync(remote, "app", new ExcludeMatcher(["Cache"]));

        Assert.DoesNotContain(entries, e => e.RelativePath.StartsWith("Cache"));
        Assert.Contains(entries, e => e.RelativePath == "keep.txt");
        Assert.DoesNotContain(remote.Calls, c => c.Contains("Cache"));
    }

    [Fact]
    public async Task ScanAsync_EmptyDirectory_ReturnsNoEntries()
    {
        var remote = new FakeRemoteFileSystem();
        remote.AddDirectory("app");

        var entries = await RemoteFileTreeScanner.ScanAsync(remote, "app", new ExcludeMatcher([]));

        Assert.Empty(entries);
    }
}
