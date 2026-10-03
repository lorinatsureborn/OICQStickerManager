using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace OICQStickerManager.Services;

/// <summary>
/// 玻璃窗口基类：分层窗口（AllowsTransparency）+ XAML 内圆角 Border + OS 级磨砂。
/// 真模糊由原生拼片（BlurTile）垫在本窗口正下方，按系统版本分两条路线：
/// Win11（build≥22000）：**单片全幅 tile + DWMWA_WINDOW_CORNER_PREFERENCE=ROUND**——
/// 系统圆角连 accent 模糊一起圆剪，四角透镜消失，拼片 3→1（2026-10-03 PoC 实测，
/// probes/GlassPoC + 交接文档附录）；内容圆角对齐系统半径（8 DIP，随 DPI 缩放）。
/// Win10：CORNER_PREFERENCE 静默失败，模糊剪影恒为窗口矩形，维持三拼片"十字"：
/// 中带横贯全宽，上下边带与圆弧相切——四条直边的模糊直达窗口边缘，模糊不越出轮廓。
/// 代价：四个 r×r 角方块无模糊，只隔着材质层透出清晰背幕——角部暗影/边缘遮罩/
/// 阶梯拼片都试过，均被否决（暗影白底太显眼、遮罩有拼接感、多拼片拖动性能爆炸）。
/// 本窗口自身不能挂 accent/玻璃帧（否则漏方角），只做圆角 alpha + 半透明材质。
/// 圆角由像素 alpha 呈现（WPF 抗锯齿）。缩放用 WM_NCHITTEST 手工实现；无边框窗口仍需
/// WM_GETMINMAXINFO 修复最大化盖住任务栏的问题。
/// </summary>
public class GlassWindow : Window
{
    private IntPtr _hwnd;
    private bool _themeHooked;
    private BlurTile?[] _tiles = Array.Empty<BlurTile?>();
    private bool _tilesReady;

    /// <summary>窗口级圆角（DIP）。仅 Win10 三拼片路线使用；Win11 用系统半径。</summary>
    public double WindowCornerRadiusDip { get; set; } = 16;

    /// <summary>Win11 22000+：单片圆角 tile 路线（DWM 圆剪连模糊一起走）；否则三拼片。</summary>
    internal static readonly bool RoundedTiles = Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Win11 系统圆角半径（DWM 固定 8px@96DPI，DIP 表述随缩放等比）。</summary>
    private const double SystemRoundRadiusDip = 8;

    /// <summary>内容圆角：Win11 对齐系统半径，Win10 维持设计半径。</summary>
    private double ContentCornerRadiusDip => RoundedTiles ? SystemRoundRadiusDip : WindowCornerRadiusDip;

    /// <summary>是否提供缩放热区（快捷面板 NoResize 时返回 false）。</summary>
    protected virtual bool Resizable => true;

    /// <summary>拼片是否进置顶层（快捷面板 Topmost=true，拼片必须同带才能垫在其下）。</summary>
    protected virtual bool BlurTilesTopmost => false;

    public GlassWindow()
    {
        Background = Brushes.Transparent;
        SizeChanged += (_, e) => { ApplyCornerClip(e.NewSize); RepositionTiles(); };
        StateChanged += (_, _) =>
        {
            ApplyCornerClip(new Size(ActualWidth, ActualHeight));
            RepositionTiles();
            SyncTileVisibility();
        };
        IsVisibleChanged += (_, _) => SyncTileVisibility();
        Activated += (_, _) => RepinTiles();
        LocationChanged += (_, _) => RepositionTiles();
        ContentRendered += EnsureTilesOnce;
        ThemeManager.ThemeChanged += OnThemeChangedRespray;
        _themeHooked = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndProc);
        TryRoundSystemCorners();
        ApplyCornerClip(new Size(ActualWidth, ActualHeight));
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_themeHooked)
        {
            ThemeManager.ThemeChanged -= OnThemeChangedRespray;
            _themeHooked = false;
        }
        foreach (var tile in _tiles) tile?.Dispose();
        _tiles = Array.Empty<BlurTile?>();
        _tilesReady = false;
        base.OnClosed(e);
    }

    private void OnThemeChangedRespray(object? sender, EventArgs e)
    {
        foreach (var tile in _tiles) tile?.ApplyAccent();
    }

    /// <summary>内容裁剪成圆角矩形：滚动内容/贴边元素不会捅出圆角。最大化时转直角。</summary>
    private void ApplyCornerClip(Size size)
    {
        if (Content is not UIElement content) return;
        var radius = WindowState == WindowState.Maximized ? 0 : ContentCornerRadiusDip;
        content.Clip = new RectangleGeometry(new Rect(0, 0, size.Width, size.Height), radius, radius);
    }

    // ———— 磨砂拼图 ————

    private void EnsureTilesOnce(object? sender, EventArgs e)
    {
        ContentRendered -= EnsureTilesOnce;
        EnsureTiles();
    }

    private void EnsureTiles()
    {
        if (_tilesReady) return;
        // 拼片以隐藏窗口创建在屏幕外；显示走 SyncTileVisibility 的原子路径
        // （单次 SetWindowPos 同时完成 显示+钉位+摆位，两步走会有层顶黑闪竞态）
        _tilesReady = true;
        // 拼片数直接决定拖动时的 SetWindowPos/DWM 重模糊开销：Win11 单片，Win10 三片，禁止再扩
        _tiles = new BlurTile?[RoundedTiles ? 1 : 3];
        for (int i = 0; i < _tiles.Length; i++)
        {
            // 先建在屏幕外，RepositionTiles 会立刻摆到位
            _tiles[i] = BlurTile.Create(-20000, -20000, 1, 1, BlurTilesTopmost, round: RoundedTiles);
        }
        RepositionTiles();
        SyncTileVisibility();
    }

    /// <summary>
    /// 摆放十字拼片（物理像素）：中带横贯全宽，上下边带与圆弧相切——直边模糊铺满到边，
    /// 四个 r×r 角方块不铺（透过材质层看到清晰背幕，换取零外溢和拖动性能）。
    /// 最大化时 R=0，中带满铺、边带高度归零。只摆位、不碰显示（ShowWindow 会把窗口放回层顶）。
    /// </summary>
    private void RepositionTiles() => LayoutTiles(showAndPin: false);

    /// <summary>可见路径：摆位+z 钉位+显示一次完成（原子 SetWindowPos）。</summary>
    private void ShowTilesPinned() => LayoutTiles(showAndPin: true);

    /// <summary>三块拼片的统一布局：showAndPin=true 时用单次 SetWindowPos 原子完成 显示+钉位+摆位。</summary>
    private void LayoutTiles(bool showAndPin)
    {
        if (!ComputeTileRects(out var left, out var top, out var w, out var h, out var r)) return;
        if (RoundedTiles)
        {
            // 单片全幅：模糊剪影由 DWM 按系统圆角圆剪，无需拼十字（r 不参与几何）
            if (showAndPin) _tiles[0]?.ShowPinnedBelow(_hwnd, left, top, w, h);
            else _tiles[0]?.Layout(left, top, w, h);
            return;
        }
        int sw = Math.Max(0, w - 2 * r);
        // 0:横贯全宽的中带 1:上边带 2:下边带。
        // 下边带比上边带高 1px（与中带重叠一行，均为磨砂无接缝）：三块拼片尺寸彼此唯一，
        // 杜绝 DWM 对同尺寸窗口（原上/下带同为 w-2r × r）的模糊纹理混用嫌疑
        int bh = Math.Min(r + 1, Math.Max(0, h - r));
        if (showAndPin)
        {
            _tiles[0]?.ShowPinnedBelow(_hwnd, left, top + r, w, Math.Max(0, h - 2 * r));
            _tiles[1]?.ShowPinnedBelow(_hwnd, left + r, top, sw, r);
            _tiles[2]?.ShowPinnedBelow(_hwnd, left + r, top + h - bh, sw, bh);
        }
        else
        {
            _tiles[0]?.Layout(left, top + r, w, Math.Max(0, h - 2 * r));
            _tiles[1]?.Layout(left + r, top, sw, r);
            _tiles[2]?.Layout(left + r, top + h - bh, sw, bh);
        }
    }

    /// <summary>拼片矩形参数（物理像素），RepositionTiles 与 ShowTilesPinned 共用同一套几何。</summary>
    private bool ComputeTileRects(out int left, out int top, out int w, out int h, out int r)
    {
        left = top = w = h = r = 0;
        if (!_tilesReady || _tiles.Length < 1) return false;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return false;
        double scale = source.CompositionTarget.TransformToDevice.M11;
        if (double.IsNaN(Left) || double.IsNaN(Top)) return false; // CenterScreen 落位前跳过

        left = (int)Math.Round(Left * scale);
        top = (int)Math.Round(Top * scale);
        w = (int)Math.Round((ActualWidth > 0 ? ActualWidth : Width) * scale);
        h = (int)Math.Round((ActualHeight > 0 ? ActualHeight : Height) * scale);
        // 圆角半径（物理 px）：边带与圆弧相切
        r = WindowState == WindowState.Maximized ? 0 : (int)Math.Round(WindowCornerRadiusDip * scale);
        return true;
    }

    private void SyncTileVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            ShowTilesPinned(); // 原子显示+钉位（含摆位），已钉好的片重复调用无害
            RepinTiles();      // 兜底：把已显示的片再钉一次，防其他窗口滑进缝隙
        }
        else
        {
            foreach (var tile in _tiles) tile?.Show(false);
        }
    }

    private void RepinTiles()
    {
        if (_hwnd == IntPtr.Zero) return;
        foreach (var tile in _tiles) tile?.RepinBelow(_hwnd);
    }

    /// <summary>Win11 的系统级圆角（连 accent 模糊一起圆剪）；Win10 返回错误码，静默忽略。</summary>
    private void TryRoundSystemCorners()
    {
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* 非 Win11 / DWM 不可用 */ }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (Resizable && WindowState == WindowState.Normal)
                {
                    var ht = HitTestResize(lParam);
                    if (ht != HTCLIENT)
                    {
                        handled = true;
                        return new IntPtr(ht);
                    }
                }
                break;
            case WM_GETMINMAXINFO:
                // 无边框窗口最大化会盖住任务栏，把最大化尺寸钳制到工作区
                FixMaximizeBounds(lParam);
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private int HitTestResize(IntPtr lParam)
    {
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        if (!GetWindowRect(_hwnd, out var rect)) return HTCLIENT;

        const int band = 8; // 物理像素热区宽度
        bool left = x - rect.Left < band;
        bool right = rect.Right - x < band;
        bool top = y - rect.Top < band;
        bool bottom = rect.Bottom - y < band;

        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return HTCLIENT;
    }

    private void FixMaximizeBounds(IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == System.IntPtr.Zero) return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        mmi.ptMaxPosition.x = Math.Abs(info.rcWork.Left - info.rcMonitor.Left);
        mmi.ptMaxPosition.y = Math.Abs(info.rcWork.Top - info.rcMonitor.Top);
        mmi.ptMaxSize.x = Math.Abs(info.rcWork.Right - info.rcWork.Left);
        mmi.ptMaxSize.y = Math.Abs(info.rcWork.Bottom - info.rcWork.Top);
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    // --- Win32 ---

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private const int HTCLIENT = 1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}