using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DshDesktopEngine;

namespace DshDesktop;

/// <summary>浅色 / 深色。</summary>
internal enum DsThemeKind { Dark = 0, Light = 1 }

/// <summary>
/// 桌面壳（WPF 原生部分）的配色与主题切换。
///
/// DSH 界面本身（WebUI）会跟随 Windows 深浅色或用户在「设置」里选的
/// ui-theme.preference 自动变色；原生控制条以前是写死的墨黑，于是会出现
/// “网页变白、外壳还是黑的”割裂感。这里把外壳的每个颜色都做成可改写的
/// 共享画刷：
///
/// * 页面加载前：按注册表 AppsUseLightTheme 选择内置调色板（启动即正确）；
/// * 页面加载后：注入的脚本把<body data-ds-dark-theme>状态与实时设计令牌
///   （--dsw-alias-*）回传给宿主，外壳与网页逐像素对齐（皮肤也能跟随）。
///
/// 调色板取值对齐 DSH 设计令牌，深色底 #151517 / 浅色底 #ffffff 与网页一致。
/// </summary>
internal static class DsTheme
{
    public static DsThemeKind Current { get; private set; } = DsThemeKind.Dark;

    /// <summary>主题（深浅或配色）发生变化时触发。</summary>
    public static event Action<DsThemeKind>? Changed;

    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.Ordinal);

    // =====================================================================
    //  调色板：Key -> (深色 ARGB, 浅色 ARGB)
    // =====================================================================
    private static readonly (string Key, uint Dark, uint Light)[] Palette =
    {
        ("WindowBg",           0xFF151517, 0xFFFFFFFF),
        ("ChromeBg",           0xFF1B1B1C, 0xFFF5F6F7),
        ("CardBg",             0xFF1B1B1C, 0xFFF9FAFB),
        ("CardBorder",         0x1FFFFFFF, 0x1A000000),
        ("LogBg",              0xFF0F1115, 0xFFF9FAFB),
        ("TextPrimary",        0xFFF9FAFB, 0xFF0F1115),
        ("TextSecondary",      0xFFCFD3D6, 0xFF61666B),
        ("TextDim",            0xFFADB2B8, 0xFF81858C),
        ("OnAccentFg",         0xFFFFFFFF, 0xFFFFFFFF),
        // 强调色（DeepSeek 蓝 #4D6BFE）：只做强调面（按钮底、选择高亮、进度图形），
        // 文字仍走 TextPrimary/TextSecondary 的近黑/白，深色与浅色主题下都能读。
        ("AccentBrush",        0xFF4D6BFE, 0xFF4D6BFE),
        ("AccentHoverBrush",   0xFF5E7BFF, 0xFF5E7BFF),
        ("AccentPressedBrush", 0xFF3A55E0, 0xFF3A55E0),
        ("GreenBrush",         0xFF34D399, 0xFF22C55E),
        ("AmberBrush",         0xFFFBBF24, 0xFFF59E0B),
        ("RedBrush",           0xFFF87171, 0xFFEC1313),
        ("GrayBrush",          0xFF8A8D88, 0xFF81858C),
        // 次要按钮：对齐网页端「无边框 + 抬升底色」（深色用 layer-2/layer-3，
        // 浅色各层都是白，只能靠一圈描边区分）
        ("GhostBg",            0xFF2C2C2E, 0xFFFFFFFF),
        ("GhostBorder",        0x002C2C2E, 0x1A000000),
        ("GhostHoverBg",       0xFF353638, 0xFFF1F3F5),
        ("GhostHoverBorder",   0x00353638, 0x29000000),
        ("GhostPressedBg",     0xFF3E3F42, 0xFFE9ECF2),
        // 危险按钮：网页端 danger 是纯色底，不描边
        ("DangerBg",           0x26F25A5A, 0xFFFEF2F2),
        ("DangerFg",           0xFFFF8A8A, 0xFFEC1313),
        ("DangerBorder",       0x00F25A5A, 0x00EC1313),
        ("DangerHoverBg",      0x40F25A5A, 0xFFFEE2E2),
        ("DangerHoverBorder",  0x00F25A5A, 0x00EC1313),
        ("DangerPressedBg",    0x59F25A5A, 0xFFFECACA),
        // 标题栏按钮
        ("TitleHoverBg",       0x1FFFFFFF, 0x14000000),
        ("TitlePressedBg",     0x29FFFFFF, 0x1F000000),
        // 版本徽标
        ("BadgeNeutralBg",     0xFF1C212B, 0xFFF1F3F5),
        ("BadgeNeutralFg",     0xFF8A94A6, 0xFF61666B),
        ("BadgeGreenBg",       0xFF0E2B22, 0xFFDCFCE7),
        ("BadgeGreenFg",       0xFF34D399, 0xFF15803D),
        ("BadgeAmberBg",       0xFF2E2510, 0xFFFEF3C7),
        ("BadgeAmberFg",       0xFFFBBF24, 0xFFB45309),
        // 本地 link: 插件：随 DSH 目录一起升级，没有 npm 版本可比较。
        // 用蓝色信息态，避免灰色“离线”读起来像插件没跑起来。
        ("BadgeBlueBg",        0xFF10243A, 0xFFDBEAFE),
        ("BadgeBlueFg",        0xFF60A5FA, 0xFF1D4ED8),
        // 文本选择与右键菜单
        ("SelectionBg",        0x664D6BFE, 0x664D6BFE),
        ("MenuBg",             0xFF232324, 0xFFFFFFFF),
        ("MenuBorder",         0x29FFFFFF, 0x29000000),
        ("MenuHoverBg",        0xFF2C2C2E, 0xFFF1F3F5),
        ("MenuFg",             0xFFF9FAFB, 0xFF0F1115),
        ("MenuSeparator",      0x1FFFFFFF, 0x1A000000),

        // =================================================================
        //  启动屏（boot plate）专用色：每一条都是 (深色, 浅色)。
        //  要点：
        //   * 强调色 #4D6BFE（DeepSeek 蓝）是主色，但**只做点缀**：进度轨、
        //     渐变扫光、百分比强调、细线高亮；绝不铺满背景；
        //   * 启动屏底色严格二选一：浅色主题纯白 #FFFFFF，
        //     深色主题中性近黑 #0F1115（不是蓝色调）；
        //   * 文字不用强调色：浅色主题近黑，深色主题白/浅灰（可读性优先）；
        //   * 标志默认是矢量：DeepSeek 鲸鱼 Path + "DeepSeek" 字标（见 MainWindow.xaml 的
        //     LoaderWhale / LoaderWordmark），填充色走 BootTextPrimary，随主题自动换色；
        //     若 dsh-brand.json 配了 logoUrl / logoUrlDark，则由 MainWindow.ApplyBootLogo()
        //     换成该图片（位图或 SVG 渲染出的矢量图）并隐藏内置鲸鱼 + 字标。
        // =================================================================
        ("BootBg",             0xFF0F1115, 0xFFFFFFFF),   // 启动屏底色：近黑 / 纯白
        ("BootSurface",        0xFF16181D, 0xFFF6F7F9),   // 迷你日志面板底
        ("BootHairline",       0xFF2A2D34, 0xFFE3E5E9),   // 版式细线（对齐 Logo 边缘）
        ("BootTextPrimary",    0xFFF5F6F7, 0xFF101114),   // 状态主文字 / 标志填充色
        ("BootLogoFallback",   0xFFF5F6F7, 0xFF101114),   // 已不再被引用（原为 Logo 图片加载失败的兜底文字色），键位保留不删
        ("BootTextSecondary",  0xFFA8ADB5, 0xFF5B6069),   // 状态详情 / 产品小字
        ("BootTextDim",        0xFF6E747E, 0xFF8A9099),   // 次要说明
        ("BootSloganFg",       0xFFA8ADB5, 0xFF5B6069),   // 启动屏标语（次级灰）
        ("BootPanelBorder",    0xFF2A2D34, 0xFFE3E5E9),   // 迷你日志面板描边
        ("BootRailTrack",      0xFF23262C, 0xFFE9EBEF),   // 轨道暗底
        ("BootSegmentOff",     0xFF2A2D34, 0xFFE3E5E9),   // 未点亮的分段进度
        ("BootAccentInk",      0xFF8FA6FF, 0xFF4D6BFE),   // 强调文字（深色下用浅蓝保证可读）
        ("BootRailFill",       0xFF4D6BFE, 0xFF4D6BFE),   // 进度实色 = 强调色
        ("BootRailFillEnd",    0xFF8FA6FF, 0xFF5E7BFF),   // 渐变末端：亮蓝（蓝 → 亮蓝扫光）
    };

    // =====================================================================
    //  日志行配色（深色 / 浅色）
    // =====================================================================
    private static readonly (string Key, uint Dark, uint Light)[] LogPalette =
    {
        ("LogDefault", 0xFFCFD3D6, 0xFF3A4046),
        ("LogDim",     0xFF81858C, 0xFF8A9096),
        ("LogInfo",    0xFF7DD3FC, 0xFF0369A1),
        ("LogGood",    0xFF34D399, 0xFF15803D),
        ("LogWarn",    0xFFFBBF24, 0xFFB45309),
        ("LogBad",     0xFFF87171, 0xFFDC2626),
        ("LogAccent",  0xFF8FA6FF, 0xFF4D6BFE),
    };

    // 启动屏里的迷你日志配色：同样跟随深浅主题（浅色下用深字，深色下用浅字），
    // 否则浅色启动屏上会出现“白底白字”。默认/次要两档用中性灰（与 BootText* 同源），
    // 只有 Accent 一档用强调色（DeepSeek 蓝），保持“强调色只做点缀”。
    private static readonly (string Key, uint Dark, uint Light)[] BootLogPalette =
    {
        ("BootLogDefault", 0xFFCFD3D8, 0xFF3A4046),
        ("BootLogDim",     0xFF6E747E, 0xFF8A9099),
        ("BootLogInfo",    0xFF9BB8FF, 0xFF0369A1),
        ("BootLogGood",    0xFF34D399, 0xFF15803D),
        ("BootLogWarn",    0xFFFBBF24, 0xFFB45309),
        ("BootLogBad",     0xFFF87171, 0xFFDC2626),
        ("BootLogAccent",  0xFF8FA6FF, 0xFF4D6BFE),
    };

    // =====================================================================
    //  实时令牌 -> 本地画刷（页面回传时优先采用，皮肤也能跟随）
    //
    //  这里刻意只映射「不会和原生控制条冲突」的颜色：
    //  * 画布底色 / 文字 / 边框 / 菜单跟着网页（皮肤换色时外壳同步）；
    //  * 顶栏、底栏、日志底 用内置值 —— 它们对齐的是网页最外层侧栏的颜色，
    //    直接用 --dsw-alias-bg-layer-1 会亮一档，在原生条与网页之间露出一道缝。
    // =====================================================================
    private static readonly (string Key, string Token)[] TokenMap =
    {
        ("WindowBg",      "bgBase"),
        ("CardBorder",    "border2"),
        ("MenuBg",        "layer1"),
        ("MenuHoverBg",   "layer2"),
        ("TextPrimary",   "label1"),
        ("TextSecondary", "label2"),
        ("TextDim",       "label3"),
    };

    // =====================================================================
    //  对外画刷（都是共享实例：改色即全局生效，已渲染的日志行也会跟着变）
    // =====================================================================

    public static Brush LogDefault => LogBrush(DshLogKind.Default);
    public static Brush LogDim => LogBrush(DshLogKind.Dim);
    public static Brush LogInfo => LogBrush(DshLogKind.Info);
    public static Brush LogGood => LogBrush(DshLogKind.Good);
    public static Brush LogWarn => LogBrush(DshLogKind.Warn);
    public static Brush LogBad => LogBrush(DshLogKind.Bad);
    public static Brush LogAccent => LogBrush(DshLogKind.Accent);

    public static Brush BadgeGreenFg => Shared("BadgeGreenFg");
    public static Brush BadgeGreenBg => Shared("BadgeGreenBg");
    public static Brush BadgeAmberFg => Shared("BadgeAmberFg");
    public static Brush BadgeAmberBg => Shared("BadgeAmberBg");
    public static Brush BadgeBlueFg => Shared("BadgeBlueFg");
    public static Brush BadgeBlueBg => Shared("BadgeBlueBg");
    public static Brush BadgeGrayFg => Shared("BadgeNeutralFg");
    public static Brush BadgeGrayBg => Shared("BadgeNeutralBg");
    public static Brush Accent => Shared("AccentBrush");
    public static Brush Green => Shared("GreenBrush");

    private static string LogKey(DshLogKind kind) => kind switch
    {
        DshLogKind.Dim => "LogDim",
        DshLogKind.Info => "LogInfo",
        DshLogKind.Good => "LogGood",
        DshLogKind.Warn => "LogWarn",
        DshLogKind.Bad => "LogBad",
        DshLogKind.Accent => "LogAccent",
        _ => "LogDefault",
    };

    private static string BootLogKey(DshLogKind kind) => kind switch
    {
        DshLogKind.Dim => "BootLogDim",
        DshLogKind.Info => "BootLogInfo",
        DshLogKind.Good => "BootLogGood",
        DshLogKind.Warn => "BootLogWarn",
        DshLogKind.Bad => "BootLogBad",
        DshLogKind.Accent => "BootLogAccent",
        _ => "BootLogDefault",
    };

    public static Brush LogBrush(DshLogKind kind) => Shared(LogKey(kind));

    public static Brush BootLogBrush(DshLogKind kind) => Shared(BootLogKey(kind));

    /// <summary>
    /// 取调色板里某个键当前的实际颜色。
    /// 启动屏的“扫光”是渐变（蓝 → 亮蓝），渐变画刷没法用 DynamicResource
    /// 逐段换色，所以在代码里按资源键取色现拼 LinearGradientBrush —— 颜色值仍然
    /// 只来自上面的调色板表，XAML / 代码里都不写死颜色。
    /// </summary>
    public static Color ColorOf(string key)
    {
        var res = Application.Current?.Resources;
        if (res?[key] is SolidColorBrush brush) return brush.Color;
        return Colors.Transparent;
    }

    // =====================================================================
    //  品牌强调色覆盖（来自 <DSH_HOME>\dsh-brand.json 的 color / colorDark）
    //
    //  只覆盖启动屏的三个强调色键，外壳其它配色一律不动：
    //   * BootRailFill    → 品牌色本体：竖向进度轨填充 + 仪表刻度（LoaderTick）；
    //   * BootRailFillEnd → 品牌色「亮一档」：渐变扫光末端 + 100% 后的横向 wipe 幕布；
    //   * BootAccentInk   → 百分比数字（深色主题下用亮一档保证可读）。
    //
    //  轨道渐变（LoaderFill / LoaderWipe / BootWipeMask）是 MainWindow.ApplyBootGradients()
    //  用 ColorOf() 现拼的 LinearGradientBrush，所以这里改完资源，调用方重建渐变即可生效。
    //  null = 该主题沿用内置默认色（json 里 color / colorDark 都留空）。
    // =====================================================================
    private static Color? _brandAccentLight;
    private static Color? _brandAccentDark;

    /// <summary>
    /// 写入品牌强调色。<paramref name="light"/> / <paramref name="dark"/> 已经由调用方
    /// 按主题解析好（暗色留空＝跟随浅色，与 Web 端 dsh-brand 的规则一致）；null = 该主题内置默认。
    /// </summary>
    public static void SetBrandAccent(Color? light, Color? dark)
    {
        _brandAccentLight = light;
        _brandAccentDark = dark;
    }

    /// <summary>当前主题下实际生效的品牌强调色（未配置 = null，表示仍在用内置默认色）。</summary>
    public static Color? BrandAccent(DsThemeKind kind)
        => kind == DsThemeKind.Dark ? _brandAccentDark : _brandAccentLight;

    /// <summary>
    /// 命中启动屏三个强调色键时改写颜色并返回 true；其余键返回 false（不动）。
    /// 「亮一档」的插值比例照抄内置调色板自身的颜色关系，这样**默认演示**
    /// （color=#4D6BFE / colorDark=#9BB8FF）算出来的渐变末端与原来几乎逐像素一致：
    ///   深色 #4D6BFE → #8FA6FF ≈ 提亮 0.40；浅色 #4D6BFE → #5E7BFF ≈ 提亮 0.10。
    /// </summary>
    private static bool BrandOverride(string key, DsThemeKind kind, ref Color color)
    {
        var accent = kind == DsThemeKind.Dark ? _brandAccentDark : _brandAccentLight;
        if (accent is null) return false;
        double lift = kind == DsThemeKind.Dark ? 0.40 : 0.10;
        switch (key)
        {
            case "BootRailFill":
                color = accent.Value;
                return true;
            case "BootRailFillEnd":
                color = Lighten(accent.Value, lift);
                return true;
            case "BootAccentInk":
                color = kind == DsThemeKind.Dark ? Lighten(accent.Value, lift) : accent.Value;
                return true;
            default:
                return false;
        }
    }

    /// <summary>把颜色朝白色插值，得到同色系的「亮一档」（保持色相，只提亮度）。</summary>
    public static Color Lighten(Color color, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            color.A,
            (byte)Math.Round(color.R + (255 - color.R) * amount),
            (byte)Math.Round(color.G + (255 - color.G) * amount),
            (byte)Math.Round(color.B + (255 - color.B) * amount));
    }

    // =====================================================================
    //  应用主题
    // =====================================================================

    /// <summary>
    /// 应用主题。<paramref name="tokens"/> 来自页面（可空）：
    /// 命中的令牌会覆盖内置调色板，实现与 WebUI（含皮肤）逐像素对齐。
    /// </summary>
    /// <returns>是否发生了可见变化。</returns>
    public static bool Apply(DsThemeKind kind, IReadOnlyDictionary<string, Color>? tokens = null)
    {
        var res = Application.Current?.Resources;
        if (res is null) return false;

        bool changed = Current != kind;
        Current = kind;

        foreach (var (key, dark, light) in Palette)
        {
            var color = FromArgb(kind == DsThemeKind.Dark ? dark : light);
            if (tokens is not null && TryToken(key, tokens, out var tc)) color = tc;
            // 品牌强调色覆盖：只命中启动屏的那三个键（json 里配了 color / colorDark 才生效）
            _ = BrandOverride(key, kind, ref color);
            if (Set(res, key, color)) changed = true;
        }

        foreach (var (key, dark, light) in LogPalette)
            if (Set(res, key, FromArgb(kind == DsThemeKind.Dark ? dark : light))) changed = true;

        foreach (var (key, dark, light) in BootLogPalette)
            if (Set(res, key, FromArgb(kind == DsThemeKind.Dark ? dark : light))) changed = true;

        if (changed) Changed?.Invoke(kind);
        return changed;
    }

    private static bool TryToken(string key, IReadOnlyDictionary<string, Color> tokens, out Color color)
    {
        color = default;
        foreach (var (k, token) in TokenMap)
            if (k == key && tokens.TryGetValue(token, out color))
                return true;
        return false;
    }

    // =====================================================================
    //  画刷存取
    // =====================================================================

    /// <summary>取（必要时新建）应用资源里的共享画刷。</summary>
    private static SolidColorBrush Shared(string key)
    {
        if (Cache.TryGetValue(key, out var cached) && !cached.IsFrozen) return cached;

        var res = Application.Current?.Resources;
        if (res?[key] is SolidColorBrush existing)
        {
            Cache[key] = existing;
            return existing;
        }

        var created = new SolidColorBrush(Colors.Transparent);
        if (res is not null) res[key] = created;
        Cache[key] = created;
        return created;
    }

    /// <summary>
    /// 写入一个资源画刷。XAML 里声明的画刷会被 WPF 冻结，改色无效，
    /// 这种情况只能换成新的可写实例（XAML 侧必须用 DynamicResource 才会重新求值）；
    /// 代码里创建的画刷（日志配色等）则就地改色，已渲染的 Run 也会跟着变。
    /// </summary>
    private static bool Set(ResourceDictionary res, string key, Color color)
    {
        if (res[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            Cache[key] = brush;
            if (brush.Color == color) return false;
            brush.Color = color;
            return true;
        }

        // 资源缺失（或极少数被冻结的情况）：换一个可写实例。
        var fresh = new SolidColorBrush(color);
        res[key] = fresh;
        Cache[key] = fresh;
        return true;
    }

    private static Color FromArgb(uint v)
        => Color.FromArgb((byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));

    // =====================================================================
    //  CSS 颜色解析（页面回传的是 CSS 计算值）
    // =====================================================================

    public static bool TryParseCssColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase)) { color = Colors.Transparent; return true; }
        if (s.StartsWith("var(", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.StartsWith("color-mix(", StringComparison.OrdinalIgnoreCase)) return false;

        if (s[0] == '#')
        {
            var hex = s[1..];
            if (hex.Length == 3 || hex.Length == 4)
            {
                var expanded = string.Empty;
                foreach (var ch in hex) expanded += new string(ch, 2);
                hex = expanded;
            }
            if (hex.Length != 6 && hex.Length != 8) return false;
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
            if (hex.Length == 6)
            {
                color = Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
            }
            else
            {
                // CSS 是 #RRGGBBAA，WPF 是 #AARRGGBB
                byte r = (byte)((v >> 24) & 0xFF), g = (byte)((v >> 16) & 0xFF), b = (byte)((v >> 8) & 0xFF), a = (byte)(v & 0xFF);
                color = Color.FromArgb(a, r, g, b);
            }
            return true;
        }

        if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            int open = s.IndexOf('('), close = s.LastIndexOf(')');
            if (open < 0 || close <= open) return false;
            var parts = s[(open + 1)..close].Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!TryChannel(parts[0], out var r) || !TryChannel(parts[1], out var g) || !TryChannel(parts[2], out var b)) return false;
            byte a = 255;
            if (parts.Length >= 4)
            {
                var at = parts[3];
                if (at.EndsWith("%", StringComparison.Ordinal))
                {
                    if (double.TryParse(at[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                        a = (byte)Math.Clamp(Math.Round(pct * 2.55), 0, 255);
                }
                else if (double.TryParse(at, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
                {
                    a = (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);
                }
            }
            color = Color.FromArgb(a, r, g, b);
            return true;
        }

        return false;
    }

    private static bool TryChannel(string text, out byte value)
    {
        value = 0;
        if (text.EndsWith("%", StringComparison.Ordinal))
        {
            if (!double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return false;
            value = (byte)Math.Clamp(Math.Round(pct * 2.55), 0, 255);
            return true;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return false;
        value = (byte)Math.Clamp(Math.Round(d), 0, 255);
        return true;
    }

    // =====================================================================
    //  Windows 主题
    // =====================================================================

    /// <summary>读注册表：应用界面是否为浅色（AppsUseLightTheme）。</summary>
    public static bool OsAppsLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            return v is int iv ? iv == 1 : true;
        }
        catch { return true; }
    }

    /// <summary>
    /// 任务栏/窗口图标用系统主题（SystemUsesLightTheme）判定：
    /// 图标贴在任务栏上，跟随系统而非应用界面更不容易“白鲸撞白底”。
    /// </summary>
    public static bool OsSystemLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("SystemUsesLightTheme");
            return v is int iv ? iv == 1 : OsAppsLight();
        }
        catch { return OsAppsLight(); }
    }

    /// <summary>环境变量 DSH_DESKTOP_THEME=light|dark 可强制主题（排查/截图用）。</summary>
    public static DsThemeKind? Forced()
    {
        var v = Environment.GetEnvironmentVariable("DSH_DESKTOP_THEME");
        if (string.IsNullOrWhiteSpace(v)) return null;
        return v.Trim().ToLowerInvariant() switch
        {
            "light" => DsThemeKind.Light,
            "dark" => DsThemeKind.Dark,
            _ => null,
        };
    }
}
