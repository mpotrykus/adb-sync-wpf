using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;

namespace AdbSync.Core.Tests.Config;

public class ProjectFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _projects;

    public ProjectFoldersTests()
    {
        _projects = Path.Combine(_root, "projects");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private EffectiveJobSettings Eff(string? overrideDir = null) =>
        new SyncJobConfig { Name = "Job", ProjectDirectory = overrideDir }.Resolve(new GlobalSettings { ProjectsDirectory = _projects });

    private string MarkerPath => Path.Combine(_projects, ProjectFolders.MarkerFileName);

    [Fact]
    public void Unavailable_RootMissing_SaysUnreachableAndCreatesNothing()
    {
        var reason = ProjectFolders.Unavailable(Eff(), "Job");

        Assert.Contains("isn't reachable", reason);
        Assert.False(Directory.Exists(_projects));
    }

    [Fact]
    public void Unavailable_EmptyRootWithoutMarker_IsRefusedAndNoMarkerIsWritten()
    {
        Directory.CreateDirectory(_projects);

        var reason = ProjectFolders.Unavailable(Eff(), "Job");

        Assert.Contains(ProjectFolders.MarkerFileName, reason);
        Assert.False(File.Exists(MarkerPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_projects));
    }

    [Fact]
    public void Unavailable_RootWithoutMarkerButWithThisJobsFolder_AdoptsTheRoot()
    {
        Directory.CreateDirectory(Path.Combine(_projects, "Job", "master"));

        Assert.Null(ProjectFolders.Unavailable(Eff(), "Job"));
        Assert.True(File.Exists(MarkerPath));
    }

    [Fact]
    public void Unavailable_RootWithoutMarkerHoldingOnlyAnotherJobsFolder_IsRefused()
    {
        Directory.CreateDirectory(Path.Combine(_projects, "OtherJob"));

        Assert.NotNull(ProjectFolders.Unavailable(Eff(), "Job"));
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void Unavailable_AdoptedRoot_IsAvailableEvenForANewJob()
    {
        ProjectFolders.AdoptRoot(_projects);

        Assert.Null(ProjectFolders.Unavailable(Eff(), "BrandNewJob"));
    }

    [Fact]
    public void Unavailable_CorruptMarker_IsRefused()
    {
        Directory.CreateDirectory(_projects);
        File.WriteAllText(MarkerPath, "not json");

        Assert.Contains("isn't readable and writable", ProjectFolders.Unavailable(Eff(), "Job"));
    }

    [Fact]
    public void Unavailable_UsesTheJobOverrideAsTheRoot()
    {
        var overrideDir = Path.Combine(_root, "override");
        ProjectFolders.AdoptRoot(_projects);

        Assert.Contains("isn't reachable", ProjectFolders.Unavailable(Eff(overrideDir), "Job"));

        ProjectFolders.AdoptRoot(overrideDir);
        Assert.Null(ProjectFolders.Unavailable(Eff(overrideDir), "Job"));
    }

    [Fact]
    public void AdoptRoot_CreatesTheRootAndMarker_AndKeepsTheSameMarkerOnTheSecondCall()
    {
        ProjectFolders.AdoptRoot(_projects);
        var first = File.ReadAllText(MarkerPath);

        ProjectFolders.AdoptRoot(_projects);

        Assert.Equal(first, File.ReadAllText(MarkerPath));
        Assert.Contains("rootId", first);
    }

    [Fact]
    public void RequireAvailable_UnavailableRoot_Throws() =>
        Assert.Throws<ProjectFolderUnavailableException>(() => ProjectFolders.RequireAvailable(Eff(), "Job"));

    [Fact]
    public void EnsureStillAvailable_MarkerOrMasterGone_Throws()
    {
        ProjectFolders.AdoptRoot(_projects);
        var master = Path.Combine(_projects, "Job", "master");
        Directory.CreateDirectory(master);
        ProjectFolders.EnsureStillAvailable(Eff(), master);

        Directory.Delete(master);
        Assert.Throws<ProjectFolderUnavailableException>(() => ProjectFolders.EnsureStillAvailable(Eff(), master));

        Directory.CreateDirectory(master);
        File.Delete(MarkerPath);
        Assert.Throws<ProjectFolderUnavailableException>(() => ProjectFolders.EnsureStillAvailable(Eff(), master));
    }
}
