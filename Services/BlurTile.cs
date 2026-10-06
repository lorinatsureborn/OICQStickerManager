using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace OICQStickerManager.Services;

/// <summary>
/// 一块原生磨砂拼片：裸 Win32 窗口（无 WPF 表面），黑刷 + DwmExtendFrameIntoClientArea(-1)
/// + accent acrylic。DWM 的 accent 模糊剪影恒为窗口矩形且无视区域（已隔离实验穷尽证实），
/// 所以用多块矩形拼片拼出圆角内切形状——接缝经条纹背景实测不可见（接缝行差 6 &lt; 正常纹理差 12）。
/// 裸窗口之所以能出模糊而 WPF 非分层窗口不能：玻璃帧让黑刷表面成为 DWM 玻璃，
/// accent 在玻璃区合成；WPF 非分层表面不透 alpha，会把 accent 整个盖黑。
/// 拼片不进任务栏、不激活、鼠标穿透；z 序由 GlassWindow 用 RepinBelow 钉在内容窗口正下方。
/// </summary>
internal sealed class BlurTile : IDisposable
{
    private IntPtr _hwnd;
    internal bool AccentAvailable => _accentOn;

    private BlurTile() { }

    public static BlurTile? Create(int x, int y, int w, int h, bool topmost, bool round = false)
    {
        if (DwmIsCompositionEnabled(out bool enabled) != 0 || !enabled) return null;
        EnsureClass();
        var hwnd = CreateWindowExW(
            (uint)(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | (topmost ? WS_EX_TOPMOST : 0)),
            ClassName, ClassName, WS_POPUP,
            x, y, w, h, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return null;

        var tile = new BlurTile { _hwnd = hwnd };
        try
        {
            var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0) { tile.Dispose(); return null; }
            tile._rounded = round;
            if (round) tile.ApplyRounding();
            tile.ApplyAccent();
            if (!tile._accentOn) { tile.Dispose(); return null; }
            return tile;
        }
        catch { tile.Dispose(); throw; }
    }

    /// <summary>移动/缩放拼片（物理像素）。零尺寸 = 隐藏（不参与拼接）。
    /// 注意：这里绝不能顺手调 ShowWindow——对已可见窗口 SW_SHOWNA 也会把它放回层顶，
    /// 拖动时每帧 LocationChanged 都会经过这里，曾致"一拖动拼片就盖住界面"。</summary>
    public void Layout(int x, int y, int w, int h)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (w <= 0 || h <= 0)
        {
            Show(false);
            return;
        }
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, SWP_NOACTIVATE | SWP_NOZORDER);
    }

    private bool _shown;
    private bool _accentOn;
    private bool _rounded;
    private bool _firstShowPending = true;
    private DispatcherTimer? _firstShowTimer;

    public void Show(bool visible)
    {
        if (_shown == visible) return; // 门闩：可见性没变化绝不调 ShowWindow（会重置 z 序到层顶）
        _shown = visible;
        if (visible)
        {
            // accent 必须在显示之后再涂：ShowWindow 会重置 accent 状态，先涂会被冲掉，
            // 拼片就以黑刷+玻璃帧的裸脸出现（2026-09-30 用户实测定案的顶部黑带真根因）。
            // 显示与重涂同一次调度内完成，赶在 DWM 合成下一帧之前，不留黑帧
            ShowWindow(_hwnd, SW_SHOWNA);
            ApplyAccent();
            ArmFirstShowReassert();
        }
        else
        {
            ShowWindow(_hwnd, SW_HIDE);
            // 已不可见再拆 accent：下次显示的 ApplyAccent 构成状态变化，强制 DWM 重算模糊
            ClearAccent();
        }
    }

    /// <summary>把拼片钉在 contentHwnd 正下方（z 序插队防护：其他窗口可能滑进两者之间）。</summary>
    public void RepinBelow(IntPtr contentHwnd)
    {
        if (_hwnd == IntPtr.Zero || contentHwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, contentHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 显示并原子完成 z 钉位+摆位（单次 SetWindowPos 带 SWP_SHOWWINDOW）。
    /// 旧路径「先 ShowWindow（落到层顶=盖住面板）→ 再 Repin 到面板下方」两步之间
    /// DWM 若恰好合成一帧，上边带拼片就把黑玻璃表面叠在面板顶部 → 横带偶现黑闪。
    /// 隐藏窗口不占 z 序没法「先钉后显」，单调用原子路径是唯一无竞态解。
    /// </summary>
    public void ShowPinnedBelow(IntPtr contentHwnd, int x, int y, int w, int h)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (w <= 0 || h <= 0) { Show(false); return; }
        if (contentHwnd == IntPtr.Zero) { Layout(x, y, w, h); Show(true); return; } // 宿主未就绪退回两步路径
        if (SetWindowPos(_hwnd, contentHwnd, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW))
        {
            _shown = true;
            ApplyAccent(); // 同上：SWP_SHOWWINDOW 会重置 accent，必须显示后立即重涂
            ArmFirstShowReassert();
        }
    }

    /// <summary>
    /// 首次显示后补一轮 Clear→Apply（等价于事后跑一遍「最小化/恢复」周期）。
    /// 症状：第一次唤出窗口常「有透明无模糊」，最小化再恢复即愈，此后不再犯——
    /// 拼片是全新窗口时，显示帧同拍涂的 accent 对 DWM 不构成状态变化，且窗口尚无
    /// 合成表面，模糊管线不建图；hide(Clear)→show(Apply) 的状态翻转才强制重算。
    /// 100ms 后 DWM 必已完成数帧合成，届时翻一轮状态必出模糊；若首轮 accent 已生效，
    /// Clear 与 Apply 同调度背靠背，DWM 合成不到中间态，无可见闪变。
    /// </summary>
    private void ArmFirstShowReassert()
    {
        if (!_firstShowPending) return;
        _firstShowPending = false;
        _firstShowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _firstShowTimer.Tick += (_, _) =>
        {
            _firstShowTimer?.Stop();
            _firstShowTimer = null;
            if (!_shown) return; // 已被藏起：隐藏路径本就 Clear 过，下轮显示自然重涂
            ClearAccent();
            ApplyAccent();
        };
        _firstShowTimer.Start();
    }

    /// <summary>
    /// 重涂模糊底。常驻/拖动统一用 BLURBEHIND（2026-09-29 用户实测定案）：ACRYLIC 即使
    /// 色调 alpha=0 也带内置雾化增亮，会把模糊区衬得比角部无模糊透镜区更白，形成可见色差；
    /// BLURBEHIND 纯模糊无雾化，透镜区边界均匀——即用户确认"均匀且喜欢"的原拖动态外观。
    /// 如需回退 ACRYLIC：AccentState=4 + AccentFlags=2（flags=2 缺失时部分 Win10 版本不渲染）。
    /// </summary>
    public void ApplyAccent()
    {
        if (_hwnd == IntPtr.Zero) return;
        var policy = new ACCENT_POLICY
        {
            AccentState = ACCENT_ENABLE_BLURBEHIND,
            AccentFlags = 0,
            GradientColor = ThemeManager.Current.GlassTintAbgr, // BLURBEHIND 不读色调，仅保留接口
            AnimationId = 0,
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>());
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = Marshal.SizeOf<ACCENT_POLICY>(),
            };
            _accentOn = SetWindowCompositionAttribute(_hwnd, ref data);
            if (_rounded) ApplyRounding(); // 圆角偏好理论上随窗口存续，重涂一次防各处 SW 重置
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Win11：DWM 系统圆角连 accent 模糊一起圆剪（PoC 实测，见交接文档附录）；
    /// Win10 上该属性静默失败，tile 保持矩形——调用方按版本走三拼片。</summary>
    private void ApplyRounding()
    {
        if (_hwnd == IntPtr.Zero) return;
        var pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    /// <summary>拆掉 accent（隐藏时调用）：下次显示时 BLURBEHIND 构成状态变化，DWM 必须重算模糊，
    /// 杜绝「揭示第一帧=上次可见时的旧背景快照」。</summary>
    private void ClearAccent()
    {
        if (_hwnd == IntPtr.Zero || !_accentOn) return;
        var policy = new ACCENT_POLICY { AccentState = ACCENT_DISABLED };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>());
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = Marshal.SizeOf<ACCENT_POLICY>(),
            };
            SetWindowCompositionAttribute(_hwnd, ref data);
            _accentOn = false;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void Dispose()
    {
        _firstShowTimer?.Stop();
        _firstShowTimer = null;
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private static void EnsureClass()
    {
        if (_classRegistered) return;
        _classRegistered = true;
        // 静态字段持引用，防委托被 GC 回收后原生回调悬挂
        WndProc proc = DefProc;
        _wndProc = proc;
        var wc = new WNDCLASS
        {
            lpfnWndProc = proc,
            lpszClassName = ClassName,
            hInstance = _hInstance,
            hbrBackground = CreateSolidBrush(0x00000000), // 黑刷 = DWM 玻璃，accent 的合成底子
        };
        RegisterClassW(ref wc);
    }

    private const string ClassName = "AsukaBlurTile";
    private static bool _classRegistered;
    private static WndProc? _wndProc;
    // 单文件发布下 GetHINSTANCE(Module) 返回 -1（IL3002），改用主模块句柄：注册窗口类/建窗只要求非零
    private static IntPtr _hInstance => GetModuleHandleW(null);

    private delegate IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr DefProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProcW(hwnd, msg, wParam, lParam);

    // --- Win32 ---
    private const uint WS_POPUP = 0x80000000u;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4, SWP_SHOWWINDOW = 0x40;
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;
    // 曾用 4 = ACRYLIC（自带雾化导致角部色差，已弃用；回退配方见 ApplyAccent 注释）

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASS wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
