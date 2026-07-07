using AdbSync.Core.Transfer;

namespace AdbSync.Core.Tests.Transfer;

public class MirrorDifferTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly MirrorDiffer _differ = new();

    private static FileEntry File(string path, long size = 10, DateTimeOffset? modified = null) =>
        new(path, false, size, modified ?? T0);

    private static FileEntry Dir(string path) => new(path, true, 0, T0);

    [Fact]
    public void NewFileInSource_IsCopiedNotDeleted()
    {
        var plan = _differ.Diff([File("a.txt")], []);

        Assert.Single(plan.ToCopy);
        Assert.Empty(plan.ToDelete);
    }

    [Fact]
    public void IdenticalFile_IsNeitherCopiedNorDeleted()
    {
        var plan = _differ.Diff([File("a.txt")], [File("a.txt")]);

        Assert.Empty(plan.ToCopy);
        Assert.Empty(plan.ToDelete);
    }

    [Fact]
    public void DifferentSize_IsCopied()
    {
        var plan = _differ.Diff([File("a.txt", size: 20)], [File("a.txt", size: 10)]);

        Assert.Single(plan.ToCopy);
    }

    [Fact]
    public void DifferentModifiedTimeBeyondTolerance_IsCopied()
    {
        var plan = _differ.Diff([File("a.txt", modified: T0.AddSeconds(10))], [File("a.txt")]);

        Assert.Single(plan.ToCopy);
    }

    [Fact]
    public void ModifiedTimeWithinTolerance_IsNotCopied()
    {
        var plan = _differ.Diff([File("a.txt", modified: T0.AddSeconds(1))], [File("a.txt")]);

        Assert.Empty(plan.ToCopy);
    }

    [Fact]
    public void FileOnlyInDestination_IsDeleted()
    {
        var plan = _differ.Diff([], [File("stale.txt")]);

        Assert.Empty(plan.ToCopy);
        Assert.Single(plan.ToDelete);
    }

    [Fact]
    public void EntireStaleDirectory_OnlyTopmostEntryIsDeleted()
    {
        var destination = new[]
        {
            Dir("StaleDir"),
            File("StaleDir/child.txt"),
            File("StaleDir/nested/grandchild.txt"),
            Dir("StaleDir/nested"),
        };

        var plan = _differ.Diff([], destination);

        var deletedPaths = plan.ToDelete.Select(e => e.RelativePath).ToList();
        Assert.Equal(["StaleDir"], deletedPaths);
    }

    [Fact]
    public void DirectoryBecomingFileOrViceVersa_IsTreatedAsChanged()
    {
        var plan = _differ.Diff([File("thing")], [Dir("thing")]);

        Assert.Single(plan.ToCopy);
    }
}
