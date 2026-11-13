using AdbSync.Server.Api;
using SkiaSharp;

namespace AdbSync.Server.Tests;

public class ThumbnailsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("thumbs").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(string name, SKBitmap bitmap, SKEncodedImageFormat format)
    {
        var path = Path.Combine(_dir, name);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static SKBitmap Marked(int w, int h)
    {
        var bitmap = new SKBitmap(w, h);
        bitmap.Erase(SKColors.Blue);
        bitmap.SetPixel(0, 0, SKColors.Red);
        return bitmap;
    }

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, 0, 0, 4, 2)]
    [InlineData(SKEncodedOrigin.TopRight, 3, 0, 4, 2)]
    [InlineData(SKEncodedOrigin.BottomRight, 3, 1, 4, 2)]
    [InlineData(SKEncodedOrigin.BottomLeft, 0, 1, 4, 2)]
    [InlineData(SKEncodedOrigin.LeftTop, 0, 0, 2, 4)]
    [InlineData(SKEncodedOrigin.RightTop, 1, 0, 2, 4)]
    [InlineData(SKEncodedOrigin.RightBottom, 1, 3, 2, 4)]
    [InlineData(SKEncodedOrigin.LeftBottom, 0, 3, 2, 4)]
    public void Orient_PutsTheFirstStoredPixelWhereExifSaysItBelongs(SKEncodedOrigin origin, int redX, int redY, int width, int height)
    {
        using var source = Marked(4, 2);
        using var result = Thumbnails.Orient(source, origin);

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(redX, redY));
        var reds = Enumerable.Range(0, width).SelectMany(x => Enumerable.Range(0, height).Select(y => (x, y)))
            .Count(p => result.GetPixel(p.x, p.y) == SKColors.Red);
        Assert.Equal(1, reds);
    }

    [Fact]
    public void RenderJpeg_ShrinksALargeImageToTheLongSide()
    {
        using var source = Marked(400, 200);
        var path = Write("wide.png", source, SKEncodedImageFormat.Png);

        var jpeg = Thumbnails.RenderJpeg(path, 100);

        Assert.NotNull(jpeg);
        using var decoded = SKBitmap.Decode(jpeg);
        Assert.Equal(100, decoded.Width);
        Assert.Equal(50, decoded.Height);
    }

    [Fact]
    public void RenderJpeg_DoesNotEnlargeASmallImage()
    {
        using var source = Marked(40, 20);
        var path = Write("small.png", source, SKEncodedImageFormat.Png);

        using var decoded = SKBitmap.Decode(Thumbnails.RenderJpeg(path, 320));

        Assert.Equal(40, decoded.Width);
        Assert.Equal(20, decoded.Height);
    }

    [Fact]
    public void RenderJpeg_ReturnsNullForAFileThatIsNotAnImage()
    {
        var path = Path.Combine(_dir, "notes.jpg");
        File.WriteAllText(path, "this is not an image");

        Assert.Null(Thumbnails.RenderJpeg(path, 320));
    }

    [Fact]
    public void RenderJpeg_ReturnsNullForAMissingFile() =>
        Assert.Null(Thumbnails.RenderJpeg(Path.Combine(_dir, "missing.png"), 320));
}
