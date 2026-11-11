using AdbSync.Core.Models.Merge;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Merge;

namespace AdbSync.Core.Tests.Config;

public class ConfigNamesTests
{
    [Theory]
    [InlineData("Nomad Sculpt")]
    [InlineData("Mitch S23+")]
    [InlineData("a")]
    [InlineData("v1.2")]
    [InlineData(".hidden")]
    [InlineData("Console")]
    [InlineData("COM10")]
    [InlineData("名前")]
    public void Problem_ValidName_ReturnsNull(string name) =>
        Assert.Null(ConfigNames.Problem(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("ends.")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../escape")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b>")]
    [InlineData("a|b")]
    [InlineData("tab\there")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("com1")]
    [InlineData("LPT9")]
    [InlineData("aux.txt")]
    [InlineData("COM¹")]
    public void Problem_InvalidName_ReturnsMessage(string? name) =>
        Assert.NotNull(ConfigNames.Problem(name));

    [Fact]
    public void Problem_NameAtMaxLength_IsValidButOneLongerIsNot()
    {
        Assert.Null(ConfigNames.Problem(new string('a', ConfigNames.MaxLength)));
        Assert.NotNull(ConfigNames.Problem(new string('a', ConfigNames.MaxLength + 1)));
    }

    [Fact]
    public void IsUnique_ComparesIgnoringCase()
    {
        Assert.False(ConfigNames.IsUnique("pixel", ["Pixel", "Tab"]));
        Assert.True(ConfigNames.IsUnique("Pixel 2", ["Pixel", "Tab"]));
    }

    [Fact]
    public void Segment_InvalidName_Throws() =>
        Assert.Throws<ArgumentException>(() => ConfigNames.Segment(".."));

    [Fact]
    public void Segment_ValidName_ReturnsIt() =>
        Assert.Equal("Nomad Sculpt", ConfigNames.Segment("Nomad Sculpt"));

    [Fact]
    public async Task ManifestStore_TraversalJobName_ThrowsInsteadOfWritingOutsideItsFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var store = new ManifestStore(paths);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.SaveAsync("../../outside", "DeviceA", new SyncManifest()));

            Assert.False(Directory.Exists(Path.GetFullPath(Path.Combine(paths.ManifestsDir, "../../outside"))));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
