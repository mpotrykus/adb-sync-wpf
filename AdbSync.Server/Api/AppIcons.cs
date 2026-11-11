using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Transfer;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using SkiaSharp;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace AdbSync.Server.Api;

/// <summary>The pure parts of turning an APK into a launcher icon: picking the icon entry from <c>aapt dump badging</c> and rendering it.</summary>
public static partial class ApkIcons
{
    public const int IconSize = 128;

    private static readonly string[] RasterExtensions = [".png", ".webp", ".jpg", ".jpeg"];

    [GeneratedRegex(@"^application-icon-(?<density>\d+):'(?<path>[^']+)'", RegexOptions.Multiline)]
    private static partial Regex DensityIconPattern();

    [GeneratedRegex(@"^application:.*?\sicon='(?<path>[^']*)'", RegexOptions.Multiline)]
    private static partial Regex DefaultIconPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex PackageNamePattern();

    /// <summary>Package names go into a device shell command, so only the characters Android allows in one are accepted.</summary>
    public static bool IsValidPackageName(string? name) => name is { Length: > 0 and <= 255 } && PackageNamePattern().IsMatch(name);

    /// <summary>
    /// The highest-density raster launcher icon listed by <c>aapt dump badging</c>. Adaptive icons are XML and can't be drawn
    /// from the file alone, so apps that ship only those have no pick (null) and the UI keeps its generated tile.
    /// </summary>
    public static string? PickIconEntry(string badging)
    {
        var best = DensityIconPattern().Matches(badging)
            .Select(m => (Density: long.Parse(m.Groups["density"].Value), Path: m.Groups["path"].Value))
            .Where(e => IsRaster(e.Path))
            .OrderByDescending(e => e.Density)
            .Select(e => e.Path)
            .FirstOrDefault();
        if (best is not null)
            return best;

        var fallback = DefaultIconPattern().Match(badging);
        return fallback.Success && IsRaster(fallback.Groups["path"].Value) ? fallback.Groups["path"].Value : null;
    }

    internal static bool IsRaster(string path) =>
        RasterExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>A square PNG of <paramref name="size"/> pixels, scaled to fit. Null when the stream isn't an image this can read.</summary>
    public static byte[]? RenderIconPng(Stream image, int size = IconSize)
    {
        try
        {
            using var decoded = SKBitmap.Decode(image);
            if (decoded is null || decoded.Width == 0 || decoded.Height == 0)
                return null;

            using var surface = new SKBitmap(size, size);
            using (var canvas = new SKCanvas(surface))
            {
                canvas.Clear(SKColors.Transparent);
                var scale = Math.Min((float)size / decoded.Width, (float)size / decoded.Height);
                var w = decoded.Width * scale;
                var h = decoded.Height * scale;
                using var paint = new SKPaint { IsAntialias = true };
                using var source = SKImage.FromBitmap(decoded);
                canvas.DrawImage(source, new SKRect((size - w) / 2, (size - h) / 2, (size + w) / 2, (size + h) / 2),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            }

            using var encoded = SKImage.FromBitmap(surface);
            using var data = encoded.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\s*A: android:drawable\([^)]*\)=(?<value>.+?)\s*$")]
    private static partial Regex DrawableAttributePattern();

    [GeneratedRegex(@"^\(type 0x1[c-f]\)0x(?<hex>[0-9a-fA-F]{8})$")]
    private static partial Regex InlineColorPattern();

    [GeneratedRegex(@"android:(?:fillColor|color)\([^)]*\)=\(type 0x1[c-f]\)0x(?<hex>[0-9a-fA-F]{8})")]
    private static partial Regex FillColorPattern();

    [GeneratedRegex(@"^\s*resource (?<id>0x[0-9a-fA-F]{8}) ")]
    private static partial Regex ResourceLinePattern();

    [GeneratedRegex(@"^\s*config (?<config>\(default\)|[A-Za-z0-9_+\-]+)")]
    private static partial Regex ConfigLinePattern();

    [GeneratedRegex(@"^\s*\(string8\) ""(?<path>[^""]+)""")]
    private static partial Regex StringValuePattern();

    [GeneratedRegex(@"^\s*\(color\) #(?<hex>[0-9a-fA-F]{6,8})")]
    private static partial Regex ColorValuePattern();

    /// <summary>The icon file the manifest names, of any kind - adaptive icons are XML, which <see cref="PickIconEntry"/> skips.</summary>
    public static string? PickAnyIconEntry(string badging)
    {
        var fallback = DefaultIconPattern().Match(badging);
        if (fallback.Success && fallback.Groups["path"].Value is { Length: > 0 } declared)
            return declared;
        return DensityIconPattern().Matches(badging)
            .Select(m => (Density: long.Parse(m.Groups["density"].Value), Path: m.Groups["path"].Value))
            .OrderByDescending(e => e.Density)
            .Select(e => e.Path)
            .FirstOrDefault();
    }

    /// <summary>
    /// The background and foreground drawables of an adaptive icon, from <c>aapt dump xmltree</c> of its XML. Each is a resource
    /// reference (<c>@0x7f0c0001</c>) or an inline color (<c>#ffdcc4ad</c>); null when the layer is missing or isn't a plain drawable
    /// reference (for example an inline vector), which this can't draw.
    /// </summary>
    public static (string? Background, string? Foreground) ParseAdaptiveIcon(string xmltree)
    {
        string? current = null;
        string? background = null;
        string? foreground = null;
        foreach (var line in xmltree.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("E: ", StringComparison.Ordinal))
            {
                var name = trimmed[3..].Split(' ')[0];
                current = name is "background" or "foreground" ? name : (name == "adaptive-icon" ? null : "other");
                continue;
            }
            if (current is null or "other" || DrawableAttributePattern().Match(line) is not { Success: true } match)
                continue;

            var value = match.Groups["value"].Value;
            var color = InlineColorPattern().Match(value);
            var layer = color.Success ? "#" + color.Groups["hex"].Value : value.StartsWith("@0x", StringComparison.Ordinal) ? value[1..] : null;
            if (current == "background")
                background ??= layer;
            else
                foreground ??= layer;
        }
        return (background, foreground);
    }

    /// <summary>A layer resolved to a drawable file inside the APK or to a plain color.</summary>
    public sealed record IconLayer(string? Path, uint? ArgbColor);

    /// <summary>
    /// Resolves a layer from <see cref="ParseAdaptiveIcon"/> against <c>aapt dump --values resources</c>: a color is returned as is, a
    /// file reference becomes the highest-density raster file (or an XML drawable when that's all there is).
    /// </summary>
    public static IconLayer? ResolveLayer(string layer, string resourceDump)
    {
        if (layer.StartsWith('#'))
            return ParseColor(layer[1..]) is { } inline ? new IconLayer(null, inline) : null;

        var id = layer;
        var density = "";
        var found = new List<(int Score, string Path)>();
        string? xmlPath = null;
        uint? color = null;
        var inResource = false;
        foreach (var line in resourceDump.Split('\n'))
        {
            if (ConfigLinePattern().Match(line) is { Success: true } config)
            {
                density = config.Groups["config"].Value;
                inResource = false;
                continue;
            }
            if (ResourceLinePattern().Match(line) is { Success: true } resource)
            {
                inResource = string.Equals(resource.Groups["id"].Value, id, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inResource)
                continue;
            if (ColorValuePattern().Match(line) is { Success: true } c && ParseColor(c.Groups["hex"].Value) is { } parsed)
            {
                color ??= parsed;
            }
            else if (StringValuePattern().Match(line) is { Success: true } s)
            {
                var path = s.Groups["path"].Value;
                if (IsRaster(path))
                    found.Add((DensityScore(density), path));
                else if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    xmlPath ??= path;
            }
        }

        if (color is not null)
            return new IconLayer(null, color);
        if (found.Count > 0)
            return new IconLayer(found.OrderByDescending(f => f.Score).First().Path, null);
        return xmlPath is null ? null : new IconLayer(xmlPath, null);
    }

    [GeneratedRegex(@"=@(?<id>0x[0-9a-fA-F]{8})")]
    private static partial Regex ResourceReferencePattern();

    /// <summary>The distinct resource ids a drawable's XML refers to (<c>=@0x7f080006</c>), in order of appearance.</summary>
    public static IReadOnlyList<string> ReferencedResourceIds(string xmltree) =>
        ResourceReferencePattern().Matches(xmltree).Select(m => m.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The first solid color in a drawable's XML (<c>aapt dump xmltree</c>), for flat vector or shape backgrounds.</summary>
    public static uint? ParseFirstColor(string xmltree) =>
        FillColorPattern().Match(xmltree) is { Success: true } m ? ParseColor(m.Groups["hex"].Value) : null;

    private static uint? ParseColor(string hex)
    {
        if (hex.Length == 6)
            hex = "ff" + hex;
        return hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var argb) ? argb : null;
    }

    private static int DensityScore(string config)
    {
        if (config.StartsWith("xxxhdpi", StringComparison.Ordinal)) return 640;
        if (config.StartsWith("xxhdpi", StringComparison.Ordinal)) return 480;
        if (config.StartsWith("xhdpi", StringComparison.Ordinal)) return 320;
        if (config.StartsWith("hdpi", StringComparison.Ordinal)) return 240;
        if (config.StartsWith("tvdpi", StringComparison.Ordinal)) return 213;
        if (config.StartsWith("mdpi", StringComparison.Ordinal)) return 160;
        if (config.StartsWith("ldpi", StringComparison.Ordinal)) return 120;
        return 100;
    }

    /// <summary>
    /// An adaptive icon the way a launcher shows it: the layers fill a 108-unit canvas and only the centre 72 units are visible, drawn
    /// inside a rounded square. Either a background bitmap or a background color may be given; with neither the foreground sits on transparency.
    /// </summary>
    public static byte[]? ComposeAdaptiveIconPng(SKBitmap? background, uint? backgroundColor, SKBitmap foreground, int size = IconSize)
    {
        try
        {
            using var surface = new SKBitmap(size, size);
            using (var canvas = new SKCanvas(surface))
            {
                canvas.Clear(SKColors.Transparent);
                var visible = size * 108f / 72f;
                var offset = -(visible - size) / 2f;
                var canvasRect = new SKRect(offset, offset, offset + visible, offset + visible);
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

                canvas.ClipRoundRect(new SKRoundRect(new SKRect(0, 0, size, size), size * 0.22f), SKClipOperation.Intersect, true);
                if (background is not null)
                {
                    using var image = SKImage.FromBitmap(background);
                    canvas.DrawImage(image, canvasRect, sampling);
                }
                else if (backgroundColor is { } argb)
                {
                    canvas.Clear(new SKColor(argb));
                }
                using var foregroundImage = SKImage.FromBitmap(foreground);
                canvas.DrawImage(foregroundImage, canvasRect, sampling);
            }

            using var encoded = SKImage.FromBitmap(surface);
            using var data = encoded.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>ADBSYNC_AAPT_PATH, else aapt or aapt2 from PATH or the usual Linux locations. Null when none is installed.</summary>
    public static string? ResolveAapt()
    {
        if (Environment.GetEnvironmentVariable("ADBSYNC_AAPT_PATH") is { Length: > 0 } configured)
            return configured;

        var extension = OperatingSystem.IsWindows() ? ".exe" : "";
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/usr/bin", "/usr/local/bin"]);
        foreach (var name in new[] { "aapt", "aapt2" })
        {
            foreach (var directory in directories)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }
}

public sealed record AppIconFetch(bool PackageFound, byte[]? Png, bool Cacheable);

/// <summary>
/// Launcher icons for the apps jobs are linked to. The icon isn't available from adb directly, so the first time one is asked for
/// the app's base APK is pulled from a phone, <c>aapt</c> names the icon inside it, and a 128px PNG is cached under the data
/// directory. Apps with no usable icon get a short-lived "none" marker so the phones aren't pulled from on every page load.
/// </summary>
public sealed class AppIconService(IAdbClient adb, IRemoteFileSystemFactory files, AppPaths paths, ILogger<AppIconService> logger)
{
    private const long MaxApkBytes = 400L * 1024 * 1024;
    private static readonly TimeSpan NegativeCacheLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan AaptTimeout = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private bool _warnedAboutMissingAapt;

    private string CacheDir => Path.Combine(paths.Root, "app-icons");
    private string IconFile(string package) => Path.Combine(CacheDir, package + ".png");
    private string MissFile(string package) => Path.Combine(CacheDir, package + ".none");

    public byte[]? TryGetCached(string package)
    {
        var file = IconFile(package);
        return File.Exists(file) ? File.ReadAllBytes(file) : null;
    }

    /// <summary>The marker carries a version so a smarter extractor isn't held back by "none" results an older one wrote.</summary>
    private const string MissMarker = "v3 ";

    public bool IsKnownMissing(string package)
    {
        var file = MissFile(package);
        return File.Exists(file)
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < NegativeCacheLifetime
            && File.ReadAllText(file).StartsWith(MissMarker, StringComparison.Ordinal);
    }

    public async Task<AppIconFetch> FetchAsync(string package, string serial, CancellationToken ct)
    {
        if (!ApkIcons.IsValidPackageName(package))
            return new AppIconFetch(false, null, false);

        await _fetchLock.WaitAsync(ct);
        try
        {
            if (TryGetCached(package) is { } already)
                return new AppIconFetch(true, already, true);
            if (IsKnownMissing(package))
                return new AppIconFetch(true, null, true);

            var device = new DeviceData { Serial = serial, State = DeviceState.Online };
            var apkPath = await FindBaseApkAsync(package, device, ct);
            if (apkPath is null)
                return new AppIconFetch(false, null, false);

            if (await ApkSizeAsync(apkPath, device, ct) is { } size && size > MaxApkBytes)
            {
                logger.LogInformation("App icon: {Package} is {Size} bytes, too large to pull for its icon.", package, size);
                return Remember(package, null);
            }

            var apk = Path.Combine(Path.GetTempPath(), $"adbsync-icon-{Guid.NewGuid():N}.apk");
            try
            {
                var remote = files.Create(serial);
                await using var connection = remote as IAsyncDisposable;
                await remote.PullFileAsync(apkPath, apk, ct);

                var aapt = ApkIcons.ResolveAapt();
                if (aapt is null)
                {
                    if (!_warnedAboutMissingAapt)
                    {
                        _warnedAboutMissingAapt = true;
                        logger.LogWarning("App icons need aapt (apt install aapt, or set ADBSYNC_AAPT_PATH); none was found.");
                    }
                    return new AppIconFetch(true, null, false);
                }

                var badging = await RunAaptAsync(aapt, ["dump", "badging", apk], ct);
                byte[]? png = null;
                string? declared = null;
                if (badging is not null)
                {
                    if (ApkIcons.PickIconEntry(badging) is { } raster)
                        png = ReadIcon(apk, raster);
                    declared = ApkIcons.PickAnyIconEntry(badging);
                    if (png is null && declared is not null && declared.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        png = await TryAdaptiveAsync(aapt, apk, declared, ct);
                }
                if (png is null)
                    logger.LogInformation("App icon: couldn't make an icon for {Package} (icon entry: {Entry}).", package, declared ?? "none");
                return Remember(package, png);
            }
            finally
            {
                try { File.Delete(apk); } catch (IOException) { }
            }
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private AppIconFetch Remember(string package, byte[]? png)
    {
        Directory.CreateDirectory(CacheDir);
        if (png is not null)
        {
            File.WriteAllBytes(IconFile(package), png);
            if (File.Exists(MissFile(package)))
                File.Delete(MissFile(package));
        }
        else
        {
            File.WriteAllText(MissFile(package), MissMarker + DateTime.UtcNow.ToString("O"));
        }
        return new AppIconFetch(true, png, true);
    }

    /// <summary>Adaptive icons: both layers are resolved to files or colors and composed the way a launcher draws them. Null when a layer can't be drawn (for example a vector foreground).</summary>
    private async Task<byte[]?> TryAdaptiveAsync(string aapt, string apk, string iconXml, CancellationToken ct)
    {
        if (string.Equals(Path.GetFileNameWithoutExtension(aapt), "aapt2", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("App icon: adaptive icons need aapt (v1); {Aapt} is aapt2.", aapt);
            return null;
        }
        if (await RunAaptAsync(aapt, ["dump", "xmltree", apk, iconXml], ct) is not { } tree)
            return null;
        var (backgroundRef, foregroundRef) = ApkIcons.ParseAdaptiveIcon(tree);
        if (foregroundRef is null)
            return null;

        string? values = null;
        async Task EnsureValuesAsync() => values ??= await RunAaptAsync(aapt, ["dump", "--values", "resources", apk], ct) ?? "";

        async Task<ApkIcons.IconLayer?> ResolveAsync(string layer)
        {
            if (layer.StartsWith('#'))
                return ApkIcons.ResolveLayer(layer, "");
            await EnsureValuesAsync();
            return ApkIcons.ResolveLayer(layer, values!);
        }

        async Task<SKBitmap?> LoadBitmapAsync(ApkIcons.IconLayer layer)
        {
            if (layer.Path is not { } path)
                return null;
            if (ApkIcons.IsRaster(path))
                return ReadBitmap(apk, path);
            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || await RunAaptAsync(aapt, ["dump", "xmltree", apk, path], ct) is not { } xml)
                return null;
            var fills = new Dictionary<string, VectorDrawables.VectorFill>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ApkIcons.ReferencedResourceIds(xml))
            {
                await EnsureValuesAsync();
                if (ApkIcons.ResolveLayer(id, values!) is not { } fillLayer)
                    continue;
                if (fillLayer.ArgbColor is { } argb)
                    fills[id] = new VectorDrawables.VectorFill(argb);
                else if (fillLayer.Path is { } fillPath && fillPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    && await RunAaptAsync(aapt, ["dump", "xmltree", apk, fillPath], ct) is { } gradient)
                    fills[id] = new VectorDrawables.VectorFill(null, gradient);
            }
            return VectorDrawables.Render(xml, id => fills.GetValueOrDefault(id), VectorLayerPixels);
        }

        if (await ResolveAsync(foregroundRef) is not { } foregroundLayer)
            return null;
        using var foreground = await LoadBitmapAsync(foregroundLayer);
        if (foreground is null)
            return null;

        SKBitmap? background = null;
        uint? backgroundColor = null;
        try
        {
            if (backgroundRef is not null && await ResolveAsync(backgroundRef) is { } layer)
            {
                if (layer.ArgbColor is { } color)
                    backgroundColor = color;
                else
                {
                    background = await LoadBitmapAsync(layer);
                    if (background is null && layer.Path is { } xml && await RunAaptAsync(aapt, ["dump", "xmltree", apk, xml], ct) is { } xmlTree)
                        backgroundColor = ApkIcons.ParseFirstColor(xmlTree);
                }
            }
            return ApkIcons.ComposeAdaptiveIconPng(background, backgroundColor, foreground);
        }
        finally
        {
            background?.Dispose();
        }
    }

    private const int VectorLayerPixels = 432;

    private static SKBitmap? ReadBitmap(string apk, string entryName)
    {
        using var archive = ZipFile.OpenRead(apk);
        if (archive.GetEntry(entryName) is not { } entry)
            return null;
        using var stream = entry.Open();
        using var buffered = new MemoryStream();
        stream.CopyTo(buffered);
        buffered.Position = 0;
        return SKBitmap.Decode(buffered);
    }

    private static byte[]? ReadIcon(string apk, string entryName)
    {
        using var archive = ZipFile.OpenRead(apk);
        if (archive.GetEntry(entryName) is not { } entry)
            return null;
        using var stream = entry.Open();
        using var buffered = new MemoryStream();
        stream.CopyTo(buffered);
        buffered.Position = 0;
        return ApkIcons.RenderIconPng(buffered);
    }

    private async Task<string?> FindBaseApkAsync(string package, DeviceData device, CancellationToken ct)
    {
        var receiver = new ConsoleOutputReceiver();
        await adb.ExecuteRemoteCommandAsync($"pm path {package}", device, receiver, Encoding.UTF8, ct);
        var paths = receiver.ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("package:", StringComparison.Ordinal))
            .Select(line => line["package:".Length..].Trim())
            .Where(path => path.Length > 0 && !path.Contains('\''))
            .ToList();
        return paths.FirstOrDefault(p => p.EndsWith("/base.apk", StringComparison.Ordinal)) ?? paths.FirstOrDefault();
    }

    private async Task<long?> ApkSizeAsync(string apkPath, DeviceData device, CancellationToken ct)
    {
        try
        {
            var receiver = new ConsoleOutputReceiver();
            await adb.ExecuteRemoteCommandAsync($"stat -c %s '{apkPath}'", device, receiver, Encoding.UTF8, ct);
            return long.TryParse(receiver.ToString().Trim(), out var size) ? size : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string?> RunAaptAsync(string aapt, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(aapt) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start);
        if (process is null)
            return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AaptTimeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? await output : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            return null;
        }
    }
}
