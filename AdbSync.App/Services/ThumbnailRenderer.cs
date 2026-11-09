using System.IO;
using System.Windows.Media.Imaging;

namespace AdbSync.App.Services;

public static class ThumbnailRenderer
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// <param name="fileName">The name to judge the type by when <paramref name="path"/> is a stored copy with a different extension.</param>
    public static BitmapSource? Load(string path, int maxPixels, string? fileName = null)
    {
        if (!IsImage(fileName ?? path))
            return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = maxPixels;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static byte[]? RenderJpeg(string path, int maxPixels, string? fileName = null)
    {
        var bitmap = Load(path, maxPixels, fileName);
        if (bitmap is null)
            return null;
        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
