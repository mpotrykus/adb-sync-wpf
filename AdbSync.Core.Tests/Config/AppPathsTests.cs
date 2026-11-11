using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Config;

namespace AdbSync.Core.Tests.Config;

public class AppPathsTests
{
    private static readonly string FullDataDir = Path.Combine(Path.GetTempPath(), "adbsync-data");
    private static readonly string FullProjectsDir = Path.Combine(Path.GetTempPath(), "adbsync-projects");

    private static Func<string, string?> Variables(string? dataDir = null, string? projectsDir = null) => name => name switch
    {
        AppPaths.DataDirVariable => dataDir,
        AppPaths.ProjectsDirVariable => projectsDir,
        _ => null,
    };

    [Fact]
    public void FromEnvironment_DataDirVariable_IsTheRoot()
    {
        var paths = AppPaths.FromEnvironment(Variables(dataDir: FullDataDir));

        Assert.Equal(FullDataDir, paths.Root);
        Assert.Equal(Path.Combine(FullDataDir, "config", "settings.json"), paths.SettingsFile);
    }

    [Fact]
    public void FromEnvironment_NoVariables_FallsBackToAFullPerUserPath()
    {
        var paths = AppPaths.FromEnvironment(Variables());

        Assert.True(Path.IsPathFullyQualified(paths.Root));
        Assert.Equal("AdbSync", Path.GetFileName(paths.Root));
    }

    [Theory]
    [InlineData("relative/data")]
    [InlineData("data")]
    public void FromEnvironment_RelativeDataDir_Throws(string dataDir)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AppPaths.FromEnvironment(Variables(dataDir: dataDir)));
        Assert.Contains(AppPaths.DataDirVariable, ex.Message);
    }

    [Fact]
    public void FromEnvironment_ProjectsDirVariable_IsTheDefaultProjectsDirectory()
    {
        var paths = AppPaths.FromEnvironment(Variables(dataDir: FullDataDir, projectsDir: FullProjectsDir));

        Assert.Equal(FullProjectsDir, paths.DefaultProjectsDirectory);
    }

    [Fact]
    public void FromEnvironment_RelativeProjectsDir_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppPaths.FromEnvironment(Variables(dataDir: FullDataDir, projectsDir: "projects")));
        Assert.Contains(AppPaths.ProjectsDirVariable, ex.Message);
    }

    [Fact]
    public void FromEnvironment_NoProjectsDirVariable_UsesDocumentsOnWindowsAndTheDataRootElsewhere()
    {
        var paths = AppPaths.FromEnvironment(Variables(dataDir: FullDataDir));

        var expected = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AdbSync Projects")
            : Path.Combine(FullDataDir, "projects");
        Assert.Equal(expected, paths.DefaultProjectsDirectory);
    }

    [Fact]
    public void FromEnvironment_BlankVariables_AreTreatedAsUnset()
    {
        var paths = AppPaths.FromEnvironment(Variables(dataDir: "  ", projectsDir: ""));

        Assert.True(Path.IsPathFullyQualified(paths.Root));
    }

    [Fact]
    public void RootProblem_EmptyRelativeAndFullPaths()
    {
        Assert.NotNull(ProjectFolders.RootProblem(null));
        Assert.NotNull(ProjectFolders.RootProblem(""));
        Assert.NotNull(ProjectFolders.RootProblem("projects"));
        Assert.Null(ProjectFolders.RootProblem(FullProjectsDir));
    }

    [Fact]
    public void RootProblem_WindowsDriveLetterPathOnLinux_IsNotAFullPath()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.NotNull(ProjectFolders.RootProblem(@"D:\Documents\AdbSync Projects"));
    }

    [Fact]
    public void ProjectRoot_ValidRootAndName_CombinesThem()
    {
        var eff = new SyncJobConfig { Name = "Job" }.Resolve(new GlobalSettings { ProjectsDirectory = FullProjectsDir });

        Assert.Equal(Path.Combine(FullProjectsDir, "Job"), ProjectFolders.ProjectRoot(eff, "Job"));
        Assert.Equal(Path.Combine(FullProjectsDir, "Job"), ProjectFolders.TryProjectRoot(eff, "Job"));
    }

    [Fact]
    public void ProjectRoot_RelativeRoot_ThrowsAndTryReturnsNull()
    {
        var eff = new SyncJobConfig { Name = "Job" }.Resolve(new GlobalSettings { ProjectsDirectory = "projects" });

        Assert.Throws<InvalidOperationException>(() => ProjectFolders.ProjectRoot(eff, "Job"));
        Assert.Null(ProjectFolders.TryProjectRoot(eff, "Job"));
    }

    [Fact]
    public void TryProjectRoot_InvalidName_ReturnsNull()
    {
        var eff = new SyncJobConfig { Name = ".." }.Resolve(new GlobalSettings { ProjectsDirectory = FullProjectsDir });

        Assert.Null(ProjectFolders.TryProjectRoot(eff, ".."));
    }

    [Fact]
    public void JobProjectDirectoryOverride_IsUsedAsTheRoot()
    {
        var overrideDir = Path.Combine(FullProjectsDir, "override");
        var eff = new SyncJobConfig { Name = "Job", ProjectDirectory = overrideDir }
            .Resolve(new GlobalSettings { ProjectsDirectory = FullProjectsDir });

        Assert.Equal(Path.Combine(overrideDir, "Job"), ProjectFolders.ProjectRoot(eff, "Job"));
    }
}
