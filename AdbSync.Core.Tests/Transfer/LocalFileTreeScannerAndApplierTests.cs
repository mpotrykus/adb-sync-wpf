using AdbSync.Core.Services.Transfer;

namespace AdbSync.Core.Tests.Transfer;

public class LocalFileTreeScannerAndApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _sourceDir;
    private readonly string _destDir;
    private readonly IExcludeMatcher _noExclude = new ExcludeMatcher([]);

    public LocalFileTreeScannerAndApplierTests()
    {
        _sourceDir = Path.Combine(_root, "source");
        _destDir = Path.Combine(_root, "dest");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_destDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void WriteSourceFile(string relativePath, string content)
    {
        var path = Path.Combine(_sourceDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Scan_SkipsExcludedFilesAndDirectories()
    {
        WriteSourceFile("keep.txt", "a");
        WriteSourceFile("Cache/skip.txt", "b");
        var exclude = new ExcludeMatcher(["Cache"]);

        var entries = LocalFileTreeScanner.Scan(_sourceDir, exclude);

        Assert.Contains(entries, e => e.RelativePath == "keep.txt");
        Assert.DoesNotContain(entries, e => e.RelativePath.StartsWith("Cache"));
    }

    [Fact]
    public void ApplyPlan_CopiesNewFilesAndPreservesTimestamp()
    {
        WriteSourceFile("a.txt", "hello");
        var expectedModified = new DateTimeOffset(2020, 5, 1, 12, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(Path.Combine(_sourceDir, "a.txt"), expectedModified.UtcDateTime);

        var source = LocalFileTreeScanner.Scan(_sourceDir, _noExclude);
        var destination = LocalFileTreeScanner.Scan(_destDir, _noExclude);
        var plan = new MirrorDiffer().Diff(source, destination);

        var (copied, deleted, bytesCopied) = MirrorPlanApplier.Apply(plan, _sourceDir, _destDir);

        Assert.Equal(1, copied);
        Assert.Equal(0, deleted);
        Assert.Equal(5, bytesCopied); // "hello"
        var destPath = Path.Combine(_destDir, "a.txt");
        Assert.True(File.Exists(destPath));
        Assert.Equal("hello", File.ReadAllText(destPath));
        Assert.Equal(expectedModified.UtcDateTime, File.GetLastWriteTimeUtc(destPath), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ApplyPlan_DeletesFilesNotInSource()
    {
        File.WriteAllText(Path.Combine(_destDir, "stale.txt"), "old");

        var source = LocalFileTreeScanner.Scan(_sourceDir, _noExclude);
        var destination = LocalFileTreeScanner.Scan(_destDir, _noExclude);
        var plan = new MirrorDiffer().Diff(source, destination);

        var (copied, deleted, _) = MirrorPlanApplier.Apply(plan, _sourceDir, _destDir);

        Assert.Equal(0, copied);
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(_destDir, "stale.txt")));
    }

    [Fact]
    public void ApplyPlan_UnchangedFileIsNotRewritten()
    {
        WriteSourceFile("a.txt", "same");
        File.WriteAllText(Path.Combine(_destDir, "a.txt"), "same");
        var sameTime = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastWriteTimeUtc(Path.Combine(_sourceDir, "a.txt"), sameTime);
        File.SetLastWriteTimeUtc(Path.Combine(_destDir, "a.txt"), sameTime);

        var source = LocalFileTreeScanner.Scan(_sourceDir, _noExclude);
        var destination = LocalFileTreeScanner.Scan(_destDir, _noExclude);
        var plan = new MirrorDiffer().Diff(source, destination);

        Assert.Empty(plan.ToCopy);
        Assert.Empty(plan.ToDelete);
    }

    [Fact]
    public void RoundTrip_MirrorsNestedDirectoriesEndToEnd()
    {
        WriteSourceFile("dir1/dir2/deep.txt", "deep content");
        WriteSourceFile("top.txt", "top content");

        var source = LocalFileTreeScanner.Scan(_sourceDir, _noExclude);
        var destination = LocalFileTreeScanner.Scan(_destDir, _noExclude);
        var plan = new MirrorDiffer().Diff(source, destination);
        MirrorPlanApplier.Apply(plan, _sourceDir, _destDir);

        Assert.Equal("deep content", File.ReadAllText(Path.Combine(_destDir, "dir1", "dir2", "deep.txt")));
        Assert.Equal("top content", File.ReadAllText(Path.Combine(_destDir, "top.txt")));
    }
}
