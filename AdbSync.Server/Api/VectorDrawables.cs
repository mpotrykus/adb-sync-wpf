using SkiaSharp;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AdbSync.Server.Api;

/// <summary>
/// Draws Android vector drawables (the XML many launcher icons use for their layers) from the text <c>aapt dump xmltree</c> prints.
/// Covers what icon layers use in practice: paths filled or stroked with a solid color or a linear or radial gradient, groups with
/// translate/scale/rotate, and clip paths. A path with any other fill is skipped.
/// </summary>
public static partial class VectorDrawables
{
    /// <summary>What a color reference resolves to: a solid ARGB color, or the <c>aapt dump xmltree</c> of a gradient drawable.</summary>
    public sealed record VectorFill(uint? Argb, string? GradientXml = null);

    private sealed class Node(string name)
    {
        public string Name { get; } = name;
        public Dictionary<string, string> Attributes { get; } = [];
        public List<Node> Children { get; } = [];
    }

    [GeneratedRegex(@"^(?<indent> *)E: (?<name>[\w:\-]+)")]
    private static partial Regex ElementPattern();

    [GeneratedRegex(@"^\s*A: (?:android:)?(?<name>[\w:\-]+)\(0x[0-9a-fA-F]+\)=(?<value>.*)$")]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"^\(type 0x(?<type>[0-9a-fA-F]+)\)0x(?<bits>[0-9a-fA-F]+)")]
    private static partial Regex TypedValuePattern();

    /// <summary>
    /// The drawable as a square bitmap of <paramref name="pixels"/> on a side, its viewport scaled to fit. <paramref name="resolveFill"/> turns a
    /// resource id (<c>0x7f080006</c>) into a color or gradient. Null when the XML isn't a vector or nothing in it could be drawn.
    /// </summary>
    public static SKBitmap? Render(string xmltree, Func<string, VectorFill?> resolveFill, int pixels)
    {
        var root = Parse(xmltree);
        var vector = root.Children.FirstOrDefault(n => n.Name == "vector");
        if (vector is null)
            return null;

        var viewportWidth = Float(vector, "viewportWidth") ?? 0;
        var viewportHeight = Float(vector, "viewportHeight") ?? 0;
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return null;

        var bitmap = new SKBitmap(pixels, pixels);
        var keep = false;
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(pixels / viewportWidth, pixels / viewportHeight);

            var drawn = false;
            foreach (var child in vector.Children)
                drawn |= Draw(canvas, child, resolveFill);
            keep = drawn;
            return drawn ? bitmap : null;
        }
        finally
        {
            if (!keep)
                bitmap.Dispose();
        }
    }

    private static bool Draw(SKCanvas canvas, Node node, Func<string, VectorFill?> resolveFill)
    {
        switch (node.Name)
        {
            case "group":
                canvas.Save();
                try
                {
                    var pivotX = Float(node, "pivotX") ?? 0;
                    var pivotY = Float(node, "pivotY") ?? 0;
                    canvas.Translate(Float(node, "translateX") ?? 0, Float(node, "translateY") ?? 0);
                    canvas.Translate(pivotX, pivotY);
                    canvas.RotateDegrees(Float(node, "rotation") ?? 0);
                    canvas.Scale(Float(node, "scaleX") ?? 1, Float(node, "scaleY") ?? 1);
                    canvas.Translate(-pivotX, -pivotY);

                    var drawn = false;
                    foreach (var child in node.Children)
                        drawn |= Draw(canvas, child, resolveFill);
                    return drawn;
                }
                finally
                {
                    canvas.Restore();
                }

            case "clip-path":
                if (Path(node) is { } clip)
                {
                    using (clip)
                        canvas.ClipPath(clip, SKClipOperation.Intersect, true);
                }
                return false;

            case "path":
                return DrawPath(canvas, node, resolveFill);

            default:
                return false;
        }
    }

    private static bool DrawPath(SKCanvas canvas, Node node, Func<string, VectorFill?> resolveFill)
    {
        using var path = Path(node);
        if (path is null)
            return false;
        path.FillType = Int(node, "fillType") == 1 ? SKPathFillType.EvenOdd : SKPathFillType.Winding;

        var drawn = false;
        if (Fill(node, "fillColor", resolveFill) is { } fill)
        {
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            if (Apply(paint, fill, Float(node, "fillAlpha") ?? 1))
            {
                canvas.DrawPath(path, paint);
                drawn = true;
            }
        }

        var strokeWidth = Float(node, "strokeWidth") ?? 0;
        if (strokeWidth > 0 && Fill(node, "strokeColor", resolveFill) is { } stroke)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth,
                StrokeCap = Int(node, "strokeLineCap") switch { 1 => SKStrokeCap.Round, 2 => SKStrokeCap.Square, _ => SKStrokeCap.Butt },
                StrokeJoin = Int(node, "strokeLineJoin") switch { 1 => SKStrokeJoin.Round, 2 => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter },
            };
            if (Apply(paint, stroke, Float(node, "strokeAlpha") ?? 1))
            {
                canvas.DrawPath(path, paint);
                drawn = true;
            }
        }
        return drawn;
    }

    /// <summary>Colors the paint with the fill. False when it can't be drawn (a gradient this doesn't understand).</summary>
    private static bool Apply(SKPaint paint, VectorFill fill, float alpha)
    {
        if (fill.Argb is { } argb)
        {
            paint.Color = WithAlpha(argb, alpha);
            return true;
        }
        if (fill.GradientXml is null || Gradient(fill.GradientXml) is not { } shader)
            return false;
        paint.Shader = shader;
        paint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(255 * alpha), 0, 255));
        return true;
    }

    private static SKShader? Gradient(string xmltree)
    {
        if (Parse(xmltree).Children.FirstOrDefault(n => n.Name == "gradient") is not { } gradient)
            return null;

        var stops = gradient.Children
            .Where(n => n.Name == "item")
            .Select(n => (Argb: RawColor(n, "color"), Offset: Float(n, "offset") ?? 0))
            .Where(i => i.Argb is not null)
            .Select(i => (Color: new SKColor(i.Argb!.Value), i.Offset))
            .OrderBy(i => i.Offset)
            .ToList();
        if (stops.Count == 0)
        {
            var start = RawColor(gradient, "startColor");
            var end = RawColor(gradient, "endColor");
            if (start is null || end is null)
                return null;
            stops.Add((new SKColor(start.Value), 0f));
            if (RawColor(gradient, "centerColor") is { } center)
                stops.Add((new SKColor(center), 0.5f));
            stops.Add((new SKColor(end.Value), 1f));
        }
        if (stops.Count < 2)
            return null;

        var colors = stops.Select(s => s.Color).ToArray();
        var positions = stops.Select(s => Math.Clamp(s.Offset, 0f, 1f)).ToArray();
        return Int(gradient, "type") switch
        {
            0 => SKShader.CreateLinearGradient(
                new SKPoint(Float(gradient, "startX") ?? 0, Float(gradient, "startY") ?? 0),
                new SKPoint(Float(gradient, "endX") ?? 0, Float(gradient, "endY") ?? 0),
                colors, positions, SKShaderTileMode.Clamp),
            1 => SKShader.CreateRadialGradient(
                new SKPoint(Float(gradient, "centerX") ?? 0, Float(gradient, "centerY") ?? 0),
                Float(gradient, "gradientRadius") ?? 1,
                colors, positions, SKShaderTileMode.Clamp),
            _ => null,
        };
    }

    private static SKPath? Path(Node node) =>
        Text(node, "pathData") is { Length: > 0 } data ? SKPath.ParseSvgPathData(data) : null;

    private static SKColor WithAlpha(uint argb, float alpha)
    {
        var color = new SKColor(argb);
        return color.WithAlpha((byte)Math.Clamp(Math.Round(color.Alpha * alpha), 0, 255));
    }

    private static VectorFill? Fill(Node node, string name, Func<string, VectorFill?> resolveFill)
    {
        if (!node.Attributes.TryGetValue(name, out var value))
            return null;
        if (value.StartsWith("@0x", StringComparison.Ordinal))
            return resolveFill(value[1..]);
        return RawColor(node, name) is { } argb ? new VectorFill(argb) : null;
    }

    private static uint? RawColor(Node node, string name)
    {
        if (!node.Attributes.TryGetValue(name, out var value))
            return null;
        return TypedValuePattern().Match(value) is { Success: true } m
            && int.Parse(m.Groups["type"].Value, NumberStyles.HexNumber) is >= 0x1c and <= 0x1f
            && uint.TryParse(m.Groups["bits"].Value, NumberStyles.HexNumber, null, out var argb)
                ? argb
                : null;
    }

    private static float? Float(Node node, string name)
    {
        if (!node.Attributes.TryGetValue(name, out var value) || TypedValuePattern().Match(value) is not { Success: true } m)
            return null;
        return int.Parse(m.Groups["type"].Value, NumberStyles.HexNumber) == 0x4
            && uint.TryParse(m.Groups["bits"].Value, NumberStyles.HexNumber, null, out var bits)
                ? BitConverter.Int32BitsToSingle(unchecked((int)bits))
                : null;
    }

    private static int? Int(Node node, string name) =>
        node.Attributes.TryGetValue(name, out var value) && TypedValuePattern().Match(value) is { Success: true } m
            && int.TryParse(m.Groups["bits"].Value, NumberStyles.HexNumber, null, out var number)
                ? number
                : null;

    private static string? Text(Node node, string name)
    {
        if (!node.Attributes.TryGetValue(name, out var value) || !value.StartsWith('"'))
            return null;
        var end = value.IndexOf('"', 1);
        return end < 0 ? null : value[1..end];
    }

    private static Node Parse(string xmltree)
    {
        var root = new Node("root");
        var stack = new List<(int Indent, Node Node)> { (-1, root) };
        Node? current = null;
        foreach (var raw in xmltree.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (ElementPattern().Match(line) is { Success: true } element)
            {
                var indent = element.Groups["indent"].Length;
                while (stack[^1].Indent >= indent)
                    stack.RemoveAt(stack.Count - 1);
                current = new Node(element.Groups["name"].Value);
                stack[^1].Node.Children.Add(current);
                stack.Add((indent, current));
            }
            else if (current is not null && AttributePattern().Match(line) is { Success: true } attribute)
            {
                current.Attributes[attribute.Groups["name"].Value] = attribute.Groups["value"].Value.Trim();
            }
        }
        return root;
    }
}
