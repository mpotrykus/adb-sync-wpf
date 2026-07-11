using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Transfer;

namespace AdbSync.Core.Tests.Transfer;

public class ExcludeMatcherTests
{
    [Theory]
    [InlineData("Cache", false)]
    [InlineData("nested/Cache", false)]
    [InlineData("deeply/nested/Cache", false)]
    public void BareNamePattern_MatchesAtAnyDepth(string path, bool isDirectory)
    {
        var matcher = new ExcludeMatcher(["Cache"]);

        Assert.True(matcher.IsExcluded(path, isDirectory));
    }

    [Fact]
    public void BareNamePattern_DoesNotMatchDifferentName()
    {
        var matcher = new ExcludeMatcher(["Cache"]);

        Assert.False(matcher.IsExcluded("Caches", false));
        Assert.False(matcher.IsExcluded("data/OtherFolder", false));
    }

    [Fact]
    public void PathShapedPattern_OnlyMatchesAnchoredSubPath()
    {
        var matcher = new ExcludeMatcher(["Painter/Cache"]);

        Assert.True(matcher.IsExcluded("Painter/Cache", true));
        Assert.True(matcher.IsExcluded("Painter/Cache/thumb.png", false));
        Assert.False(matcher.IsExcluded("Cache", true));
        Assert.False(matcher.IsExcluded("Other/Painter/Cache", true));
    }

    [Fact]
    public void NoPatterns_ExcludesNothing()
    {
        var matcher = new ExcludeMatcher([]);

        Assert.False(matcher.IsExcluded("anything/at/all", false));
    }

    [Fact]
    public void BackslashesAndTrailingSlashes_AreNormalized()
    {
        var matcher = new ExcludeMatcher(["Painter\\Cache\\"]);

        Assert.True(matcher.IsExcluded("Painter/Cache", true));
    }
}
