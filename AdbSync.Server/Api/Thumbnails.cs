using SkiaSharp;

namespace AdbSync.Server.Api;

public static class Thumbnails
{
    /// <summary>A JPEG no larger than <paramref name="maxSize"/> on its long side, turned upright by its EXIF orientation. Null when the file isn't an image this can read.</summary>
    public static byte[]? RenderJpeg(string path, int maxSize)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null)
                return null;
            using var decoded = SKBitmap.Decode(codec);
            if (decoded is null)
                return null;
            using var upright = Orient(decoded, codec.EncodedOrigin);

            var scale = Math.Min(1.0, (double)maxSize / Math.Max(upright.Width, upright.Height));
            using var scaled = scale < 1.0
                ? upright.Resize(new SKImageInfo(Math.Max(1, (int)(upright.Width * scale)), Math.Max(1, (int)(upright.Height * scale))),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                : upright.Copy();
            if (scaled is null)
                return null;

            using var image = SKImage.FromBitmap(scaled);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
            return data?.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            return source.Copy();

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var w = source.Width;
        var h = source.Height;
        var result = new SKBitmap(swap ? h : w, swap ? w : h);
        using var canvas = new SKCanvas(result);
        canvas.SetMatrix(origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        });
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}
