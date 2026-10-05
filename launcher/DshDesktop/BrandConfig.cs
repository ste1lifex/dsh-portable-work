using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;

namespace DshDesktop;

/// <summary>
/// 桌面启动屏读到的品牌配置 —— 与 Web 端 dsh-brand 插件用的是**同一份文件**：
/// <c>&lt;DSH_HOME&gt;\dsh-brand.json</c>（DSH_HOME 由 DshCore 按 exe 位置推算）。
///
/// 时序：DshDesktop 启动时读一次；切深浅主题时用同一份内存配置按主题挑对应字段，
/// 所以用户改完 json 是**下次启动 DshDesktop 生效**，这里不做文件监听。
///
/// 容错：json 里所有字段都由插件按字符串存；这里字符串照读，数字也认（转成文本），
/// 非法/缺字段一律退化成“未配置”（空串），任何异常都不抛给启动流程 —— 启动屏
/// 宁可回落内置品牌，也不能因为一份坏 json 起不来。
/// </summary>
internal sealed class DshBrandConfig
{
    /// <summary>
    /// <c>desktopLogoHeight</c> 留空时的标志高度（px）。这是**桌面启动屏专用**字段，
    /// 与 Web 端的 markHeight / heroMarkHeight 无关。
    /// </summary>
    public const double DefaultLogoHeight = 96;

    /// <summary>标志高度安全区间（防呆：0 会看不见，过大能撑破版式）。</summary>
    public const double MinLogoHeight = 12;
    public const double MaxLogoHeight = 512;

    /// <summary>浅色主题标志（logoUrl）：相对 $DSH_HOME 的路径 / 绝对路径 / data: URL。</summary>
    public string LogoUrl { get; private set; } = string.Empty;

    /// <summary>深色主题标志（logoUrlDark）：留空＝跟随 <see cref="LogoUrl"/>。</summary>
    public string LogoUrlDark { get; private set; } = string.Empty;

    /// <summary>启动屏标语（bootSlogan）：多行；留空＝沿用内置常量。</summary>
    public string BootSlogan { get; private set; } = string.Empty;

    /// <summary>启动屏标志高度（desktopLogoHeight，px 文本）：留空＝<see cref="DefaultLogoHeight"/>。</summary>
    public string DesktopLogoHeight { get; private set; } = string.Empty;

    /// <summary>浅色主题强调色（color）。</summary>
    public string Color { get; private set; } = string.Empty;

    /// <summary>深色主题强调色（colorDark）：留空＝跟随 <see cref="Color"/>（与 Web 端同规则）。</summary>
    public string ColorDark { get; private set; } = string.Empty;

    /// <summary>实际读取的配置文件路径（日志用；文件不存在时也是这个路径）。</summary>
    public string ConfigPath { get; private set; } = string.Empty;

    /// <summary>文件存在但读取/解析失败（缺文件不算失败：缺文件 = 全默认）。</summary>
    public bool LoadFailed { get; private set; }

    /// <summary>文件是否存在。</summary>
    public bool FileExists { get; private set; }

    /// <summary>
    /// 读取品牌配置。<paramref name="dshHome"/> 由调用方从 DshCore.DshHome 传入，
    /// 不在这里自己猜路径，避免和 Web 端算出两份不同目录。
    /// </summary>
    public static DshBrandConfig Load(string dshHome)
    {
        var cfg = new DshBrandConfig();
        try
        {
            cfg.ConfigPath = Path.Combine(dshHome, "dsh-brand.json");
            cfg.FileExists = File.Exists(cfg.ConfigPath);
            if (!cfg.FileExists) return cfg;

            using var doc = JsonDocument.Parse(File.ReadAllText(cfg.ConfigPath, Encoding.UTF8));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return cfg;

            cfg.LogoUrl = ReadText(root, "logoUrl");
            cfg.LogoUrlDark = ReadText(root, "logoUrlDark");
            cfg.BootSlogan = ReadText(root, "bootSlogan");
            cfg.DesktopLogoHeight = ReadText(root, "desktopLogoHeight");
            cfg.Color = ReadText(root, "color");
            cfg.ColorDark = ReadText(root, "colorDark");
        }
        catch
        {
            cfg.LoadFailed = true;
        }
        return cfg;
    }

    /// <summary>字符串照读；数字转文本（插件理论上只写字符串，这里容错）；别的类型当未配置。</summary>
    private static string ReadText(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return string.Empty;
        return el.ValueKind switch
        {
            JsonValueKind.String => (el.GetString() ?? string.Empty).Trim(),
            JsonValueKind.Number => el.GetRawText().Trim(),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// 当前主题该用哪个标志值：深色优先 logoUrlDark，留空跟随 logoUrl
    /// （与 Web 端 “深色主题商标留空＝跟随浅色主题商标” 一致）。
    /// </summary>
    public string PickLogo(bool dark)
        => dark && LogoUrlDark.Length > 0 ? LogoUrlDark : LogoUrl;

    /// <summary>
    /// 当前主题的品牌强调色；未配置（或解析不出来）返回 null = 沿用 Theme.cs 内置默认色。
    /// 暗色留空＝跟随 color，与 Web 端 withAccentTheme() 的规则一致（同一份配置、同一观感）。
    /// </summary>
    public Color? PickAccent(bool dark)
    {
        var light = ParseColor(Color);
        var darkColor = ParseColor(ColorDark);
        if (dark) return darkColor ?? light;
        return light;
    }

    private static Color? ParseColor(string text)
        => DsTheme.TryParseCssColor(text, out var color) && color.A != 0 ? color : null;

    /// <summary>标志高度：留空/非法＝96，非整数四舍五入，夹在 12~512。</summary>
    public double LogoHeight
    {
        get
        {
            if (double.TryParse(DesktopLogoHeight, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && !double.IsNaN(value) && !double.IsInfinity(value) && value > 0)
            {
                return Math.Clamp(Math.Round(value), MinLogoHeight, MaxLogoHeight);
            }
            return DefaultLogoHeight;
        }
    }

    /// <summary>
    /// 启动屏标语文本：空/纯空白 = null（调用方回落内置常量 BootPurposeSlogan）。
    /// 换行两种写法都认：json 里真实的换行，以及字面量 "\n"（在设置界面单行输入时更顺手）。
    /// 回调排版保持现状（XAML 里 TextAlignment=Center + NoWrap），这里只负责给文本。
    /// </summary>
    public string? SloganOrNull()
    {
        var text = BootSlogan
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace("\\n", "\n")
            .Trim();
        return text.Length == 0 ? null : text;
    }
}
