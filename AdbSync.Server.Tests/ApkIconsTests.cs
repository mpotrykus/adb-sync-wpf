using AdbSync.Server.Api;
using SkiaSharp;

namespace AdbSync.Server.Tests;

public class ApkIconsTests
{
    [Fact]
    public void Pick_takes_the_highest_density_raster_icon()
    {
        const string badging = """
            package: name='com.example.app' versionCode='1'
            application-label:'Example'
            application-icon-120:'res/mipmap-ldpi-v4/ic_launcher.png'
            application-icon-160:'res/mipmap-mdpi-v4/ic_launcher.png'
            application-icon-480:'res/mipmap-xxhdpi-v4/ic_launcher.webp'
            application-icon-640:'res/mipmap-xxxhdpi-v4/ic_launcher.png'
            application: label='Example' icon='res/mipmap-mdpi-v4/ic_launcher.png'
            """;

        Assert.Equal("res/mipmap-xxxhdpi-v4/ic_launcher.png", ApkIcons.PickIconEntry(badging));
    }

    [Fact]
    public void Pick_skips_adaptive_xml_icons_in_favour_of_a_raster_one()
    {
        const string badging = """
            application-icon-160:'res/mipmap-mdpi-v4/ic_launcher.png'
            application-icon-640:'res/mipmap-anydpi-v26/ic_launcher.xml'
            application-icon-65534:'res/mipmap-anydpi-v26/ic_launcher.xml'
            application: label='X' icon='res/mipmap-anydpi-v26/ic_launcher.xml'
            """;

        Assert.Equal("res/mipmap-mdpi-v4/ic_launcher.png", ApkIcons.PickIconEntry(badging));
    }

    [Fact]
    public void Pick_returns_null_for_an_adaptive_only_app()
    {
        const string badging = """
            application-icon-160:'res/mipmap-anydpi-v26/ic_launcher.xml'
            application-icon-65534:'res/mipmap-anydpi-v26/ic_launcher.xml'
            application: label='AdbSync Companion' icon='res/mipmap-anydpi-v26/ic_launcher.xml'
            """;

        Assert.Null(ApkIcons.PickIconEntry(badging));
    }

    [Fact]
    public void Pick_falls_back_to_the_application_line_when_no_density_entries_exist()
    {
        const string badging = "application: label='Old' icon='res/drawable/icon.png'\n";

        Assert.Equal("res/drawable/icon.png", ApkIcons.PickIconEntry(badging));
    }

    [Fact]
    public void Pick_returns_null_when_there_is_no_icon_at_all()
    {
        Assert.Null(ApkIcons.PickIconEntry("package: name='x.y'\napplication: label='X' icon=''\n"));
        Assert.Null(ApkIcons.PickIconEntry(""));
    }

    [Theory]
    [InlineData("com.stephaneginier.nomad", true)]
    [InlineData("com.brakefield.infinitepainter", true)]
    [InlineData("org.example.app_2.beta", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("nodots", false)]
    [InlineData("com.example;rm -rf /", false)]
    [InlineData("com.example app", false)]
    [InlineData("com.example.$(id)", false)]
    [InlineData("com.example.'quoted'", false)]
    [InlineData("1com.example", false)]
    public void Package_names_are_checked_before_they_reach_a_shell_command(string? name, bool expected) =>
        Assert.Equal(expected, ApkIcons.IsValidPackageName(name));

    [Fact]
    public void Render_scales_a_wide_image_to_a_square_png_without_stretching_it()
    {
        using var source = new SKBitmap(256, 64);
        source.Erase(SKColors.Red);
        using var stream = Encode(source);

        var png = ApkIcons.RenderIconPng(stream);

        Assert.NotNull(png);
        using var rendered = SKBitmap.Decode(png!);
        Assert.Equal(ApkIcons.IconSize, rendered.Width);
        Assert.Equal(ApkIcons.IconSize, rendered.Height);
        Assert.Equal(SKColors.Red, rendered.GetPixel(ApkIcons.IconSize / 2, ApkIcons.IconSize / 2));
        Assert.Equal(0, rendered.GetPixel(ApkIcons.IconSize / 2, 2).Alpha);
    }

    [Fact]
    public void Render_returns_null_for_data_that_is_not_an_image()
    {
        using var stream = new MemoryStream("<adaptive-icon/>"u8.ToArray());

        Assert.Null(ApkIcons.RenderIconPng(stream));
    }

    private const string NomadIconXml = """
        N: android=http://schemas.android.com/apk/res/android
          E: adaptive-icon (line=2)
            E: background (line=3)
              A: android:drawable(0x01010199)=@0x7f050040
            E: foreground (line=4)
              A: android:drawable(0x01010199)=@0x7f0c0001
            E: monochrome (line=5)
              A: android:drawable(0x01010199)=@0x7f0c0001
        """;

    private const string NomadResources = """
              spec resource 0x7f050040 com.stephaneginier.nomad:color/ic_launcher_background: flags=0x00000000
            config (default):
              resource 0x7f050040 com.stephaneginier.nomad:color/ic_launcher_background: t=0x1d d=0xffdcc4ad (s=0x0008 r=0x00)
                (color) #ffdcc4ad
              resource 0x7f050041 com.stephaneginier.nomad:color/material_blue_grey_800: t=0x1c d=0xff37474f (s=0x0008 r=0x00)
                (color) #ff37474f
              spec resource 0x7f0c0001 com.stephaneginier.nomad:mipmap/ic_launcher_foreground: flags=0x00000100
              config mdpi:
                resource 0x7f0c0000 com.stephaneginier.nomad:mipmap/ic_launcher: t=0x03 d=0x000000c4 (s=0x0008 r=0x00)
                  (string8) "res/mipmap-mdpi-v4/ic_launcher.png"
                resource 0x7f0c0001 com.stephaneginier.nomad:mipmap/ic_launcher_foreground: t=0x03 d=0x000000c5 (s=0x0008 r=0x00)
                  (string8) "res/mipmap-mdpi-v4/ic_launcher_foreground.png"
              config xxxhdpi:
                resource 0x7f0c0000 com.stephaneginier.nomad:mipmap/ic_launcher: t=0x03 d=0x000000cd (s=0x0008 r=0x00)
                  (string8) "res/mipmap-xxxhdpi-v4/ic_launcher.png"
                resource 0x7f0c0001 com.stephaneginier.nomad:mipmap/ic_launcher_foreground: t=0x03 d=0x000000ce (s=0x0008 r=0x00)
                  (string8) "res/mipmap-xxxhdpi-v4/ic_launcher_foreground.png"
              config xhdpi:
                resource 0x7f0c0001 com.stephaneginier.nomad:mipmap/ic_launcher_foreground: t=0x03 d=0x000000ca (s=0x0008 r=0x00)
                  (string8) "res/mipmap-xhdpi-v4/ic_launcher_foreground.png"
              config anydpi flags=0x01 [sparse]:
                resource 0x7f0c0000 com.stephaneginier.nomad:mipmap/ic_launcher: t=0x03 d=0x000000cf (s=0x0008 r=0x00)
                  (string8) "res/mipmap-anydpi-v26/ic_launcher.xml"
        """;

    [Fact]
    public void Adaptive_icon_layers_are_read_from_the_icon_xml()
    {
        var (background, foreground) = ApkIcons.ParseAdaptiveIcon(NomadIconXml);

        Assert.Equal("0x7f050040", background);
        Assert.Equal("0x7f0c0001", foreground);
    }

    [Fact]
    public void Adaptive_icon_layers_accept_inline_colors_and_ignore_nested_drawables()
    {
        const string xml = """
            E: adaptive-icon (line=2)
              E: background (line=3)
                A: android:drawable(0x01010199)=(type 0x1c)0xff202020
              E: foreground (line=4)
                E: inset (line=5)
                  A: android:drawable(0x01010199)=@0x7f0c0009
            """;

        var (background, foreground) = ApkIcons.ParseAdaptiveIcon(xml);

        Assert.Equal("#ff202020", background);
        Assert.Null(foreground);
    }

    [Fact]
    public void A_color_layer_resolves_to_its_color()
    {
        var layer = ApkIcons.ResolveLayer("0x7f050040", NomadResources);

        Assert.NotNull(layer);
        Assert.Null(layer!.Path);
        Assert.Equal(0xffdcc4adu, layer.ArgbColor);
    }

    [Fact]
    public void A_file_layer_resolves_to_its_highest_density_raster_file()
    {
        var layer = ApkIcons.ResolveLayer("0x7f0c0001", NomadResources);

        Assert.Equal("res/mipmap-xxxhdpi-v4/ic_launcher_foreground.png", layer?.Path);
        Assert.Null(layer?.ArgbColor);
    }

    [Fact]
    public void An_unknown_layer_does_not_resolve()
    {
        Assert.Null(ApkIcons.ResolveLayer("0x7f0c00ff", NomadResources));
    }

    [Fact]
    public void Inline_colors_resolve_without_a_resource_table()
    {
        Assert.Equal(0xff202020u, ApkIcons.ResolveLayer("#ff202020", "")?.ArgbColor);
    }

    [Fact]
    public void The_first_solid_color_of_a_vector_background_is_found()
    {
        const string vector = """
            E: vector (line=2)
              E: path (line=9)
                A: android:fillColor(0x01010404)=(type 0x1c)0xff3ddc84
            """;

        Assert.Equal(0xff3ddc84u, ApkIcons.ParseFirstColor(vector));
        Assert.Null(ApkIcons.ParseFirstColor("E: vector (line=2)\n"));
    }

    [Fact]
    public void Any_icon_entry_is_found_even_when_it_is_adaptive_xml()
    {
        const string badging = """
            application-icon-160:'res/mipmap-anydpi-v26/ic_launcher.xml'
            application: label='Nomad Sculpt' icon='res/mipmap-anydpi-v26/ic_launcher.xml' banner='res/mipmap-anydpi-v26/ic_launcher.xml'
            """;

        Assert.Equal("res/mipmap-anydpi-v26/ic_launcher.xml", ApkIcons.PickAnyIconEntry(badging));
        Assert.Null(ApkIcons.PickIconEntry(badging));
    }

    [Fact]
    public void Compose_draws_the_foreground_over_the_background_inside_a_rounded_square()
    {
        using var foreground = new SKBitmap(432, 432);
        foreground.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(foreground))
            canvas.DrawRect(new SKRect(176, 176, 256, 256), new SKPaint { Color = SKColors.Red });

        var png = ApkIcons.ComposeAdaptiveIconPng(null, 0xffdcc4adu, foreground);

        Assert.NotNull(png);
        using var rendered = SKBitmap.Decode(png!);
        Assert.Equal(ApkIcons.IconSize, rendered.Width);
        Assert.Equal(SKColors.Red, rendered.GetPixel(ApkIcons.IconSize / 2, ApkIcons.IconSize / 2));
        Assert.Equal(new SKColor(0xdc, 0xc4, 0xad), rendered.GetPixel(ApkIcons.IconSize / 2, 20));
        Assert.Equal(0, rendered.GetPixel(0, 0).Alpha);
    }

    private static MemoryStream Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new MemoryStream(data.ToArray());
    }
}
