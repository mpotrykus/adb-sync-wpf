using AdbSync.Server.Api;
using SkiaSharp;

namespace AdbSync.Server.Tests;

public class VectorDrawablesTests
{
    private static string Vector(string body) => $"""
        N: android=http://schemas.android.com/apk/res/android
          E: vector (line=7)
            A: android:name(0x01010003)="vector" (Raw: "vector")
            A: android:viewportWidth(0x01010402)=(type 0x4)0x41200000
            A: android:viewportHeight(0x01010403)=(type 0x4)0x41200000
        {body}
        """;

    private const string RedSquare = """
              E: path (line=11)
                A: android:fillColor(0x01010404)=(type 0x1d)0xffff0000
                A: android:pathData(0x01010405)="M 2 2 L 8 2 L 8 8 L 2 8 Z" (Raw: "M 2 2 L 8 2 L 8 8 L 2 8 Z")
        """;

    private static VectorDrawables.VectorFill? NoResources(string id) => null;

    [Fact]
    public void A_filled_path_is_scaled_from_the_viewport_to_the_bitmap()
    {
        using var bitmap = VectorDrawables.Render(Vector(RedSquare), NoResources, 100);

        Assert.NotNull(bitmap);
        Assert.Equal(100, bitmap!.Width);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(50, 50));
        Assert.Equal(0, bitmap.GetPixel(5, 5).Alpha);
    }

    [Fact]
    public void A_color_resource_reference_is_resolved()
    {
        const string body = """
                  E: path (line=11)
                    A: android:fillColor(0x01010404)=@0x7f080006
                    A: android:pathData(0x01010405)="M 0 0 L 10 0 L 10 10 L 0 10 Z" (Raw: "M 0 0 L 10 0 L 10 10 L 0 10 Z")
            """;

        using var bitmap = VectorDrawables.Render(Vector(body), id => id == "0x7f080006" ? new VectorDrawables.VectorFill(0xff3366cc) : null, 100);

        Assert.NotNull(bitmap);
        Assert.Equal(new SKColor(0x33, 0x66, 0xcc), bitmap!.GetPixel(50, 50));
    }

    [Fact]
    public void Fill_alpha_makes_the_fill_translucent()
    {
        const string body = """
                  E: path (line=11)
                    A: android:fillColor(0x01010404)=(type 0x1d)0xffffffff
                    A: android:pathData(0x01010405)="M 0 0 L 10 0 L 10 10 L 0 10 Z" (Raw: "M 0 0 L 10 0 L 10 10 L 0 10 Z")
                    A: android:fillAlpha(0x010104cc)=(type 0x4)0x3f000000
            """;

        using var bitmap = VectorDrawables.Render(Vector(body), NoResources, 100);

        Assert.NotNull(bitmap);
        Assert.InRange((int)bitmap!.GetPixel(50, 50).Alpha, 126, 129);
    }

    [Fact]
    public void A_group_translates_the_paths_inside_it()
    {
        const string body = """
                  E: group (line=9)
                    A: android:translateX(0x0101045a)=(type 0x4)0x40800000
                    A: android:translateY(0x0101045b)=(type 0x4)0x40800000
                    E: path (line=11)
                      A: android:fillColor(0x01010404)=(type 0x1d)0xff00ff00
                      A: android:pathData(0x01010405)="M 0 0 L 2 0 L 2 2 L 0 2 Z" (Raw: "M 0 0 L 2 0 L 2 2 L 0 2 Z")
            """;

        using var bitmap = VectorDrawables.Render(Vector(body), NoResources, 100);

        Assert.NotNull(bitmap);
        Assert.Equal(SKColors.Lime, bitmap!.GetPixel(50, 50));
        Assert.Equal(0, bitmap.GetPixel(10, 10).Alpha);
    }

    [Fact]
    public void A_stroked_path_is_drawn_with_its_stroke_color()
    {
        const string body = """
                  E: path (line=11)
                    A: android:strokeColor(0x01010406)=(type 0x1d)0xff0000ff
                    A: android:strokeWidth(0x01010407)=(type 0x4)0x40000000
                    A: android:pathData(0x01010405)="M 0 5 L 10 5" (Raw: "M 0 5 L 10 5")
            """;

        using var bitmap = VectorDrawables.Render(Vector(body), NoResources, 100);

        Assert.NotNull(bitmap);
        Assert.Equal(SKColors.Blue, bitmap!.GetPixel(50, 50));
        Assert.Equal(0, bitmap.GetPixel(50, 10).Alpha);
    }

    [Fact]
    public void A_path_without_a_solid_fill_is_skipped_and_an_empty_result_is_null()
    {
        const string gradientOnly = """
                  E: path (line=11)
                    A: android:pathData(0x01010405)="M 0 0 L 10 0 L 10 10 L 0 10 Z" (Raw: "M 0 0 L 10 0 L 10 10 L 0 10 Z")
            """;

        Assert.Null(VectorDrawables.Render(Vector(gradientOnly), NoResources, 100));
    }

    [Fact]
    public void A_linear_gradient_fill_runs_between_its_stops()
    {
        const string gradient = """
            N: android=http://schemas.android.com/apk/res/android
              E: gradient (line=18)
                A: android:type(0x010101a1)=(type 0x10)0x0
                A: android:startX(0x01010510)=(type 0x4)0x0
                A: android:startY(0x01010511)=(type 0x4)0x0
                A: android:endX(0x01010512)=(type 0x4)0x0
                A: android:endY(0x01010513)=(type 0x4)0x41200000
                E: item (line=21)
                  A: android:color(0x010101a5)=(type 0x1c)0xffff0000
                  A: android:offset(0x01010514)=(type 0x4)0x0
                E: item (line=24)
                  A: android:color(0x010101a5)=(type 0x1c)0xff0000ff
                  A: android:offset(0x01010514)=(type 0x4)0x3f800000
            """;
        const string body = """
                  E: path (line=11)
                    A: android:fillColor(0x01010404)=@0x7f080006
                    A: android:pathData(0x01010405)="M 0 0 L 10 0 L 10 10 L 0 10 Z" (Raw: "M 0 0 L 10 0 L 10 10 L 0 10 Z")
            """;

        using var bitmap = VectorDrawables.Render(Vector(body), id => new VectorDrawables.VectorFill(null, gradient), 100);

        Assert.NotNull(bitmap);
        var top = bitmap!.GetPixel(50, 2);
        var bottom = bitmap.GetPixel(50, 97);
        Assert.True(top.Red > 200 && top.Blue < 60, $"top was {top}");
        Assert.True(bottom.Blue > 200 && bottom.Red < 60, $"bottom was {bottom}");
    }

    [Fact]
    public void A_gradient_the_renderer_cannot_read_leaves_the_path_undrawn()
    {
        const string body = """
                  E: path (line=11)
                    A: android:fillColor(0x01010404)=@0x7f080006
                    A: android:pathData(0x01010405)="M 0 0 L 10 0 L 10 10 L 0 10 Z" (Raw: "M 0 0 L 10 0 L 10 10 L 0 10 Z")
            """;

        Assert.Null(VectorDrawables.Render(Vector(body), id => new VectorDrawables.VectorFill(null, "E: sweep (line=2)\n"), 100));
    }

    [Fact]
    public void Referenced_resource_ids_are_listed_once_in_order()
    {
        const string xml = """
            A: android:fillColor(0x01010404)=@0x7f080006
            A: android:strokeColor(0x01010406)=@0x7f0c0001
            A: android:fillColor(0x01010404)=@0x7F080006
            A: android:fillColor(0x01010404)=(type 0x1d)0xff660300
            """;

        Assert.Equal(["0x7f080006", "0x7f0c0001"], ApkIcons.ReferencedResourceIds(xml));
    }

    [Fact]
    public void Xml_that_is_not_a_vector_renders_nothing()
    {
        Assert.Null(VectorDrawables.Render("E: adaptive-icon (line=2)\n", NoResources, 100));
        Assert.Null(VectorDrawables.Render("", NoResources, 100));
    }
}
