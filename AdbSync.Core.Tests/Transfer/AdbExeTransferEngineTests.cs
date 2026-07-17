using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Transfer;

namespace AdbSync.Core.Tests.Transfer;

public class AdbExeTransferEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task PullMirrorAsync_ScansThePulledTreeAndCopiesIntoLocalDestination()
    {
        var runner = new FakeAdbProcessRunner();
        string? capturedTempRoot = null;
        runner.Handlers["pull"] = args =>
        {
            var tempRoot = args[4];
            capturedTempRoot = tempRoot;
            var pulledRoot = Path.Combine(tempRoot, "files");
            Directory.CreateDirectory(pulledRoot);
            File.WriteAllText(Path.Combine(pulledRoot, "a.txt"), "hello");
            return new AdbProcessResult(0, "", "");
        };
        var engine = new AdbExeTransferEngine(runner, new MirrorDiffer());
        var localPath = Path.Combine(_root, "dest");

        var result = await engine.PullMirrorAsync("emulator-5554", "/sdcard/app/files", localPath, new ExcludeMatcher([]));

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(localPath, "a.txt")));
        Assert.NotNull(capturedTempRoot);
        Assert.False(Directory.Exists(capturedTempRoot), "temp pull directory should be cleaned up");
    }

    [Fact]
    public async Task PullMirrorAsync_AdbPullFailure_ReturnsErrorWithoutThrowing()
    {
        var runner = new FakeAdbProcessRunner();
        runner.Handlers["pull"] = _ => new AdbProcessResult(1, "", "device offline");
        var engine = new AdbExeTransferEngine(runner, new MirrorDiffer());

        var result = await engine.PullMirrorAsync("emulator-5554", "/sdcard/app/files", Path.Combine(_root, "dest"), new ExcludeMatcher([]));

        Assert.False(result.AnyChange);
        Assert.Contains(result.Errors, e => e.Contains("device offline"));
    }

    [Fact]
    public async Task PushMirrorAsync_PushesEachTopLevelChildAndDeletesRemoteExtras()
    {
        var localSourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(localSourceDir);
        File.WriteAllText(Path.Combine(localSourceDir, "keep.txt"), "x");

        var runner = new FakeAdbProcessRunner();
        runner.Handlers["shell"] = args =>
        {
            var list = args.ToList();
            if (list.Contains("find"))
            {
                var type = list[list.IndexOf("-type") + 1];
                var output = type == "f" ? "/remote/keep.txt\n/remote/stale.txt\n" : "";
                return new AdbProcessResult(0, output, "");
            }
            return new AdbProcessResult(0, "", "");
        };

        var engine = new AdbExeTransferEngine(runner, new MirrorDiffer());
        var result = await engine.PushMirrorAsync("emulator-5554", localSourceDir, "/remote", new ExcludeMatcher([]));

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Contains(runner.Calls, c => c.Contains("push") && c.Contains(Path.Combine(localSourceDir, "keep.txt")));
        Assert.Contains(runner.Calls, c => c.Contains("rm") && c.Contains("/remote/stale.txt") && !c.Contains("-rf"));
    }

    [Fact]
    public async Task PushMirrorAsync_ExcludedTopLevelChild_IsNeverPushed()
    {
        var localSourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(localSourceDir, "Cache"));
        File.WriteAllText(Path.Combine(localSourceDir, "Cache", "junk.txt"), "x");

        var runner = new FakeAdbProcessRunner();
        var engine = new AdbExeTransferEngine(runner, new MirrorDiffer());

        await engine.PushMirrorAsync("emulator-5554", localSourceDir, "/remote", new ExcludeMatcher(["Cache"]));

        Assert.DoesNotContain(runner.Calls, c => c.Contains("push") && c.Any(a => a.Contains("Cache")));
    }

    [Fact]
    public async Task PushMirrorAsync_RemoteExtraUnderStaleDirectory_OnlyDirectoryIsRemoved()
    {
        var localSourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(localSourceDir);

        var runner = new FakeAdbProcessRunner();
        runner.Handlers["shell"] = args =>
        {
            var list = args.ToList();
            if (list.Contains("find"))
            {
                var type = list[list.IndexOf("-type") + 1];
                var output = type == "d" ? "/remote/StaleDir\n" : "/remote/StaleDir/child.txt\n";
                return new AdbProcessResult(0, output, "");
            }
            return new AdbProcessResult(0, "", "");
        };

        var engine = new AdbExeTransferEngine(runner, new MirrorDiffer());
        var result = await engine.PushMirrorAsync("emulator-5554", localSourceDir, "/remote", new ExcludeMatcher([]));

        Assert.Equal(1, result.FilesDeleted);
        Assert.Contains(runner.Calls, c => c.Contains("rm") && c.Contains("-rf") && c.Contains("/remote/StaleDir"));
        Assert.DoesNotContain(runner.Calls, c => c.Contains("rm") && c.Contains("/remote/StaleDir/child.txt"));
    }
}
