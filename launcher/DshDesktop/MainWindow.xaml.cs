using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DshDesktopEngine;
using Microsoft.Web.WebView2.Core;

namespace DshDesktop;

/// <summary>一条日志。颜色不缓存，按当前主题从共享画刷取（切主题时已渲染的行也会一起更新）。</summary>
public sealed class DsLogLine
{
    public string Text { get; }
    public DshLogKind Kind { get; }

    public DsLogLine(string text, DshLogKind kind) { Text = text; Kind = kind; }

    /// <summary>主日志配色（跟随界面主题）。</summary>
    public Brush Brush => DsTheme.LogBrush(Kind);

    /// <summary>启动遮罩里的迷你日志：跟随深浅主题（BootLog* 资源键，深/浅两套）。</summary>
    public Brush BootBrush => DsTheme.BootLogBrush(Kind);
}

public partial class MainWindow : Window
{
    private readonly DshCore _core;
    private readonly DispatcherTimer _statusTimer;
    private bool _allowClose;
    private bool _closePromptOpen;
    private bool _stoppingForExit;
    private bool _webViewReady;
    private int _navigationRequest;
    /// <summary>认证失败后自动重启服务的次数（上限 1，避免 401 → 重启 → 401 循环）。</summary>
    private int _authRecoveryAttempts;
    /// <summary>最近一次被判为未认证的地址，用来判断重新取到的 token 是否已经换过。</summary>
    private string? _unauthorizedUrl;
    // 外部链接去重：NewWindowRequested 与 NavigationStarting 可能对同一链接各触发一次，
    // 用短时间窗内同 URL 只开一次，避免弹两个相同标签页。
    private readonly HashSet<string> _openedExternally = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _openedExternalWindow = DateTime.MinValue;

    // DSH boot-plate loader (rail progress + sweep/fade finish)
    private readonly DispatcherTimer _loaderTimer;
    private readonly DispatcherTimer _finishTimer;
    private readonly DispatcherTimer _balanceTimer;
    private bool _balanceBusy;
    private DateTime _finishStartUtc;
    private bool _loaderRunning;
    private bool _finishing;

    // ---------------------------------------------------------------------
    //  DSH 启动屏（boot plate）
    // ---------------------------------------------------------------------

    /// <summary>
    /// 启动屏标语（画面中央标志下方那两行）。
    ///
    /// 排版：第二行以 "but" 开头 —— 常量里用一个 \n 手动断行，
    /// XAML 侧 LoaderSlogan 是 TextAlignment=Center + TextWrapping=Wrap。
    /// 运行时由 ApplyBootSlogan() 写进 XAML 里的 LoaderSlogan（x:Name="LoaderSlogan"）。
    /// 【要换标语：只改这一个常量即可】
    /// </summary>
    private const string BootPurposeSlogan =
        "Idealism is that you will probably never receive something back,\n" +
        "but nonetheless still decide to give.";

    /// <summary>左侧进度轨宽度（XAML 里 LoaderTrack/LoaderFill/LoaderWipe 同宽）。</summary>
    private const double BootRailWidth = 8;

    /// <summary>
    /// 仪表组（刻度 + 百分比 + 状态）的左边距：轨道宽 8 + 间隙 16 = 24。
    /// 轨道贴窗口左边、不内缩，仪表组紧贴轨道右侧（"数字与轨道对齐"就是靠这个值；
    /// 改轨道宽度要同步这里，XAML 里 LoaderMeter 的初始 Margin 也是 24）。
    /// </summary>
    private const double BootGutter = 24;



    /// <summary>
    /// 验收开关：环境变量 DSH_DESKTOP_SPLASH_HOLD_MS（正整数，毫秒）。
    /// 设了就表示「进度到 100% 后再多停留这么多毫秒才淡出」，方便截图看清启动屏；
    /// 未设置 / 非法值 = 0 = 现有行为完全不变。
    /// </summary>
    private static readonly int BootExtraHoldMs = ReadBootExtraHoldMs();

    private static int ReadBootExtraHoldMs()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("DSH_DESKTOP_SPLASH_HOLD_MS");
            if (int.TryParse(raw, out int ms) && ms > 0) return Math.Min(ms, 600_000); // 上限 10 分钟，防呆
        }
        catch { /* 读环境变量失败就当没设 */ }
        return 0;
    }

    /// <summary>
    /// 启动遮罩的最短可见时长：暖启动时页面几百毫秒就绪，没有这条下限启动屏会
    /// 一闪而过（用户根本看不到品牌画面）。到 100% 后仍会走淡出，不会突然消失。
    /// </summary>
    private const double SplashMinVisibleMs = 1500;

    /// <summary>遮罩变可见的时刻，用来算「已亮时长」，保证最短可见时长的下限。</summary>
    private DateTime _overlayShownUtc;

    // ---------------------------------------------------------------------
    //  阶段驱动进度（不再是固定时间轴）
    //  _loaderTarget 由真实里程碑推进（SetBootStage），_loaderProgress 每 33ms
    //  向它逼近 —— 用户看到的是「在爬」，而不是开局就 99% 干等。
    // ---------------------------------------------------------------------

    /// <summary>当前显示的进度百分比（0~100，永不回退、永不超过 _loaderTarget）。</summary>
    private double _loaderProgress;

    /// <summary>当前阶段的目标百分比（真实里程碑推进；未完成前上限 StageCap）。</summary>
    private double _loaderTarget;

    /// <summary>
    /// 未完成（FinishLoading）之前进度能爬到的上限：留一段余量，
    /// 保证真实加载没结束就绝不会显示 100%，用户能看出它还在走。
    /// </summary>
    private const double BootStageCap = 92;

    /// <summary>每帧最小爬升量（百分比）：网络/磁盘很快时进度条也不会「跳」过去。</summary>
    private const double BootProgressMinStep = 0.35;

    /// <summary>每帧向目标逼近的比例（指数逼近，越接近目标越慢）。</summary>
    private const double BootProgressApproach = 0.10;

    // ---------------------------------------------------------------------
    //  100% 之后的强调色横向 wipe 转场（取代原来的整体淡出）
    //  幕布 BootWipeMask 在 XAML 里、默认 Collapsed：Width 从 0 展开盖满整屏，
    //  再整体向右滑出，把下面已经加载好的主界面「刷」出来。
    // ---------------------------------------------------------------------

    /// <summary>覆盖段 / 露出段各自的时长（合计约 0.84s，控制在 0.9s 内）。</summary>
    private const double BootWipeCoverMs = 420;
    private const double BootWipeRevealMs = 420;

    /// <summary>wipe 阶段：0 = 未开始，1 = 覆盖段，2 = 露出段。</summary>
    private int _bootWipePhase;

    /// <summary>
    /// 验收开关：环境变量 DSH_DESKTOP_SPLASH_PREVIEW_MS（正整数，毫秒）。
    /// 设了就表示「启动屏从亮起那一刻起至少保持可见这么久」——期间连接成功/失败/401
    /// 都不会把它隐藏，到时再走正常流程（若那时已到 100% 则正常淡出）。
    /// 服务不可用时也能稳定看清启动画面，专门给截图验收用。
    /// 未设置 / 非法值 = 0 = 现有行为完全不变。
    /// </summary>
    private static readonly int BootPreviewMs = ReadBootPreviewMs();

    private static int ReadBootPreviewMs()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("DSH_DESKTOP_SPLASH_PREVIEW_MS");
            if (int.TryParse(raw, out int ms) && ms > 0) return Math.Min(ms, 600_000); // 上限 10 分钟，防呆
        }
        catch { /* 读环境变量失败就当没设 */ }
        return 0;
    }

    /// <summary>预览期结束的定时器（只在 BootPreviewMs &gt; 0 时创建并启动）。</summary>
    private DispatcherTimer? _splashPreviewTimer;

    /// <summary>预览期内收到的「该收起了」请求：等预览到时再执行。</summary>
    private bool _hideRequestedDuringPreview;


    // 徽标只保存资源键：切主题时靠资源引用自动换色（XAML 里的画刷会被冻结，不能就地改色）
    private static (string fg, string bg) BadgeGreen => ("BadgeGreenFg", "BadgeGreenBg");
    private static (string fg, string bg) BadgeAmber => ("BadgeAmberFg", "BadgeAmberBg");
    private static (string fg, string bg) BadgeGray => ("BadgeNeutralFg", "BadgeNeutralBg");
    private static (string fg, string bg) BadgeBlue => ("BadgeBlueFg", "BadgeBlueBg");

    /// <summary>主日志已写入的行数（行数多了会从头丢弃，这个只用于显示）。</summary>
    private int _logLineCount;

    /// <summary>加载遮罩里的迷你实时日志</summary>
    public ObservableCollection<DsLogLine> LoadingLogLines { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        _core = new DshCore();
        _core.Log = (text, kind) => DispatchLog(text, kind);
        _core.BusyChanged = busy => Dispatcher.BeginInvoke(() => SetBusyUi(busy));
        _core.StatusChanged = () => Dispatcher.BeginInvoke(RefreshStatusUi);
        _core.VersionsChanged = () => Dispatcher.BeginInvoke(ApplyVersionsUi);

        var ver = typeof(MainWindow).Assembly.GetName().Version;
        // 版本号不再放顶栏（和网页品牌挤在一起很碎），收到「版本信息」面板里
        RowShellVer.Text = "桌面版 v" + (ver == null ? "1.0.0" : ver.ToString(3));

        // 主题：先用 Windows 深浅色落地（页面还没加载），WebView 起来后由页面回传的真实主题接管
        DsTheme.Apply(DsTheme.Forced() ?? (DsTheme.OsAppsLight() ? DsThemeKind.Light : DsThemeKind.Dark));
        DsTheme.Changed += OnThemeChanged;
        ApplyWindowIcon();

        // DSH 启动屏：DeepSeek 鲸鱼标志（矢量，随主题自动换色）+ 标语
        // + 几何分段格 + 渐变扫光（都放在主题落地之后，保证第一帧就是当前深浅色）

        ApplyBootSlogan();
        ApplyBootGradients();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshStatusUi();

        _loaderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _loaderTimer.Tick += (_, _) => LoaderTick_Advance();
        _finishTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _finishTimer.Tick += (_, _) => FinishTick();

        // 启动屏从第一帧就亮起来（不依赖 Loaded 那条链路），页面就绪后由 FinishLoading
        // → 强调色横向 wipe 交棒；Loaded → EnsureServerAndLoadAsync 只负责推进阶段与连接。
        // 注意：必须放在上面两个定时器建好之后 —— StartLoader() 会用到 _loaderTimer。
        ShowLoading("正在启动 DeepSeek Harness…", "正在检查环境并连接工作台");
        // 阶段驱动进度的取值来源（验收用）：hold / preview 都在这行日志里
        LogLine($"[启动屏] 阶段驱动进度已启用；hold={BootExtraHoldMs}ms preview={BootPreviewMs}ms",
                DshLogKind.Dim);

        // DSH_DESKTOP_SPLASH_PREVIEW_MS：预览期内不因连接成功/失败而隐藏遮罩
        if (BootPreviewMs > 0) ArmSplashPreview();

        // 日志：主日志是 RichTextBox（可拖选复制），迷你日志走绑定
        DataContext = this;
        SizeChanged += (_, e) =>
        {
            ApplyCompactLayout(e.NewSize.Width);
            // wipe 期间改窗口大小：按新宽度重算幕布几何，别露出一条边
            if (_bootWipePhase != 0) SyncBootWipeGeometry();
        };

        // 官方余额：启动查一次 → 平时每 5 分钟一次 → 切回窗口若已过期 60 秒则立刻补一次
        // （查询失败时自动降到 1 分钟重试，成功后回到 5 分钟）
        _balanceTimer = new DispatcherTimer { Interval = BalanceIntervalOk };
        _balanceTimer.Tick += (_, _) => _ = RefreshBalanceAsync();
        Activated += (_, _) =>
        {
            if (!_balanceBusy && DateTime.UtcNow - _balanceLastAttemptUtc > BalanceStaleAfter)
                _ = RefreshBalanceAsync();
        };

        Loaded += async (_, _) =>
        {
            ClampWindowIntoWorkArea();
            ApplyCompactLayout(ActualWidth);
            LogLine("— DeepSeek Harness 桌面版已启动 —", DshLogKind.Dim);
            LogLine($"安装目录：{_core.Root}", DshLogKind.Default);
            _core.PrepareEnvironment();
            RefreshStatusUi();
            _statusTimer.Start();
            _balanceTimer.Start();
            _ = _core.LoadVersionsAsync();
            _ = RefreshBalanceAsync();
            await EnsureServerAndLoadAsync();
        };

        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _balanceTimer.Stop();
            DsTheme.Changed -= OnThemeChanged;
        };
    }

    // =====================================================================
    //  DeepSeek 官方余额（底栏右下角）
    // =====================================================================

    /// <summary>正常刷新间隔（余额随时在变，但不值得打太勤）。</summary>
    private static readonly TimeSpan BalanceIntervalOk = TimeSpan.FromMinutes(5);

    /// <summary>查询失败后的重试间隔（网络抖动 / 401 时别干等 5 分钟）。</summary>
    private static readonly TimeSpan BalanceIntervalRetry = TimeSpan.FromMinutes(1);

    /// <summary>切回窗口时，距上次查询超过这个时间就顺手补一次。</summary>
    private static readonly TimeSpan BalanceStaleAfter = TimeSpan.FromSeconds(60);

    private DateTime _balanceLastAttemptUtc = DateTime.MinValue;

    private void Balance_Click(object sender, RoutedEventArgs e) => _ = RefreshBalanceAsync();

    private async Task RefreshBalanceAsync()
    {
        if (_balanceBusy) return;
        _balanceBusy = true;
        _balanceLastAttemptUtc = DateTime.UtcNow;
        try
        {
            var key = DeepSeekBalance.ReadApiKey(_core.Root);
            if (string.IsNullOrEmpty(key))
            {
                // 没配密钥：不是临时故障，按正常节奏重查即可（用户补上 .env 后最多 5 分钟内生效）
                _balanceTimer.Interval = BalanceIntervalOk;
                SetBalance("—", "TextDim",
                    "DeepSeek 官方余额不可用\n未找到 DEEPSEEK_API_KEY（app-npm\\.env）");
                return;
            }

            var (info, error) = await DeepSeekBalance.QueryAsync(key, CancellationToken.None);
            if (info is null)
            {
                _balanceTimer.Interval = BalanceIntervalRetry;
                SetBalance("—", "TextDim", $"DeepSeek 官方余额不可用\n{error}\n点击重试");
                LogLine($"[余额] 查询失败：{error}", DshLogKind.Dim);
                return;
            }

            _balanceTimer.Interval = BalanceIntervalOk;
            bool low = !info.Available || info.Total < 10m;
            var tip = $"DeepSeek 官方账户余额\n{info.Breakdown}\n"
                      + (info.Available ? "" : "余额不足，API 调用会被拒绝\n")
                      + $"更新于 {DateTime.Now:HH:mm:ss} · 每 5 分钟自动刷新，点击立即刷新";
            SetBalance(info.Display, low ? "AmberBrush" : "TextSecondary", tip);
        }
        catch (Exception ex)
        {
            _balanceTimer.Interval = BalanceIntervalRetry;
            SetBalance("—", "TextDim", $"DeepSeek 官方余额不可用\n{ex.Message}\n点击重试");
        }
        finally
        {
            _balanceBusy = false;
        }
    }

    private void SetBalance(string text, string brushKey, string tooltip)
    {
        BalanceText.Text = text;
        BalanceChip.SetResourceReference(Control.ForegroundProperty, brushKey);
        BalanceChip.ToolTip = new TextBlock { Text = tooltip, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
    }

    // =====================================================================
    //  Logging
    // =====================================================================

    private void DispatchLog(string text, DshLogKind kind)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AddLog(text, kind));
            return;
        }
        AddLog(text, kind);
    }

    private void AddLog(string text, DshLogKind kind)
    {
        var line = new DsLogLine(string.IsNullOrEmpty(text) ? " " : text, kind);

        // 主日志：追加到 RichTextBox（RichTextBox 自带选择/复制，块数到顶后从头裁掉）
        try
        {
            var paragraph = new System.Windows.Documents.Paragraph(
                new System.Windows.Documents.Run(line.Text) { Foreground = line.Brush })
            {
                Margin = new Thickness(0, 0, 0, 3),
            };
            LogDoc.Blocks.Add(paragraph);
            while (LogDoc.Blocks.Count > 3000) LogDoc.Blocks.Remove(LogDoc.Blocks.FirstBlock);
        }
        catch { /* 日志渲染失败绝不能影响主流程 */ }

        _logLineCount++;
        LogCountText.Text = $"{_logLineCount} 行";
        // 正在拖选时不抢滚动位置，否则选到一半会被拽到底部
        if (LogPanel.Visibility == Visibility.Visible && LogBox.Selection.IsEmpty) LogBox.ScrollToEnd();

        if (!string.IsNullOrWhiteSpace(text))
            FooterText.Text = text.Length > 160 ? text[..160] : text;

        // 加载遮罩可见时，同步到迷你日志（深/浅两套配色由 DsLogLine.BootBrush 决定）
        if (LoadingOverlay.Visibility == Visibility.Visible)
        {
            LoadingLogLines.Add(line);
            while (LoadingLogLines.Count > 6) LoadingLogLines.RemoveAt(0);
            LoadingLogScroll?.ScrollToEnd();
        }
    }

    private void LogLine(string text, DshLogKind kind)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AddLog(text, kind)); return; }
        AddLog(text, kind);
    }

    // =====================================================================
    //  UI state
    // =====================================================================

    private void SetBusyUi(bool busy)
    {
        BtnStart.IsEnabled = !busy;
        BtnStop.IsEnabled = !busy;
        BtnRestart.IsEnabled = !busy;
        BtnUpdate.IsEnabled = !busy;
        BtnCheckUpdate.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }

    private void RefreshStatusUi()
    {
        // 页面还没回传主题时，按系统深浅色刷新（切主题后任务栏/Alt+Tab 图标自动换）
        if (!_pageThemeValid) RefreshThemeFromOs();

        bool running = _core.IsRunning(out var pid);
        // 资源引用：切主题后状态点颜色自动跟着换
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, running ? "GreenBrush" : "AccentBrush");
        StatusText.Text = running ? "运行中" : "未运行";
        StatusDetailText.Text = running
            ? $"http://127.0.0.1:{DshCore.Port} · PID {pid?.ToString() ?? "?"}"
            : $"http://127.0.0.1:{DshCore.Port}";
        BtnOpenExternal.IsEnabled = running;
    }

    // =====================================================================
    //  主题：外壳跟随内嵌 DSH 界面（WebUI）的深浅色
    // =====================================================================

    private static ImageSource? _iconDark;
    private static ImageSource? _iconLight;

    /// <summary>页面是否已经回传过主题（回传后以页面为准，注册表只做兜底）。</summary>
    private bool _pageThemeValid;

    /// <summary>DSH_DESKTOP_DEBUG_THEME=1 时把页面回传的主题/令牌写进日志（排查用）。</summary>
    private readonly bool _debugTheme =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DSH_DESKTOP_DEBUG_THEME"));

    private static ImageSource LoadPackIcon(string name)
    {
        var img = new System.Windows.Media.Imaging.BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri($"pack://application:,,,/{name}", UriKind.Absolute);
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private void OnThemeChanged(DsThemeKind kind)
    {
        ApplyWindowIcon();
        // 启动屏的标志是矢量（随主题自动换色），这里只需重建代码现拼的渐变。

        // 启动屏的渐变扫光是代码现拼的 LinearGradientBrush，不会跟着 DynamicResource
        // 自动换色，必须在这里重建；分段格子的颜色用的是资源引用，会自动更新。
        ApplyBootGradients();
    }

    /// <summary>
    /// 窗口/任务栏图标用系统（任务栏）主题判定：
    /// 图标贴在任务栏上，跟随 SystemUsesLightTheme 才不会“深色图撞浅色栏”。
    /// 深色用原图（黑底），浅色用把黑底换成白底的那版（IconGen --character 生成）。
    /// </summary>
    private void ApplyWindowIcon()
    {
        try
        {
            _iconDark ??= LoadPackIcon("icon-dark-256.png");
            _iconLight ??= LoadPackIcon("icon-light-256.png");
            Icon = DsTheme.OsSystemLight() ? _iconLight : _iconDark;
        }
        catch
        {
            // 读取失败时保持默认（XAML 里声明的 icon-dark-256.png）
        }
    }

    /// <summary>按 Windows 深浅色应用主题（页面加载前的兜底，也是页面不可用时的来源）。</summary>
    private void RefreshThemeFromOs()
    {
        DsTheme.Apply(DsTheme.Forced() ?? (DsTheme.OsAppsLight() ? DsThemeKind.Light : DsThemeKind.Dark));
        ApplyWindowIcon();
    }

    /// <summary>
    /// 应用页面回传的主题：<paramref name="dark"/> 来自 body[data-ds-dark-theme]，
    /// tokens 是页面上的实时设计令牌（含皮肤），命中即用于外壳，做到与 WebUI 逐像素一致。
    /// </summary>
    private void ApplyPageTheme(bool dark, Dictionary<string, Color> tokens)
    {
        if (_debugTheme)
        {
            var dump = tokens.Count == 0
                ? "（页面未提供设计令牌）"
                : string.Join(" ", tokens.Select(kv => $"{kv.Key}={kv.Value}"));
            LogLine($"[主题·调试] 页面回传 dark={dark} {dump}", DshLogKind.Dim);
        }

        if (DsTheme.Forced() is not null) return; // 调试覆盖优先
        bool first = !_pageThemeValid;
        bool kindChanged = DsTheme.Current != (dark ? DsThemeKind.Dark : DsThemeKind.Light);
        _pageThemeValid = true;

        if (DsTheme.Apply(dark ? DsThemeKind.Dark : DsThemeKind.Light, tokens))
        {
            ApplyWindowIcon();
            if (first || kindChanged)
                LogLine($"[主题] 外壳已跟随界面：{(dark ? "深色" : "浅色")}", DshLogKind.Dim);
        }
    }

    private void ApplyVersionsUi()
    {
        ApplyRow(RowCoreVer, RowCoreBadge, RowCoreBadgeText, _core.CoreLocal, _core.CoreLatest);

        PluginRows.Children.Clear();
        foreach (var p in _core.Plugins)
            AppendPluginRow(p);

        if (_core.PdfReaderReady)
        {
            RowOcrText.Text = "PDF 阅读依赖就绪（Python + pymupdf）";
            SetBadge(RowOcrBadge, RowOcrBadgeText, "就绪", BadgeGreen);
        }
        else
        {
            RowOcrText.Text = "缺失（需要 Python 3 + pymupdf）";
            SetBadge(RowOcrBadge, RowOcrBadgeText, "缺失", BadgeAmber);
        }

        BtnUpdateText.Text = _core.HasUpdate ? "立即更新（有新版本）" : "立即更新";
        BtnUpdate.Opacity = _core.HasUpdate ? 1.0 : 0.7;
        if (_compact == true) BtnUpdate.ToolTip = BtnUpdateText.Text;
    }

    /// <summary>按 plugin-track.json 追踪清单动态生成一行插件版本。</summary>
    private void AppendPluginRow(PluginVersionInfo p)
    {
        var row = new Grid { Height = 24 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var metaStyle = TryFindResource("MetaText") as Style;
        var label = new TextBlock { Text = p.Label, VerticalAlignment = VerticalAlignment.Center, Style = metaStyle };
        var ver = new TextBlock { Text = p.Display, VerticalAlignment = VerticalAlignment.Center, Style = metaStyle };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        var badgeText = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        badge.SetResourceReference(Border.BackgroundProperty, BadgeGray.bg);
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, BadgeGray.fg);

        Grid.SetColumn(label, 0);
        Grid.SetColumn(ver, 1);
        Grid.SetColumn(badge, 2);
        row.Children.Add(label);
        row.Children.Add(ver);
        badge.Child = badgeText;
        row.Children.Add(badge);

        switch (p.Badge)
        {
            case DshBadge.Green: SetBadge(badge, badgeText, "已最新", BadgeGreen); break;
            case DshBadge.Amber: SetBadge(badge, badgeText, "发现新版本", BadgeAmber); break;
            // 本地 link: 插件随 DSH 目录一起升级，没有 npm 版本可比较：
            // 用蓝色信息态而不是灰色“离线”，免得看起来像插件没跑起来。
            case DshBadge.Local: SetBadge(badge, badgeText, "本地", BadgeBlue); break;
            default: SetBadge(badge, badgeText, "离线", BadgeGray); break;
        }

        PluginRows.Children.Add(row);
    }

    private static void ApplyRow(TextBlock ver, Border badge, TextBlock badgeText, string? local, string? latest)
    {
        if (latest is null)
        {
            ver.Text = local ?? "—";
            SetBadge(badge, badgeText, "离线", BadgeGray);
            return;
        }
        ver.Text = $"{local}  →  {latest}";
        if (DshCore.CompareVersions(latest, local ?? "0") > 0)
            SetBadge(badge, badgeText, "发现新版本", BadgeAmber);
        else
            SetBadge(badge, badgeText, "已最新", BadgeGreen);
    }

    private static void SetBadge(Border border, TextBlock text, string label, (string fg, string bg) palette)
    {
        text.Text = label;
        // 用资源引用而不是直接赋画刷：切主题时颜色会自动跟着换
        text.SetResourceReference(TextBlock.ForegroundProperty, palette.fg);
        border.SetResourceReference(Border.BackgroundProperty, palette.bg);
    }

    // =====================================================================
    //  Loading overlay + WebView2
    // =====================================================================

    private void ShowLoading(string text, string detail = "")
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowLoading(text, detail)); return; }
        LoadingText.Text = text;
        LoadingDetail.Text = detail;
        // 只盖遮罩，不隐藏 WebView：WebView2 在隐藏状态下初始化会渲染成黑屏
        if (LoadingOverlay.Visibility != Visibility.Visible) _overlayShownUtc = DateTime.UtcNow;
        LoadingOverlay.Visibility = Visibility.Visible;
        StartLoader();
    }

    private void HideLoading()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => HideLoading()); return; }
        // 预览期内不允许收起：记一笔请求，等 BootPreviewMs 到时由
        // SplashPreview_Elapsed() 继续走正常流程（见 DSH_DESKTOP_SPLASH_PREVIEW_MS）。
        if (IsSplashPreviewActive())
        {
            _hideRequestedDuringPreview = true;
            return;
        }
        LoadingOverlay.Visibility = Visibility.Collapsed;
        StopLoader();
    }

    // =====================================================================
    //  DSH boot-plate loader (rail progress + sweep/fade finish)
    // =====================================================================

    /// <summary>把 BootPurposeSlogan 常量写进启动屏的标语槽位（换标语只改那个常量）。</summary>
    private void ApplyBootSlogan()
    {
        // 标语是两行整句，不再逐字插 hair space（那样会过宽）：
        // 断行由常量里的 \n 控制，居中由 XAML 的 TextAlignment/TextWrapping 控制。
        if (LoaderSlogan is not null) LoaderSlogan.Text = BootPurposeSlogan;
    }

    /// <summary>
    /// 启动屏标志（DeepSeek 鲸鱼 + 字标）是 XAML 里的矢量 Path + TextBlock，
    /// 填充色走 {DynamicResource BootTextPrimary}：切主题由 WPF 自动重新求值，
    /// 所以不再需要按主题换图片文件，也就没有对应的代码路径了。
    /// </summary>

    /// <summary>
    /// 用资源键现拼启动屏的渐变：竖向「蓝 → 亮蓝」轨道进度 + 扫光，
    /// 以及 100% 之后横向 wipe 幕布的「左深右浅」蓝渐变。
    /// 颜色值只来自 Theme.cs 的 BootRailFill / BootRailFillEnd（深/浅两套），
    /// 这里不出现任何写死的色值；换主题时由 OnThemeChanged 重建。
    /// </summary>
    private void ApplyBootGradients()
    {
        var from = DsTheme.ColorOf("BootRailFill");
        var to = DsTheme.ColorOf("BootRailFillEnd");
        if (from == Colors.Transparent && to == Colors.Transparent) return; // 资源还没就绪就保持 XAML 的实色兜底

        if (LoaderFill is not null) LoaderFill.Background = BootRailGradient(from, to);
        if (LoaderWipe is not null) LoaderWipe.Background = BootRailGradient(to, from);
        // wipe 幕布：横向渐变（左深右浅），和轨道渐变同一套颜色来源
        if (BootWipeMask is not null) BootWipeMask.Background = BootWipeGradient(from, to);
    }

    /// <summary>竖向（自下而上）的蓝 → 亮蓝渐变，用于轨道进度与扫光。</summary>
    private static LinearGradientBrush BootRailGradient(Color from, Color to)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 1),
            EndPoint = new Point(0.5, 0),
        };
        brush.GradientStops.Add(new GradientStop(from, 0));
        brush.GradientStops.Add(new GradientStop(to, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 横向（左 → 右）的蓝 → 亮蓝渐变，用于 100% 之后的 wipe 幕布：
    /// 左深右浅能读出「从左边刷过来」的方向感。同样只用传入的颜色，不写死色值。
    /// </summary>
    private static LinearGradientBrush BootWipeGradient(Color from, Color to)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };
        brush.GradientStops.Add(new GradientStop(from, 0));
        brush.GradientStops.Add(new GradientStop(to, 1));
        brush.Freeze();
        return brush;
    }

    private void StartLoader()
    {
        if (_loaderRunning) return;
        _loaderRunning = true;
        // 阶段驱动：进度与目标都从 0 起（第一个真实里程碑由 SetBootStage 给到 8）
        _loaderProgress = 0;
        _loaderTarget = 0;
        if (LoaderFill is not null) LoaderFill.Height = 0;
        if (LoaderWipe is not null) { LoaderWipe.Opacity = 0; LoaderWipe.Width = BootRailWidth; }
        if (LoaderPct is not null) LoaderPct.Text = "0%";
        if (LoaderStatus is not null) LoaderStatus.Text = "Checking...";
        if (LoaderMeter is not null) LoaderMeter.Margin = new Thickness(BootGutter, 0, 0, 0);
        ApplyBootSlogan();

        // wipe 幕布也复位：上次若没走完（异常路径），这里保证下次启动从「未展开」开始
        EndBootWipe(resetOverlayOpacity: true);
        LoadingOverlay.Opacity = 1;
        _loaderTimer.Start();
        SetBootStage(8, "Checking...");   // 遮罩刚亮 = 环境检查中
    }

    private void StopLoader()
    {
        _loaderTimer.Stop();
        _loaderRunning = false;
    }

    /// <summary>
    /// 真实里程碑推进阶段目标：<paramref name="target"/> 与当前目标取大（进度只进不退），
    /// 未完成前上限 <see cref="BootStageCap"/>；遮罩不可见时直接忽略。
    /// </summary>
    private void SetBootStage(int target, string status)
    {
        if (LoadingOverlay is null || LoadingOverlay.Visibility != Visibility.Visible) return;
        double capped = Math.Min(target, BootStageCap);
        if (capped > _loaderTarget) _loaderTarget = capped;
        if (LoaderStatus is not null && !string.IsNullOrEmpty(status)) LoaderStatus.Text = status;
    }

    private void LoaderTick_Advance()
    {
        if (LoadingOverlay.Visibility != Visibility.Visible) { StopLoader(); return; }
        double h = Math.Max(1, LoadingOverlay.ActualHeight);

        // 向阶段目标逼近（指数逼近 + 最小步长）：真实进度快时不会「跳」，慢时也看得出在爬。
        // 永远不超过 _loaderTarget，所以加载没结束就绝不会自己走到 100%。
        _loaderProgress += Math.Max(BootProgressMinStep, (_loaderTarget - _loaderProgress) * BootProgressApproach);
        if (_loaderProgress > _loaderTarget) _loaderProgress = _loaderTarget;
        if (_loaderProgress < 0) _loaderProgress = 0;

        int value = (int)Math.Round(_loaderProgress);
        if (LoaderFill is not null) LoaderFill.Height = _loaderProgress / 100.0 * h;
        if (LoaderPct is not null) LoaderPct.Text = value + "%";
        if (LoaderMeter is not null)
        {
            double mh = Math.Max(40, LoaderMeter.ActualHeight);
            double frac = Math.Clamp(_loaderProgress / 100.0, 0, 1);
            double fillTop = (1 - frac) * h; // 进度条上沿（自下而上）
            // 夹在上下留白之间：到 100% 时不会贴着顶边、0% 时不会掉出底边
            double top = Math.Clamp(fillTop - mh - 10, 24, Math.Max(24, h - mh - 24));
            LoaderMeter.Margin = new Thickness(BootGutter, top, 0, 0);
        }
    }

    /// <summary>导航成功：跳到 100%，定格后整体淡出（不扫屏），交给 Web 端启动屏接力。</summary>
    private void FinishLoading()
    {
        if (_finishing) return;
        StopLoader();
        if (LoadingOverlay.Visibility != Visibility.Visible) return;
        double h = Math.Max(1, LoadingOverlay.ActualHeight);
        _loaderProgress = 100;
        _loaderTarget = 100;
        if (LoaderFill is not null) LoaderFill.Height = h;
        if (LoaderPct is not null) LoaderPct.Text = "100%";
        if (LoaderStatus is not null) LoaderStatus.Text = "Ready";
        if (LoaderWipe is not null) { LoaderWipe.Opacity = 0; LoaderWipe.Width = BootRailWidth; }
        // 到 100% 时进度上沿就是顶边：把仪表组固定在顶部留白处，别让它贴住窗口边缘。
        if (LoaderMeter is not null) LoaderMeter.Margin = new Thickness(BootGutter, 24, 0, 0);
        _finishing = true;
        _finishStartUtc = DateTime.UtcNow;
        _finishTimer.Start();
    }

    // ---------------------------------------------------------------------
    //  DSH_DESKTOP_SPLASH_PREVIEW_MS：启动屏预览锁定
    // ---------------------------------------------------------------------

    /// <summary>预览期是否仍然生效（未设置 = 永远 false）。</summary>
    private bool IsSplashPreviewActive() =>
        _splashPreviewTimer is not null && _splashPreviewTimer.IsEnabled;

    /// <summary>
    /// 启动预览锁定：从遮罩亮起算起 BootPreviewMs 内，HideLoading 不生效
    /// （连接成功/失败/401 都藏不掉它），保证服务不可用时也能稳定看到启动屏。
    /// 定时器在 ctor 里、ShowLoading 之前就建好，不存在空引用。
    /// </summary>
    private void ArmSplashPreview()
    {
        _splashPreviewTimer ??= new DispatcherTimer();
        _splashPreviewTimer.Stop();
        _splashPreviewTimer.Interval = TimeSpan.FromMilliseconds(BootPreviewMs);
        _splashPreviewTimer.Tick -= SplashPreview_Tick;
        _splashPreviewTimer.Tick += SplashPreview_Tick;
        _hideRequestedDuringPreview = false;
        _splashPreviewTimer.Start();
    }

    /// <summary>
    /// 预览到时：解除锁定；如果这期间收到过「该收起了」的请求就补做一次真正的收起。
    /// 注意 FinishTick 的淡出循环在预览期内已经把 _finishing 收掉了，所以这里不能
    /// 只调 HideLoading()（那样会被 IsSplashPreviewActive 之外的状态挡住），
    /// 必须先把淡出状态复位再收。
    /// </summary>
    private void SplashPreview_Tick(object? sender, EventArgs e)
    {
        _splashPreviewTimer?.Stop();
        if (!_hideRequestedDuringPreview) return;
        _hideRequestedDuringPreview = false;
        _finishTimer.Stop();      // 预览期内可能还挂着淡出循环，先停掉
        _finishing = false;
        HideLoading();
    }

    /// <summary>
    /// 100% 之后的收尾。保持「爬到 100% → 定格（≥ 最短可见 + HOLD）→ 强调色横向 wipe」的顺序：
    ///  - 未开始 wipe：等定格时长；到点进入覆盖段（_bootWipePhase = 1）；
    ///  - 覆盖段：XAML 里 BootWipeMask 的 Width 由 0 动画到窗口宽（easeInOut，420ms），
    ///            整屏高的强调色幕布从左向右合上；同时启动屏整体淡出，
    ///            盖上那一刻幕布不透明、启动屏已透明，衔接处不会出现空白帧；
    ///  - 露出段：幕布完全盖住后 HideLoading() 收掉启动屏（下面已经是加载好的主界面），
    ///            再让整块幕布向右平移滑出（TranslateTransform.X 0 → 窗口宽，420ms）；
    ///  - 收尾：EndBootWipe() 复位（Collapsed / Width=0 / X=0 / 清动画），供下次启动复用。
    /// </summary>
    private void FinishTick()
    {
        const double HoldMs = 220;
        double sinceFinish = (DateTime.UtcNow - _finishStartUtc).TotalMilliseconds;
        double sinceShown = (DateTime.UtcNow - _overlayShownUtc).TotalMilliseconds;
        // 定格时长取两者较大：完成后的 220ms（+验收开关 DSH_DESKTOP_SPLASH_HOLD_MS），
        // 以及「遮罩总可见时长不少于 SplashMinVisibleMs」所需补足的部分。
        double hold = Math.Max(HoldMs + BootExtraHoldMs, SplashMinVisibleMs - Math.Max(0, sinceShown - sinceFinish));
        if (sinceFinish < hold) return; // 100% 定格一拍

        if (_bootWipePhase == 0)
        {
            StartBootWipeCover();
            return;
        }
        if (_bootWipePhase == 1 && sinceFinish >= hold + BootWipeCoverMs)
        {
            StartBootWipeReveal();
            return;
        }
        if (_bootWipePhase == 2 && sinceFinish >= hold + BootWipeCoverMs + BootWipeRevealMs)
        {
            EndBootWipe(resetOverlayOpacity: false);
            _finishTimer.Stop();
            _finishing = false;
        }
    }

    /// <summary>覆盖段：强调色幕布宽度 0 → 窗口宽（easeInOut），启动屏同时淡出。</summary>
    private void StartBootWipeCover()
    {
        if (BootWipeMask is null || BootWipeSlide is null) { EndBootWipe(resetOverlayOpacity: true); return; }
        _bootWipePhase = 1;

        double w = Math.Max(1, ActualWidth > 0 ? ActualWidth : SystemParameters.WorkArea.Width);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var dur = TimeSpan.FromMilliseconds(BootWipeCoverMs);

        BootWipeMask.Visibility = Visibility.Visible;
        BootWipeSlide.X = 0;
        BootWipeMask.Opacity = 1;
        BootWipeMask.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(0, w, dur) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });

        // 启动屏内容在这 420ms 内淡出（420ms 后正好被幕布完全盖住，视觉上无缝）
        double from = LoadingOverlay.Opacity;
        LoadingOverlay.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(from, 0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
    }

    /// <summary>露出段：幕布盖满后收掉启动屏，再把整块幕布向右平移滑出画面。</summary>
    private void StartBootWipeReveal()
    {
        _bootWipePhase = 2;
        HideLoading();   // 启动屏 Collapsed：此刻整屏被不透明幕布盖住，不会露出空白

        if (BootWipeMask is null || BootWipeSlide is null) return;
        double w = Math.Max(1, ActualWidth > 0 ? ActualWidth : SystemParameters.WorkArea.Width);
        BootWipeSlide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, w, TimeSpan.FromMilliseconds(BootWipeRevealMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd,
            });
    }

    /// <summary>
    /// wipe 进行中窗口被改变大小时，按新的 ActualWidth 重算两条动画（从当前值继续），
    /// 避免幕布比窗口窄、右边露出没被盖住的一帧。
    /// </summary>
    private bool SyncBootWipeGeometry()
    {
        if (_bootWipePhase == 0 || BootWipeMask is null || BootWipeSlide is null) return false;
        double w = Math.Max(1, ActualWidth > 0 ? ActualWidth : SystemParameters.WorkArea.Width);

        if (_bootWipePhase == 1)
        {
            // 覆盖段：宽度目标改成新宽度，起点取当前已经展开的宽度，动画不跳
            double current = BootWipeMask.ActualWidth > 0 ? BootWipeMask.ActualWidth : BootWipeMask.Width;
            if (current < 0 || double.IsNaN(current)) current = 0;
            BootWipeMask.BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(current, w, TimeSpan.FromMilliseconds(BootWipeCoverMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                    FillBehavior = FillBehavior.HoldEnd,
                });
        }
        else
        {
            // 露出段：位移目标改成新宽度（幕布整块被推出画面）
            BootWipeSlide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(BootWipeSlide.X, w, TimeSpan.FromMilliseconds(BootWipeRevealMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                    FillBehavior = FillBehavior.HoldEnd,
                });
        }
        return true;
    }

    /// <summary>
    /// wipe 收尾/复位：停掉两条动画并回到未展开状态（Collapsed、Width 0、X 0）。
    /// <paramref name="resetOverlayOpacity"/> = true 时同时把启动屏不透明度拉回 1
    /// （StartLoader / 预览超时这些「半路取消」的路径需要，正常结束时不需要）。
    /// </summary>
    private void EndBootWipe(bool resetOverlayOpacity)
    {
        _bootWipePhase = 0;
        if (BootWipeMask is not null)
        {
            BootWipeMask.BeginAnimation(FrameworkElement.WidthProperty, null);
            BootWipeMask.Width = 0;
            BootWipeMask.Opacity = 1;
            BootWipeMask.Visibility = Visibility.Collapsed;
        }
        if (BootWipeSlide is not null)
        {
            BootWipeSlide.BeginAnimation(TranslateTransform.XProperty, null);
            BootWipeSlide.X = 0;
        }
        if (resetOverlayOpacity)
        {
            LoadingOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            LoadingOverlay.Opacity = 1;
        }
    }

    private async Task EnsureServerAndLoadAsync()
    {
        try
        {
            SetBootStage(8, "Checking...");   // 阶段 8%：进入环境检查
            if (!_core.EnvironmentReady)
            {
                ShowLoading("环境不完整", "缺少关键文件，详见日志");
                return;
            }
            SetBootStage(20, "Preparing..."); // 阶段 20%：环境检查通过

            bool running = _core.IsRunning(out _);
            if (!running)
            {
                ShowLoading("正在启动 DeepSeek Harness...", "首次启动会自动修复依赖，请稍候");
                SetBootStage(35, "Starting service...");   // 阶段 35%：服务启动中
                await _core.StartAsync(noOpen: true);
                await _core.WaitForPortAsync(true, 12000);
                SetBootStage(60, "Service ready...");      // 阶段 60%：端口已就绪
            }
            else
            {
                // 服务已在运行：同样播启动屏，保证每次打开桌面版都有加载界面
                // （WebView 就绪并导航完成后由 FinishLoading 扫屏/淡出让位）
                ShowLoading("正在连接工作台...", "正在加载 DSH 界面");
                SetBootStage(45, "Connecting...");         // 阶段 45%：附着已有服务
            }

            if (!_core.IsRunning(out _))
            {
                ShowLoading("服务未就绪", "启动未成功，请点「日志」查看详情");
                return;
            }

            await InitWebViewAsync();
            SetBootStage(75, "Initializing UI...");        // 阶段 75%：WebView2 初始化返回
            if (_webViewReady) NavigateToApp();
        }
        catch (Exception ex)
        {
            ShowLoading("启动失败：" + ex.Message, "");
        }
    }

    private async Task InitWebViewAsync()
    {
        if (_webViewReady) return;

        // 检测系统 WebView2 运行时
        string? runtimeVersion = null;
        try { runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { runtimeVersion = null; }

        var userData = Path.Combine(_core.Root, "dsh-home", "webview2-data");

        if (runtimeVersion is null)
        {
            await HandleMissingRuntimeAsync();
            return;
        }

        try
        {
            // 强制软件渲染：实测本机/部分显卡驱动下 WebView2 的 GPU 合成会
            // 显示白屏/黑屏（页面已加载但内容不显示）。聊天类界面软件渲染
            // 足够流畅，且能保证任何环境都不黑屏。
            var opts = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-gpu --disable-gpu-compositing",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, userData, opts);
            await WebView.EnsureCoreWebView2Async(env);
            ApplyWebColorScheme();
            await ConfigureWebViewAsync();
            _webViewReady = true;
            SetBootStage(75, "Initializing UI...");   // 阶段 75%：WebView2 内核就绪
        }
        catch (Exception ex)
        {
            ShowLoading("WebView2 初始化失败：" + ex.Message, "");
        }
    }

    /// <summary>
    /// 明确设定内嵌页面的配色方案：默认 Auto（跟随 Windows），
    /// DSH_DESKTOP_THEME 覆盖时按覆盖值按死。
    /// 必须每次都显式写入 —— PreferredColorScheme 会存进 WebView2 用户数据目录，
    /// 只写不还原的话，一次强制浅色就会永久留在配置里。
    /// </summary>
    private void ApplyWebColorScheme()
    {
        if (WebView.CoreWebView2 is null) return;
        try
        {
            WebView.CoreWebView2.Profile.PreferredColorScheme = DsTheme.Forced() switch
            {
                DsThemeKind.Light => CoreWebView2PreferredColorScheme.Light,
                DsThemeKind.Dark => CoreWebView2PreferredColorScheme.Dark,
                _ => CoreWebView2PreferredColorScheme.Auto,
            };
        }
        catch { /* 旧版本运行时可能不支持，忽略 */ }
    }

    private async Task ConfigureWebViewAsync()
    {
        if (WebView.CoreWebView2 is null) return;
        WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

        // 页面回传主题（深浅 + 实时设计令牌）→ 外壳配色/图标跟随
        WebView.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            try
            {
                var json = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(json)) return;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "dsh-desktop-theme") return;

                bool dark = root.TryGetProperty("dark", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.True;
                var tokens = new Dictionary<string, Color>(StringComparer.Ordinal);
                if (root.TryGetProperty("tokens", out var tk) && tk.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var prop in tk.EnumerateObject())
                    {
                        if (DsTheme.TryParseCssColor(prop.Value.GetString(), out var color))
                            tokens[prop.Name] = color;
                    }
                }
                ApplyPageTheme(dark, tokens);
            }
            catch { /* 主题回传解析失败不影响主流程 */ }
        };

        // 主题桥：把页面真实的深浅色状态 + 设计令牌回传宿主。
        // DSH 网页端把深色写在 <body data-ds-dark-theme>（跟随系统或用户偏好），
        // 皮肤/令牌变了也一并回传，外壳因此永远与网页一致。
        //
        // 令牌用「探针元素 + 计算样式」解析而不是直接读自定义属性：自定义属性
        // 取回来可能是 val(--x) 链或尚未解析的写法，交给浏览器解析成 rgb() 最稳。
        const string themeScript = @"(() => {
  if (window.__dshDesktopThemeBridge) return;
  window.__dshDesktopThemeBridge = true;
  const KEYS = {
    bgBase: '--dsw-alias-bg-base',
    layer1: '--dsw-alias-bg-layer-1',
    layer2: '--dsw-alias-bg-layer-2',
    code: '--dsw-alias-markdown-code-block',
    label1: '--dsw-alias-label-primary',
    label2: '--dsw-alias-label-secondary',
    label3: '--dsw-alias-label-tertiary',
    border2: '--dsw-alias-border-l2'
  };
  const SENTINEL = 'rgba(1, 2, 3, 0.5)';
  const isDark = () => {
    const b = document.body;
    if (b && b.hasAttribute('data-ds-dark-theme')) return true;
    const cs = document.documentElement && document.documentElement.style;
    if (cs && cs.colorScheme === 'dark') return true;
    try { return typeof matchMedia === 'function' && matchMedia('(prefers-color-scheme: dark)').matches; } catch (e) { return false; }
  };
  let probe = null;
  const tokenProbe = () => {
    if (probe && probe.parentNode) return probe;
    try {
      probe = document.getElementById('__dsh_desktop_theme_probe');
      if (!probe) {
        probe = document.createElement('span');
        probe.id = '__dsh_desktop_theme_probe';
        probe.setAttribute('aria-hidden', 'true');
        probe.style.cssText = 'position:absolute;left:-9999px;top:0;width:0;height:0;pointer-events:none;';
        document.body.appendChild(probe);
      }
    } catch (e) { probe = null; }
    return probe;
  };
  const readTokens = () => {
    const out = {};
    const el = tokenProbe();
    if (!el) return out;
    let cs = null;
    try { cs = getComputedStyle(el); } catch (e) { return out; }
    for (const key in KEYS) {
      try {
        el.style.color = '';
        el.style.color = 'var(' + KEYS[key] + ', ' + SENTINEL + ')';
        const value = cs.color;
        if (value && value !== SENTINEL && value !== 'rgb(1, 2, 3)') out[key] = value;
      } catch (e) { }
    }
    el.style.color = '';
    return out;
  };
  let last = '';
  const post = () => {
    let payload;
    try { payload = JSON.stringify({ type: 'dsh-desktop-theme', dark: isDark(), tokens: readTokens() }); }
    catch (e) { return; }
    if (payload === last) return;
    last = payload;
    try { window.chrome && window.chrome.webview && window.chrome.webview.postMessage(payload); } catch (e) { }
  };
  const observe = () => {
    try {
      const mo = new MutationObserver(post);
      if (document.documentElement) mo.observe(document.documentElement, { attributes: true, attributeFilter: ['style', 'class', 'data-ds-dark-theme'] });
      if (document.body) mo.observe(document.body, { attributes: true, attributeFilter: ['style', 'class', 'data-ds-dark-theme'] });
    } catch (e) { }
  };
  const start = () => {
    post();
    observe();
    try { matchMedia('(prefers-color-scheme: dark)').addEventListener('change', post); } catch (e) { }
    // 兜底轮询：令牌（皮肤）可能在插件启用后才写入，快照去重保证不刷屏
    let waited = 0;
    const timer = setInterval(() => {
      waited += 1500;
      post();
      if (waited > 60000 && Object.keys(readTokens()).length > 0) clearInterval(timer);
    }, 1500);
  };
  if (document.body) start();
  else document.addEventListener('DOMContentLoaded', start, { once: true });
})();";

        // 在页面任何脚本运行前注入一层纯色遮罩，盖住 DSH 壳自带的原生加载画面，
        // 直到上游 Web 端启动屏插件挂载（z-index 更高）再撤掉——消除“原生加载界面”闪现。
        // 底色取原生启动屏同一个资源键（BootBg），浅色主题下也不会闪一下黑。
        // 注意：'[data-endfield-loader]' 是上游 web 插件自己挂的 DOM 属性，
        // 属于跨进程协议的一部分，不能跟着品牌改名，否则这里会一直等不到它。
        var bootCoverColor = DsTheme.ColorOf("BootBg");
        string earlyScript = @"(() => {
  const cover = document.createElement('div');
  cover.style.cssText = 'position:fixed;inset:0;z-index:2147482000;background:__BOOT_BG__;pointer-events:none;';
  (document.head || document.documentElement).appendChild(cover);
  let mo = null;
  const remove = () => { if (cover.parentNode) cover.parentNode.removeChild(cover); if (mo) { mo.disconnect(); mo = null; } };
  const hasPlate = () => typeof document.querySelector === 'function' && !!document.querySelector('[data-endfield-loader]');
  if (hasPlate()) { remove(); return; }
  mo = new MutationObserver(() => { if (hasPlate()) remove(); });
  mo.observe(document.documentElement, { childList: true, subtree: true });
  setTimeout(remove, 12000);
})();".Replace("__BOOT_BG__", $"#{bootCoverColor.R:X2}{bootCoverColor.G:X2}{bootCoverColor.B:X2}");

        // 注入完成后再导航，避免和 Navigate 抢时序
        try { await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(themeScript); }
        catch { /* 注入失败不影响主流程 */ }
        try { await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(earlyScript); }
        catch { /* 注入失败不影响主流程 */ }

        // 拦截所有导航：只允许 DSH 本地界面（127.0.0.1:3098）在内嵌 WebView 中渲染；
        // 外部链接（含 target=_blank 弹窗）一律交给系统默认浏览器，避免卡在内嵌页。
        WebView.CoreWebView2.NavigationStarting += (_, e) =>
        {
            try
            {
                if (IsAppNavigation(e.Uri))
                {
                    // 新文档还没回传主题：先按系统兜底，等页面脚本回传后再对齐
                    _pageThemeValid = false;
                    return;
                }
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
            catch { /* ignore */ }
        };

        WebView.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            try
            {
                e.Handled = true;
                if (!IsAppNavigation(e.Uri)) OpenExternal(e.Uri);
            }
            catch { /* ignore */ }
        };
    }

    /// <summary>是否属于 DSH 本地应用导航（同源 + about/blob/data）。</summary>
    private bool IsAppNavigation(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return true;
        if (uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;
        if (uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
        if (Uri.TryCreate(_core.Url, UriKind.Absolute, out var app)
            && Uri.TryCreate(uri, UriKind.Absolute, out var target)
            && string.Equals(target.Scheme, app.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.Host, app.Host, StringComparison.OrdinalIgnoreCase)
            && target.Port == app.Port) return true;
        return false;
    }

    /// <summary>用系统默认浏览器打开外部链接（仅 http/https；5 秒内同 URL 去重）。</summary>
    private void OpenExternal(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        if (!(uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
              || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return;
        var now = DateTime.UtcNow;
        if ((now - _openedExternalWindow).TotalSeconds > 5)
        {
            _openedExternally.Clear();
            _openedExternalWindow = now;
        }
        if (!_openedExternally.Add(uri.TrimEnd('/'))) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private async void NavigateToApp()
    {
        if (!_webViewReady) return;

        // The server begins listening before redirected stdout necessarily
        // reaches dsh-web.out.log, and a first boot (dependency repair, plugin
        // install, engine warm-up) can take far longer than the token takes to
        // appear after the port opens. Wait for the current process's launch
        // token instead of racing ahead to the unauthenticated origin: the
        // server answers a bare request with HTTP 401 ("dsh web authentication
        // required"), WebView2 reports that response as a *successful*
        // navigation, and the window is then left stuck on that text page.
        int request = ++_navigationRequest;
        string url = _core.Url;
        for (int attempt = 0; attempt < 120; attempt++)
        {
            if (request != _navigationRequest || !_webViewReady) return;
            if (attempt > 0) await Task.Delay(250);
            url = _core.BrowserUrl;
            if (!string.Equals(url, _core.Url, StringComparison.Ordinal)) break;
        }

        if (request != _navigationRequest || !_webViewReady) return;

        // Without a launch token only the persisted dsh-auth cookie can
        // authenticate the origin. Navigating bare without one is guaranteed to
        // land on the 401 page, so recover a usable session instead.
        if (string.Equals(url, _core.Url, StringComparison.Ordinal) && !await HasAuthCookieAsync())
        {
            await RecoverAuthenticationAsync("没有可用的启动 token，本地也没有认证 cookie");
            return;
        }

        SetBootStage(88, "Loading interface...");   // 阶段 88%：真正开始导航
        WebView.CoreWebView2?.Navigate(url);
    }

    /// <summary>
    /// Whether the WebView2 profile holds a usable <c>dsh-auth-*</c> cookie for
    /// this origin. The cookie name is derived from the authority only, so an
    /// existing one authenticates the bare URL (HTTP 200) even across restarts.
    /// </summary>
    private async Task<bool> HasAuthCookieAsync()
    {
        try
        {
            if (WebView.CoreWebView2 is null) return false;
            var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync(_core.Url);
            foreach (var cookie in cookies)
            {
                if (!cookie.Name.StartsWith("dsh-auth-", StringComparison.Ordinal)) continue;
                // The .NET projection maps a session cookie's epoch/-1 expiry to
                // a DateTime at or before 1970; anything else is a persistent
                // cookie that only counts while it has not expired.
                var expires = cookie.Expires;
                if (expires == default || expires.Year <= 1970) return true;
                if (expires.ToUniversalTime() > DateTime.UtcNow) return true;
            }
        }
        catch
        {
            // Cookie store unavailable: treat the cookie as absent and let the
            // recovery path decide.
        }
        return false;
    }

    /// <summary>
    /// Recover from an authentication rejection (HTTP 401/403). The launch token
    /// only exists in the running server's stdout log, so the escalation order
    /// is: a freshly re-read token URL, then the persisted cookie, then one
    /// automatic service restart that mints a new token, then an explicit hint.
    /// <paramref name="reason"/> is shown to the user.
    /// </summary>
    private async Task RecoverAuthenticationAsync(string reason)
    {
        if (!_webViewReady || WebView.CoreWebView2 is null) return;

        ShowLoading("正在重新认证…", reason);

        string tokenUrl = _core.BrowserUrl;
        bool hasToken = !string.Equals(tokenUrl, _core.Url, StringComparison.Ordinal);
        bool tokenChanged = hasToken && !string.Equals(tokenUrl, _unauthorizedUrl, StringComparison.Ordinal);
        if (hasToken) _unauthorizedUrl = tokenUrl;

        if (tokenChanged)
        {
            LogLine("[认证] 重新取到启动 token，重试导航。", DshLogKind.Warn);
            WebView.CoreWebView2.Navigate(tokenUrl);
            return;
        }

        if (await HasAuthCookieAsync())
        {
            LogLine("[认证] 检测到本地认证 cookie，改用裸地址重新加载。", DshLogKind.Warn);
            WebView.CoreWebView2.Navigate(_core.Url);
            return;
        }

        if (_authRecoveryAttempts < 1 && _core.IsRunning(out _))
        {
            _authRecoveryAttempts++;
            LogLine("[认证] 启动 token 与本地认证 cookie 都不可用，重启服务以重新认证。", DshLogKind.Warn);
            ShowLoading("正在重启服务以重新认证…", "会话记录不受影响，完成后会自动重新连接");
            await _core.RestartAsync(noOpen: true);
            _unauthorizedUrl = null;
            if (_core.IsRunning(out _) && _webViewReady) NavigateToApp();
            else ShowLoading("重新认证失败", "请点「重启」，或用「浏览器打开」");
            return;
        }

        ShowLoading("认证失败", "请点「重启」重建会话，或用「浏览器打开」");
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewReady) NavigateToApp();
    }

    private async void WebView_InitCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _webViewReady = true;
            ApplyWebColorScheme();
            await ConfigureWebViewAsync();
            if (_core.IsRunning(out _)) NavigateToApp();
        }
    }

    private async void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // 401/403 counts as a *successful* navigation for WebView2, so the auth
        // rejection has to be read from the status code. Without this check the
        // shell drops its loading overlay and leaves the server's bare 401 text
        // ("dsh web authentication required") stuck in the window.
        if (e.HttpStatusCode is 401 or 403)
        {
            _pageThemeValid = false;
            RefreshThemeFromOs();
            await RecoverAuthenticationAsync($"服务返回 HTTP {e.HttpStatusCode}");
            return;
        }

        if (e.IsSuccess)
        {
            _authRecoveryAttempts = 0;
            _unauthorizedUrl = null;
            SetBootStage(100, "Ready");   // 阶段 100%：导航成功（FinishLoading 内部也会置 100）
            FinishLoading();
        }
        else
        {
            // 页面没起来：退回系统主题兜底
            _pageThemeValid = false;
            RefreshThemeFromOs();
            ShowLoading("连接 DSH 界面失败", "请确认服务已启动，或点「浏览器打开」");
        }
    }

    private async Task HandleMissingRuntimeAsync()
    {
        var choice = DshHub.AskDialog.Show(this, "需要 WebView2 运行时",
            "检测到本机缺少 WebView2 运行时（嵌入式浏览器内核）。\n\n是否现在下载并安装？\n（约 2MB，Microsoft 官方引导器，装到当前用户，无需管理员）",
            ("下载并安装", DshHub.AskResult.Yes, true),
            ("取消", DshHub.AskResult.Cancel, false));

        if (choice != DshHub.AskResult.Yes)
        {
            ShowLoading("缺少 WebView2 运行时", "可点「浏览器打开」用外部浏览器使用 DSH");
            return;
        }

        ShowLoading("正在下载 WebView2 运行时...", "约 2MB，请稍候");
        try
        {
            var exe = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
            using (var http = new System.Net.Http.HttpClient())
            {
                var data = await http.GetByteArrayAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                await File.WriteAllBytesAsync(exe, data);
            }
            ShowLoading("正在安装 WebView2 运行时...", "约几十秒，无需管理员");
            using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = "--silent --install" });
            if (p is not null) await Task.Run(() => p.WaitForExit(120000));
            await InitWebViewAsync();
            if (_webViewReady) NavigateToApp();
        }
        catch (Exception ex)
        {
            ShowLoading("WebView2 安装失败：" + ex.Message, "可点「浏览器打开」使用外部浏览器");
        }
    }

    // =====================================================================
    //  Actions
    // =====================================================================

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_core.Busy) return;
        await _core.StartAsync(noOpen: true);
        if (_core.IsRunning(out _))
        {
            if (_webViewReady) NavigateToApp();
            else await EnsureServerAndLoadAsync();
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_core.Busy) return;
        await _core.StopAsync();
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_core.Busy) return;
        await _core.RestartAsync(noOpen: true);
        if (_core.IsRunning(out _) && _webViewReady) NavigateToApp();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_core.Busy) return;
        await _core.CheckUpdateAsync();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_core.Busy) return;
        bool running = _core.IsRunning(out _);
        string msg = running
            ? "更新前会先停止当前服务（自动备份 → 升级 → 自检），\n完成后需要手动重新启动服务。\n\n确定现在开始更新吗？"
            : "将自动备份当前版本并升级到最新版（含自检）。\n\n确定现在开始更新吗？";
        var choice = DshHub.AskDialog.Show(this, "立即更新", msg,
            ("开始更新", DshHub.AskResult.Yes, true),
            ("取消", DshHub.AskResult.Cancel, false));
        if (choice != DshHub.AskResult.Yes) return;

        ShowLoading("正在更新...", "更新可能耗时数分钟，完成后请重新启动服务");
        await _core.UpdateAsync();
        HideLoading();
        if (_core.IsRunning(out _) && _webViewReady) NavigateToApp();
    }

    private void Versions_Click(object sender, RoutedEventArgs e)
        => VersionsPanel.Visibility = VersionsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    // 「日志」按钮：展开/收起底部日志栏
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => ToggleLogPanel();

    private void ToggleLogPanel_Click(object sender, RoutedEventArgs e) => ToggleLogPanel();

    private void ToggleLogPanel()
    {
        bool show = LogPanel.Visibility != Visibility.Visible;
        LogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) LogBox.ScrollToEnd();
    }

    /// <summary>复制全部日志（主日志是 RichTextBox，这里走纯文本，方便贴到别处）。</summary>
    private void CopyAllLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = new System.Windows.Documents.TextRange(LogDoc.ContentStart, LogDoc.ContentEnd).Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            Clipboard.SetText(text);
            FooterText.Text = $"已复制 {_logLineCount} 行日志到剪贴板";
        }
        catch (Exception ex)
        {
            LogLine($"[错误] 复制日志失败：{ex.Message}", DshLogKind.Bad);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogDoc.Blocks.Clear();
        LoadingLogLines.Clear();
        _logLineCount = 0;
        LogCountText.Text = "";
    }

    // =====================================================================
    //  顶栏紧凑模式：窗口不够宽时收起按钮文字，只留图标（避免内容被裁掉）
    // =====================================================================

    private bool? _compact;

    private void ApplyCompactLayout(double width)
    {
        // 满标签时顶栏内容约 990 DIP（去掉品牌后窄了不少），留点余量：窄于 1060 就收起文字只留图标
        bool compact = width < 1060;
        if (_compact == compact) return;
        _compact = compact;

        var visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var label in new[] { TxtStart, TxtStop, TxtRestart, TxtCheckUpdate, BtnUpdateText, TxtVersions, TxtLogs, TxtReload, TxtOpenExternal })
        {
            if (label is not null) label.Visibility = visibility;
        }
        foreach (var separator in new[] { SepUpdate, SepTools })
        {
            if (separator is not null) separator.Visibility = visibility;
        }

        // 紧凑模式下把被收起的文字挪到 ToolTip，鼠标悬停仍能看懂
        BtnUpdate.ToolTip = compact ? BtnUpdateText.Text : "立即更新";

        // 每行按钮的左右内边距在紧凑模式收紧一点
        var padding = compact ? new Thickness(9, 0, 9, 0) : new Thickness(10, 0, 10, 0);
        foreach (var button in new[] { BtnStart, BtnStop, BtnRestart, BtnCheckUpdate, BtnUpdate, BtnVersions, BtnLogs, BtnReload, BtnOpenExternal })
        {
            if (button is not null) button.Padding = padding;
        }
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_core.BrowserUrl) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_core.Root}\"") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    // =====================================================================
    //  Close behavior
    // =====================================================================

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        bool running = _core.IsRunning(out _);
        if (!running)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closePromptOpen || _stoppingForExit) return;

        _closePromptOpen = true;
        try
        {
            var choice = DshHub.AskDialog.Show(this, "关闭桌面版",
                "DeepSeek Harness 服务正在运行。\n关闭窗口时是否同时停止服务？\n\n选择「仅退出」后服务会继续在后台运行。",
                ("停止并退出", DshHub.AskResult.Yes, true),
                ("仅退出", DshHub.AskResult.No, false),
                ("取消", DshHub.AskResult.Cancel, false));

            if (choice == DshHub.AskResult.Yes)
            {
                _stoppingForExit = true;
                _ = StopThenCloseAsync();
            }
            else if (choice == DshHub.AskResult.No)
            {
                _allowClose = true;
                _ = Dispatcher.BeginInvoke(Close);
            }
        }
        catch (Exception ex)
        {
            LogLine($"[错误] 关闭确认框异常：{ex.Message}", DshLogKind.Bad);
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    private async Task StopThenCloseAsync()
    {
        try
        {
            await _core.StopThenCloseAsync();
        }
        finally
        {
            _allowClose = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    // =====================================================================
    //  Window chrome
    // =====================================================================

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed && e.ClickCount == 1)
        {
            try { DragMove(); } catch { /* ignore */ }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // =====================================================================
    //  最大化时不要盖住任务栏
    //
    //  WindowStyle=None + WindowChrome 的窗口在最大化时，WPF 会把窗口拉到整块
    //  显示器尺寸（含任务栏那一条），底栏和右下角余额就被任务栏压住了。
    //  处理 WM_GETMINMAXINFO，把最大化位置/尺寸改成显示器【工作区】：
    //  所有最大化途径（按钮、双击标题栏、Win+↑、拖到屏幕顶端）都会经过这条消息。
    // =====================================================================

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        }
        catch { /* 挂不上钩子时保持系统默认行为 */ }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            try { ClampMaximizedSizeToWorkArea(hwnd, lParam); } catch { /* 用系统默认 */ }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ClampMaximizedSizeToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return;

        // ptMaxPosition 是相对「显示器左上角」的偏移，所以要把工作区原点减掉显示器原点
        mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
        mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
        mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
        mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
        // 注意：不动 ptMaxTrackSize —— 那是「手动拖拽上限」，压到工作区会让窗口
        // 没法跨显示器拉宽；这里只修最大化/贴边，用户自己拖到任务栏底下属于他的自由。

        // handled=true 会跳过 WPF 自己的处理，所以最小尺寸得自己补回来
        double scale = 1.0;
        try { scale = GetDpiForWindow(hwnd) / 96.0; } catch { }
        if (scale <= 0) scale = 1.0;
        mmi.ptMinTrackSize.X = Math.Max(mmi.ptMinTrackSize.X, (int)Math.Round(MinWidth * scale));
        mmi.ptMinTrackSize.Y = Math.Max(mmi.ptMinTrackSize.Y, (int)Math.Round(MinHeight * scale));

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    // 确保标题栏/控制条在屏幕内（多显示器/远程桌面 CenterScreen 可能顶出屏幕）
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // 确保窗口完整落在所在显示器工作区内：
    // 尺寸过大时收缩（避免 150% DPI 下窗口比屏幕还大、底部内容被顶出屏幕
    // 看不到，例如加载遮罩的状态/日志区），位置越界时拉回。
    private void ClampWindowIntoWorkArea()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return;

            // 工作区转成 DIP（Left/Top/Width/Height 是 DIP）
            double scale = 1.0;
            try { scale = GetDpiForWindow(hwnd) / 96.0; } catch { }
            double waLeft = mi.rcWork.Left / scale;
            double waTop = mi.rcWork.Top / scale;
            double waW = (mi.rcWork.Right - mi.rcWork.Left) / scale;
            double waH = (mi.rcWork.Bottom - mi.rcWork.Top) / scale;

            // 尺寸过大 → 收缩到工作区（保留最小尺寸）
            double w = Width, h = Height;
            double maxW = waW - 24, maxH = waH - 24;
            if (maxW >= MinWidth && w > maxW) w = maxW;
            if (maxH >= MinHeight && h > maxH) h = maxH;
            if (w != Width) Width = w;
            if (h != Height) Height = h;

            // 位置钳位
            double x = Left, y = Top;
            double minX = waLeft, minY = waTop;
            double maxX = waLeft + waW - Math.Min(80, w);
            double maxY = waTop + waH - Math.Min(80, h);
            if (maxX < minX || maxY < minY) return;
            double nx = Math.Clamp(x, minX, maxX);
            double ny = Math.Clamp(y, minY, maxY);
            if (nx != x) Left = nx;
            if (ny != y) Top = ny;
        }
        catch { /* ignore */ }
    }
}
