using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Companion;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Scheduling;
using AdbSync.Core.Services.Transfer;

namespace AdbSync.Core.Tests;

public class PortabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));

    public PortabilityTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ScheduleClock_ConfiguredZone_IsUsedForNow()
    {
        var now = ScheduleClock.Now(new GlobalSettings { TimeZone = "Asia/Tokyo" });

        Assert.Equal(TimeSpan.FromHours(9), now.Offset);
        Assert.True((now - DateTimeOffset.UtcNow).Duration() < TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not/AZone")]
    public void ScheduleClock_EmptyOrUnknownZone_FallsBackToTheHostZone(string? zone) =>
        Assert.Equal(TimeZoneInfo.Local.Id, ScheduleClock.Zone(new GlobalSettings { TimeZone = zone }).Id);

    [Fact]
    public void ScheduleClock_IsValidZone()
    {
        Assert.True(ScheduleClock.IsValidZone("America/Chicago"));
        Assert.True(ScheduleClock.IsValidZone(null));
        Assert.False(ScheduleClock.IsValidZone("Not/AZone"));
    }

    [Fact]
    public void Scan_FileNameWithABackslashOnLinux_KeepsItsName()
    {
        if (OperatingSystem.IsWindows())
            return;
        File.WriteAllText(Path.Combine(_root, "a\\b.txt"), "x");

        var entries = LocalFileTreeScanner.Scan(_root, new ExcludeMatcher([]));

        Assert.Equal("a\\b.txt", Assert.Single(entries).RelativePath);
    }

    [Fact]
    public void Scan_DirectoryLinkToAnAncestor_IsSkippedInsteadOfLoopingForever()
    {
        var inner = Path.Combine(_root, "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "file.txt"), "x");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(inner, "loop"), _root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var entries = LocalFileTreeScanner.Scan(_root, new ExcludeMatcher([]));

        Assert.Equal(["inner", "inner/file.txt"], entries.Select(e => e.RelativePath).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void CompanionCertificate_IsReadableByTheOwnerOnlyOnLinux()
    {
        if (OperatingSystem.IsWindows())
            return;
        var paths = new AppPaths(_root);

        _ = new CompanionCertificateStore(paths).Certificate;

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.CompanionCertificateFile));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }
}
