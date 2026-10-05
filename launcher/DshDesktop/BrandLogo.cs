using System;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DshDesktop;

/// <summary>
/// 启动屏标志的取值解析：把 dsh-brand.json 里 <c>logoUrl</c> / <c>logoUrlDark</c> 的值
/// 解析成可显示的 WPF 图像。支持三种取值（与任务要求一致）：
///
///  1. **相对 $DSH_HOME 的本地路径**：<c>./brand/logo-light.svg</c>、<c>brand/logo.png</c>；
///  2. **绝对路径**：<c>D:\brand\logo.svg</c>（或 UNC 路径）；
///  3. **data: URL**：<c>data:image/svg+xml,…</c>、<c>data:image/png;base64,…</c>；
///     另外顺手认**原始 SVG 代码**（&lt;svg 开头，插件 sanitize() 会把它转成 data URL，
///     但直接写在文件里也照样能读）。
///
/// **http(s) 远程 URL 本轮不支持**（不引入网络依赖）：命中即视为“未配置”，
/// 启动屏回落到内置鲸鱼 + 字标，并在日志里写一行原因。
///
/// 图像格式：位图交给 WPF 解码器（png / jpg / gif / bmp / ico / tiff 可用；webp 取决于系统 WIC 编解码器）；
/// SVG 交给 <see cref="SvgImage"/> 的极简矢量渲染（WPF 没有内置 SVG 解码器）。
/// 任何失败都返回 false，绝不抛异常 —— 保底是内置鲸鱼标志，不能因为一张图起不来。
/// </summary>
internal static class BrandLogo
{
    /// <summary>单个标志（文件或 data URL）的解码上限，防止一份畸形配置把启动吃满内存。</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>解析结果：矢量/位图统一成 ImageSource + 原始宽高（用于算宽高比）。</summary>
    internal readonly struct Logo
    {
        public Logo(ImageSource source, double width, double height)
        {
            Source = source;
            Width = width;
            Height = height;
        }

        public ImageSource Source { get; }

        public double Width { get; }

        public double Height { get; }

        /// <summary>宽高比；异常值一律按 1:1 处理，保证调用方能算出可见尺寸。</summary>
        public double Aspect => Width > 0 && Height > 0 ? Width / Height : 1.0;
    }

    /// <summary>
    /// 尝试把配置值解析成标志图。
    /// <paramref name="fallbackFill"/> 用于 SVG 里**没写 fill** 的图形（走主题的
    /// BootTextPrimary，浅色主题给近黑、深色主题给白，而不是 SVG 规范的黑色）。
    /// </summary>
    /// <returns>false = 未配置 / 不支持 / 读不出来，调用方保持内置标志。</returns>
    public static bool TryLoad(string? value, string dshHome, Color fallbackFill, out Logo logo, out string reason)
    {
        logo = default;
        reason = string.Empty;

        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            reason = "未配置";
            return false;
        }

        // 1) data: URL
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDecodeDataUrl(raw, out var mime, out var text, out var bytes, out reason)) return false;
            if (LooksLikeSvg(mime, text)) return TrySvg(text!, fallbackFill, out logo, out reason);
            if (bytes is null)
            {
                reason = "data URL 不是可解码的图片数据";
                return false;
            }
            return TryBitmap(bytes, out logo, out reason);
        }

        // 2) 远程 URL：本轮明确不支持（不引网络依赖），回落内置标志
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            reason = "暂不支持 http(s) 远程标志（本地路径 / data URL 可用）";
            return false;
        }

        // 3) 原始 SVG 代码
        if (raw.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0)
            return TrySvg(raw, fallbackFill, out logo, out reason);

        // 4) 文件路径：绝对路径直接用；相对路径相对 $DSH_HOME 解析（不写死盘符）
        string full;
        try
        {
            full = Path.IsPathFullyQualified(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(dshHome, raw));
        }
        catch (Exception ex)
        {
            reason = "标志路径非法：" + ex.Message;
            return false;
        }

        try
        {
            if (!File.Exists(full))
            {
                reason = "标志文件不存在：" + full;
                return false;
            }
            var info = new FileInfo(full);
            if (info.Length > MaxBytes)
            {
                reason = $"标志文件过大（{info.Length / 1024} KB > {MaxBytes / 1024} KB）：{full}";
                return false;
            }
            if (full.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                return TrySvg(File.ReadAllText(full, Encoding.UTF8), fallbackFill, out logo, out reason);
            return TryBitmap(File.ReadAllBytes(full), out logo, out reason);
        }
        catch (Exception ex)
        {
            reason = "标志读取失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>位图交给 WPF 解码（ImageSource 冻结，跨线程/缓存都安全）。</summary>
    private static bool TryBitmap(byte[] bytes, out Logo logo, out string reason)
    {
        logo = default;
        reason = string.Empty;
        if (bytes.Length == 0)
        {
            reason = "图片数据为空";
            return false;
        }
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            // OnLoad：解码立刻完成，之后不再依赖这个 stream（using 会把它关掉）
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            // 按 DPI 还原成 WPF 的 DIP 尺寸（96 DPI 的图 1:1）
            double width = bitmap.PixelWidth * 96.0 / Math.Max(1.0, bitmap.DpiX);
            double height = bitmap.PixelHeight * 96.0 / Math.Max(1.0, bitmap.DpiY);
            if (width <= 0 || height <= 0)
            {
                reason = "图片尺寸为 0";
                return false;
            }
            logo = new Logo(bitmap, width, height);
            return true;
        }
        catch (Exception ex)
        {
            reason = "图片解码失败（WPF 不支持该格式？）：" + ex.Message;
            return false;
        }
    }

    private static bool LooksLikeSvg(string mime, string? text)
        => mime.Contains("svg", StringComparison.OrdinalIgnoreCase)
           || (text is not null && text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith("<svg", StringComparison.OrdinalIgnoreCase));

    /// <summary>SVG → 矢量图；把 SvgImage 的 (ImageSource, 宽, 高) 包成 Logo。</summary>
    private static bool TrySvg(string svgText, Color fallbackFill, out Logo logo, out string reason)
    {
        logo = default;
        if (SvgImage.TryRender(svgText, fallbackFill, out var source, out var width, out var height, out reason)
            && source is not null)
        {
            logo = new Logo(source, width, height);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 解析 data URL：<c>data:[&lt;mime&gt;][;base64],&lt;payload&gt;</c>。
    /// base64 一律解成字节（SVG 再转成文本）；非 base64 只对文本型（SVG）有意义。
    /// </summary>
    private static bool TryDecodeDataUrl(string url, out string mime, out string? text, out byte[]? bytes, out string reason)
    {
        mime = string.Empty;
        text = null;
        bytes = null;
        reason = string.Empty;

        int comma = url.IndexOf(',');
        if (comma < 0)
        {
            reason = "data URL 缺少逗号分隔的数据段";
            return false;
        }
        var header = url.Substring(5, comma - 5);
        var payload = url[(comma + 1)..];
        bool base64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        if (base64) header = header[..^7];

        var semicolon = header.IndexOf(';');
        mime = semicolon >= 0 ? header[..semicolon] : header;
        if (mime.Length == 0) mime = "text/plain";

        try
        {
            if (base64)
            {
                bytes = Convert.FromBase64String(payload.Trim());
                if (bytes.Length > MaxBytes)
                {
                    reason = "data URL 数据过大";
                    return false;
                }
                if (mime.Contains("svg", StringComparison.OrdinalIgnoreCase))
                    text = Encoding.UTF8.GetString(bytes);
                return true;
            }

            var decoded = Uri.UnescapeDataString(payload);
            if (decoded.Length > MaxBytes)
            {
                reason = "data URL 数据过大";
                return false;
            }
            if (mime.Contains("svg", StringComparison.OrdinalIgnoreCase) || decoded.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            {
                text = decoded;
                return true;
            }
            // 非 base64 的非 SVG 图片（极少见）：按 UTF-8 文本取字节试解码
            bytes = Encoding.UTF8.GetBytes(decoded);
            return true;
        }
        catch (Exception ex)
        {
            reason = "data URL 解析失败：" + ex.Message;
            return false;
        }
    }
}
