using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace DshDesktop;

/// <summary>
/// 极简 SVG 渲染器：WPF 没有内置的 SVG 解码器，这里只实现“启动屏标志”真正需要的子集，
/// 直接产出 WPF **矢量**图（<see cref="DrawingImage"/>）—— 任意高度都清晰，
/// 不需要额外的 NuGet 解析库，也不需要网络。
///
/// 支持：<c>&lt;svg viewBox&gt;</c>、<c>&lt;g&gt;</c>、<c>&lt;path d&gt;</c>、<c>&lt;rect&gt;</c>、
/// <c>&lt;circle&gt;</c>、<c>&lt;ellipse&gt;</c>、<c>&lt;polygon&gt;</c>、<c>&lt;polyline&gt;</c>、
/// <c>&lt;line&gt;</c>；<c>fill / fill-opacity / stroke / stroke-width / stroke-linecap /
/// stroke-linejoin / opacity</c>；<c>translate / scale / rotate / skewX / skewY / matrix</c>
/// 变换；<c>#rgb</c> / <c>#rgba</c> / <c>#rrggbb</c> / <c>#rrggbbaa</c> / <c>rgb()</c> /
/// <c>rgba()</c> / 常用颜色名 / <c>none</c> / <c>currentColor</c>，以及 <c>style="fill:…"</c>。
///
/// 不支持（刻意不做）：渐变与图案、滤镜、蒙版、clipPath、<c>&lt;use&gt;</c>、<c>&lt;text&gt;</c>、
/// CSS 类选择器、SMIL 动画。遇到不认识的元素直接跳过，解析失败返回 false，
/// 调用方（BrandLogo）回落到内置鲸鱼标志 —— 启动屏不会因为一张图出不来而失败。
///
/// 一个刻意的偏离：SVG 规范里未指定 fill 时默认黑色。深色启动屏上那是“黑底黑图”，
/// 所以这里用调用方给的 <c>fallbackFill</c>（主题的 BootTextPrimary）兜底；
/// 文件里显式写了 fill 的（本项目的 logo-light.svg / logo-dark.svg 都写了）照原样使用。
/// </summary>
internal static class SvgImage
{
    /// <summary>各元素的绘制属性（SVG 里这些是可继承的）。</summary>
    private struct Paint
    {
        public Color? Fill;
        public double FillOpacity;
        public Color? Stroke;
        public double StrokeWidth;
        public double Opacity;
        public PenLineCap Cap;
        public PenLineJoin Join;
    }

    /// <summary>
    /// 渲染 SVG 文本。
    /// <paramref name="fallbackFill"/>：没写 fill 的图形用它着色（见类注释里的偏离说明）。
    /// <paramref name="width"/>/<paramref name="height"/> 返回 viewBox 尺寸（用户单位，
    /// 调用方按此算宽高比并决定实际显示高度）。
    /// </summary>
    public static bool TryRender(string svgText, Color fallbackFill,
        out ImageSource? source, out double width, out double height, out string reason)
    {
        source = null;
        width = 0;
        height = 0;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(svgText))
        {
            reason = "SVG 内容为空";
            return false;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(svgText, LoadOptions.None);
        }
        catch (Exception ex)
        {
            reason = "SVG 解析失败：" + ex.Message;
            return false;
        }

        var root = doc.Root;
        if (root is null || !NameIs(root, "svg"))
        {
            reason = "不是 <svg> 根元素";
            return false;
        }

        var content = new DrawingGroup();
        var paint = new Paint
        {
            Fill = fallbackFill,
            FillOpacity = 1,
            Stroke = null,
            StrokeWidth = 1,
            Opacity = 1,
            Cap = PenLineCap.Flat,
            Join = PenLineJoin.Miter,
        };
        // <svg> 自己也能带 fill / opacity，一并作为继承起点
        ApplyAttributes(root, ref paint, fallbackFill);
        foreach (var child in root.Elements())
            Walk(child, paint, content.Children, fallbackFill);

        if (content.Children.Count == 0)
        {
            reason = "SVG 里没有本渲染器支持的图形";
            return false;
        }

        // 尺寸：优先 viewBox，其次 width/height，最后退回图形实际范围
        double minX = 0, minY = 0, vbW, vbH;
        var viewBox = ViewBoxOf(root);
        if (viewBox is not null)
        {
            minX = viewBox[0];
            minY = viewBox[1];
            vbW = viewBox[2];
            vbH = viewBox[3];
        }
        else
        {
            double w = LengthOf(root.Attribute("width")?.Value);
            double h = LengthOf(root.Attribute("height")?.Value);
            if (w > 0 && h > 0)
            {
                vbW = w;
                vbH = h;
            }
            else
            {
                var bounds = content.Bounds;
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    reason = "SVG 没有可用尺寸（缺 viewBox / width / height）";
                    return false;
                }
                minX = bounds.X;
                minY = bounds.Y;
                vbW = bounds.Width;
                vbH = bounds.Height;
            }
        }

        if (vbW <= 0 || vbH <= 0 || double.IsNaN(vbW) || double.IsNaN(vbH))
        {
            reason = "SVG viewBox 尺寸非法";
            return false;
        }

        // 把 viewBox 的原点平移回 (0,0)：调用方拿 (vbW, vbH) 当逻辑尺寸按比例放大即可
        content.Transform = new MatrixTransform(new Matrix(1, 0, 0, 1, -minX, -minY));
        var image = new DrawingImage(content);
        try
        {
            content.Freeze();
            image.Freeze();
        }
        catch
        {
            // 冻结失败只影响性能，不影响显示
        }

        source = image;
        width = vbW;
        height = vbH;
        return true;
    }

    private static void Walk(XElement el, Paint inherited, DrawingCollection output, Color fallbackFill)
    {
        var name = el.Name.LocalName;
        switch (name)
        {
            case "defs":
            case "title":
            case "desc":
            case "metadata":
            case "style":
            case "script":
                return;
        }

        var paint = inherited;
        ApplyAttributes(el, ref paint, fallbackFill);

        if (name is "g" or "svg" or "a")
        {
            foreach (var child in el.Elements())
                Walk(child, paint, output, fallbackFill);
            return;
        }

        var geometry = BuildGeometry(el, name);
        if (geometry is null) return; // use / text / image / 未知元素：跳过

        var transform = TransformOf(el);
        if (transform is not null)
        {
            // Geometry.Parse() 返回的是**已冻结**的几何体，直接给它的 Transform 赋值会抛
            // InvalidOperationException（"处于只读状态"），所以套一层自己创建的 GeometryGroup。
            var wrapper = new GeometryGroup();
            wrapper.Children.Add(geometry);
            wrapper.Transform = transform;
            geometry = wrapper;
        }

        // 线只有描边有意义（SVG 里 <line> 也确实不渲染填充）
        bool strokeOnly = name == "line";

        Brush? fill = null;
        if (!strokeOnly && paint.Fill is { } fillColor)
        {
            double alpha = paint.Opacity * paint.FillOpacity;
            if (alpha > 0.001)
            {
                var brush = new SolidColorBrush(Scale(fillColor, alpha));
                brush.Freeze();
                fill = brush;
            }
        }

        Pen? pen = null;
        if (paint.Stroke is { } strokeColor && paint.StrokeWidth > 0)
        {
            var brush = new SolidColorBrush(Scale(strokeColor, paint.Opacity));
            brush.Freeze();
            pen = new Pen(brush, paint.StrokeWidth)
            {
                StartLineCap = paint.Cap,
                EndLineCap = paint.Cap,
                LineJoin = paint.Join,
            };
            pen.Freeze();
        }

        if (fill is null && pen is null) return;

        var drawing = new GeometryDrawing(fill, pen, geometry);
        drawing.Freeze();
        output.Add(drawing);
    }

    /// <summary>把元素的绘制属性并进继承状态（属性优先于内联 style，两者都读）。</summary>
    private static void ApplyAttributes(XElement el, ref Paint paint, Color fallbackFill)
    {
        var style = el.Attribute("style")?.Value;

        string? Value(string attribute)
        {
            var direct = el.Attribute(attribute)?.Value;
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
            if (string.IsNullOrWhiteSpace(style)) return null;
            var match = Regex.Match(style, @"(?:^|;)\s*" + Regex.Escape(attribute) + @"\s*:\s*([^;]+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        var fill = Value("fill");
        if (fill is not null) paint.Fill = ParsePaint(fill, fallbackFill);

        var fillOpacity = Value("fill-opacity");
        if (TryNumber(fillOpacity, out var fo)) paint.FillOpacity = Math.Clamp(fo, 0, 1);

        var stroke = Value("stroke");
        if (stroke is not null) paint.Stroke = ParsePaint(stroke, fallbackFill);

        var strokeWidth = Value("stroke-width");
        if (strokeWidth is not null) paint.StrokeWidth = Math.Max(0, LengthOf(strokeWidth));

        var opacity = Value("opacity");
        if (TryNumber(opacity, out var op)) paint.Opacity *= Math.Clamp(op, 0, 1);

        var linecap = Value("stroke-linecap");
        if (linecap is not null)
        {
            paint.Cap = linecap.Trim().ToLowerInvariant() switch
            {
                "round" => PenLineCap.Round,
                "square" => PenLineCap.Square,
                _ => PenLineCap.Flat,
            };
        }

        var linejoin = Value("stroke-linejoin");
        if (linejoin is not null)
        {
            paint.Join = linejoin.Trim().ToLowerInvariant() switch
            {
                "round" => PenLineJoin.Round,
                "bevel" => PenLineJoin.Bevel,
                _ => PenLineJoin.Miter,
            };
        }
    }

    /// <summary>颜色值：none / transparent / currentColor / #hex / rgb() / 常用颜色名；渐变等 url(…) 视为无色。</summary>
    private static Color? ParsePaint(string value, Color fallbackFill)
    {
        var text = value.Trim();
        if (text.Length == 0) return null;
        if (text.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return fallbackFill;
        if (DsTheme.TryParseCssColor(text, out var color)) return color.A == 0 ? null : color;
        return NamedColors.TryGetValue(text, out var named) ? named : (Color?)null;
    }

    private static Geometry? BuildGeometry(XElement el, string name)
    {
        switch (name)
        {
            case "path":
            {
                var d = el.Attribute("d")?.Value;
                if (string.IsNullOrWhiteSpace(d)) return null;
                try
                {
                    // WPF 的路径迷你语言就是 SVG 的 d 语法，直接复用它的解析器
                    return Geometry.Parse(d);
                }
                catch
                {
                    return null; // 畸形路径：跳过这个元素，不拖垮整张图
                }
            }
            case "rect":
            {
                double w = LengthOf(el.Attribute("width")?.Value);
                double h = LengthOf(el.Attribute("height")?.Value);
                if (w <= 0 || h <= 0) return null;
                double rx = LengthOf(el.Attribute("rx")?.Value);
                double ry = LengthOf(el.Attribute("ry")?.Value);
                if (rx <= 0 && ry > 0) rx = ry;
                if (ry <= 0 && rx > 0) ry = rx;
                return new RectangleGeometry(
                    new Rect(LengthOf(el.Attribute("x")?.Value), LengthOf(el.Attribute("y")?.Value), w, h),
                    Math.Min(rx, w / 2), Math.Min(ry, h / 2));
            }
            case "circle":
            {
                double r = LengthOf(el.Attribute("r")?.Value);
                if (r <= 0) return null;
                return new EllipseGeometry(
                    new Point(LengthOf(el.Attribute("cx")?.Value), LengthOf(el.Attribute("cy")?.Value)), r, r);
            }
            case "ellipse":
            {
                double rx = LengthOf(el.Attribute("rx")?.Value);
                double ry = LengthOf(el.Attribute("ry")?.Value);
                if (rx <= 0 || ry <= 0) return null;
                return new EllipseGeometry(
                    new Point(LengthOf(el.Attribute("cx")?.Value), LengthOf(el.Attribute("cy")?.Value)), rx, ry);
            }
            case "polygon":
            case "polyline":
            {
                var points = Points(el.Attribute("points")?.Value);
                if (points.Count < 2) return null;
                var figure = new PathFigure
                {
                    StartPoint = points[0],
                    IsClosed = name == "polygon",
                    IsFilled = true,
                };
                for (int i = 1; i < points.Count; i++)
                    figure.Segments.Add(new LineSegment(points[i], true));
                var path = new PathGeometry();
                path.Figures.Add(figure);
                return path;
            }
            case "line":
                return new LineGeometry(
                    new Point(LengthOf(el.Attribute("x1")?.Value), LengthOf(el.Attribute("y1")?.Value)),
                    new Point(LengthOf(el.Attribute("x2")?.Value), LengthOf(el.Attribute("y2")?.Value)));
            default:
                return null;
        }
    }

    private static Transform? TransformOf(XElement el)
    {
        var text = el.Attribute("transform")?.Value;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var matrix = Matrix.Identity;
        bool any = false;
        foreach (Match match in Regex.Matches(text, @"([a-zA-Z]+)\s*\(([^)]*)\)"))
        {
            var numbers = Numbers(match.Groups[2].Value);
            Matrix step;
            switch (match.Groups[1].Value)
            {
                case "matrix":
                    if (numbers.Count < 6) continue;
                    step = new Matrix(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]);
                    break;
                case "translate":
                    step = new Matrix(1, 0, 0, 1, At(numbers, 0), At(numbers, 1));
                    break;
                case "scale":
                {
                    double sx = At(numbers, 0, 1);
                    double sy = numbers.Count > 1 ? numbers[1] : sx;
                    step = new Matrix(sx, 0, 0, sy, 0, 0);
                    break;
                }
                case "rotate":
                {
                    double angle = At(numbers, 0) * Math.PI / 180.0;
                    double cos = Math.Cos(angle), sin = Math.Sin(angle);
                    var rotate = new Matrix(cos, sin, -sin, cos, 0, 0);
                    if (numbers.Count >= 3)
                    {
                        // rotate(a,cx,cy) = T(-cx,-cy) → 旋转 → T(cx,cy)
                        step = Matrix.Multiply(
                            Matrix.Multiply(new Matrix(1, 0, 0, 1, -numbers[1], -numbers[2]), rotate),
                            new Matrix(1, 0, 0, 1, numbers[1], numbers[2]));
                    }
                    else
                    {
                        step = rotate;
                    }
                    break;
                }
                case "skewX":
                {
                    double angle = At(numbers, 0) * Math.PI / 180.0;
                    step = new Matrix(1, 0, Math.Tan(angle), 1, 0, 0);
                    break;
                }
                case "skewY":
                {
                    double angle = At(numbers, 0) * Math.PI / 180.0;
                    step = new Matrix(1, Math.Tan(angle), 0, 1, 0, 0);
                    break;
                }
                default:
                    continue;
            }
            // SVG 的变换列表是「右边的先作用到坐标上」（列向量），WPF 的 Matrix 是行向量：
            // 所以从左往右做 m = Multiply(当前项, m)，结果与 SVG 语义一致。若写反，
            // translate(...) scale(...) 会把缩放也作用到平移量上，位置直接跑偏。
            matrix = Matrix.Multiply(step, matrix);
            any = true;
        }

        return any ? new MatrixTransform(matrix) : null;
    }

    // ---------------------------------------------------------------------
    //  小工具
    // ---------------------------------------------------------------------

    private static bool NameIs(XElement el, string name)
        => string.Equals(el.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    private static double[]? ViewBoxOf(XElement root)
    {
        var text = root.Attribute("viewBox")?.Value;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var numbers = Numbers(text);
        if (numbers.Count < 4 || numbers[2] <= 0 || numbers[3] <= 0) return null;
        return new[] { numbers[0], numbers[1], numbers[2], numbers[3] };
    }

    /// <summary>带单位的长度 → 用户单位（px）；不认识的单位（% / em…）按 0 处理。</summary>
    private static double LengthOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var match = Regex.Match(text.Trim(),
            @"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*([a-zA-Z%]*)$");
        if (!match.Success) return 0;
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return 0;
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "" or "px" => value,
            "pt" => value * 96.0 / 72.0,
            "pc" => value * 16.0,
            "in" => value * 96.0,
            "cm" => value * 96.0 / 2.54,
            "mm" => value * 96.0 / 25.4,
            _ => 0,
        };
    }

    private static List<double> Numbers(string text)
    {
        var list = new List<double>();
        foreach (Match match in Regex.Matches(text, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?"))
        {
            if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                list.Add(value);
        }
        return list;
    }

    private static double At(List<double> numbers, int index, double fallback = 0)
        => index < numbers.Count ? numbers[index] : fallback;

    private static List<Point> Points(string? text)
    {
        var numbers = text is null ? new List<double>() : Numbers(text);
        var points = new List<Point>(numbers.Count / 2);
        for (int i = 0; i + 1 < numbers.Count; i += 2)
            points.Add(new Point(numbers[i], numbers[i + 1]));
        return points;
    }

    private static bool TryNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static Color Scale(Color color, double factor)
        => Color.FromArgb(
            (byte)Math.Clamp(Math.Round(color.A * Math.Clamp(factor, 0, 1)), 0, 255),
            color.R, color.G, color.B);

    /// <summary>常用颜色名（SVG 147 个具名色里的常见一批；未命中的名字按无色处理）。</summary>
    private static readonly Dictionary<string, Color> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = Colors.Black, ["white"] = Colors.White, ["red"] = Colors.Red,
        ["green"] = Color.FromRgb(0x00, 0x80, 0x00), ["blue"] = Colors.Blue,
        ["gray"] = Colors.Gray, ["grey"] = Colors.Gray, ["silver"] = Colors.Silver,
        ["navy"] = Colors.Navy, ["yellow"] = Colors.Yellow, ["orange"] = Colors.Orange,
        ["purple"] = Colors.Purple, ["pink"] = Colors.Pink, ["brown"] = Colors.Brown,
        ["cyan"] = Colors.Cyan, ["aqua"] = Colors.Aqua, ["magenta"] = Colors.Magenta,
        ["fuchsia"] = Colors.Fuchsia, ["lime"] = Colors.Lime, ["teal"] = Colors.Teal,
        ["maroon"] = Colors.Maroon, ["olive"] = Colors.Olive, ["gold"] = Colors.Gold,
        ["transparent"] = Colors.Transparent, ["darkgray"] = Colors.DarkGray,
        ["darkgrey"] = Colors.DarkGray, ["lightgray"] = Colors.LightGray,
        ["lightgrey"] = Colors.LightGray, ["dimgray"] = Colors.DimGray,
        ["dimgrey"] = Colors.DimGray, ["whitesmoke"] = Colors.WhiteSmoke,
        ["deepskyblue"] = Colors.DeepSkyBlue, ["dodgerblue"] = Colors.DodgerBlue,
        ["royalblue"] = Colors.RoyalBlue, ["steelblue"] = Colors.SteelBlue,
        ["skyblue"] = Colors.SkyBlue, ["lightblue"] = Colors.LightBlue,
        ["indigo"] = Colors.Indigo, ["violet"] = Colors.Violet, ["crimson"] = Colors.Crimson,
        ["indianred"] = Colors.IndianRed, ["tomato"] = Colors.Tomato,
        ["coral"] = Colors.Coral, ["salmon"] = Colors.Salmon,
        ["darkblue"] = Colors.DarkBlue, ["darkred"] = Colors.DarkRed,
        ["darkgreen"] = Colors.DarkGreen, ["darkorange"] = Colors.DarkOrange,
        ["darkviolet"] = Colors.DarkViolet, ["deeppink"] = Colors.DeepPink,
        ["hotpink"] = Colors.HotPink, ["lightgreen"] = Colors.LightGreen,
        ["limegreen"] = Colors.LimeGreen, ["seagreen"] = Colors.SeaGreen,
        ["forestgreen"] = Colors.ForestGreen, ["slategray"] = Colors.SlateGray,
        ["slategrey"] = Colors.SlateGray,
        ["currentcolor"] = Colors.Transparent, // 实际由 ParsePaint 在更早的分支处理
    };
}
