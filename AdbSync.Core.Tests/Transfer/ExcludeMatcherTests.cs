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

    [Theory]
    [InlineData("thumb.tmp", false)]
    [InlineData("nested/deep/thumb.tmp", false)]
    public void SegmentGlobPattern_MatchesAtAnyDepth(string path, bool isDirectory)
    {
        var matcher = new ExcludeMatcher(["*.tmp"]);

        Assert.True(matcher.IsExcluded(path, isDirectory));
    }

    [Fact]
    public void SegmentGlobPattern_DoesNotMatchDifferentExtension()
    {
        var matcher = new ExcludeMatcher(["*.tmp"]);

        Assert.False(matcher.IsExcluded("thumb.tmp2", false));
        Assert.False(matcher.IsExcluded("thumb.json", false));
    }

    [Fact]
    public void SegmentGlobPattern_QuestionMarkMatchesExactlyOneCharacter()
    {
        var matcher = new ExcludeMatcher(["queued_?.mp4"]);

        Assert.True(matcher.IsExcluded("Projects/1/playback/queued_1.mp4", false));
        Assert.False(matcher.IsExcluded("Projects/1/playback/queued_12.mp4", false));
    }

    [Fact]
    public void PathShapedGlobPattern_IsAnchoredAndMatchesEverythingUnderIt()
    {
        var matcher = new ExcludeMatcher(["*/.thumbnails/*"]);

        Assert.True(matcher.IsExcluded("Painter/.thumbnails/thumb.png", false));
        Assert.True(matcher.IsExcluded("Painter/.thumbnails/nested/thumb.png", false));
        Assert.False(matcher.IsExcluded("Painter/Sub/.thumbnails/thumb.png", false));
    }

    [Fact]
    public void GlobPattern_LiteralDotIsNotTreatedAsWildcard()
    {
        var matcher = new ExcludeMatcher(["*.pntr_archive.zip"]);

        Assert.False(matcher.IsExcluded("1234.pntrXarchive.zip", false));
        Assert.True(matcher.IsExcluded("1234.pntr_archive.zip", false));
    }
}
