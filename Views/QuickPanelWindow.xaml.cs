using OICQStickerManager.Services;
using OICQStickerManager.ViewModels;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OICQStickerManager.Views
{
    /// <summary>
    /// 快捷表情面板：无边框雾玻璃、置顶、非激活窗口（WS_EX_NOACTIVATE）。
    /// 呼出时焦点始终留在聊天应用里，点击表情直接粘贴给前台窗口，杜绝“切错窗口”。
    /// 两种来源：热键呼出（光标处）/ QQ 表情面板共存（贴靠在 QQ 面板旁，发送时把焦点还给 QQ 输入框）。
    /// 进出场：缩放弹出（BackEase 微弹）/ 淡出。
    /// </summary>
    public partial class QuickPanelWindow : GlassWindow
    {
        private const int CloseGraceMs = 300; // 鼠标离开面板后延迟关闭，避免误擦过

        private readonly DispatcherTimer _closeTimer;
        private readonly MainViewModel _viewModel;
        private bool _coexistMode; // 是否由 QQ 表情面板共存触发打开
        private bool _pinned;      // 钉住模式（设置按钮唤出）：不因鼠标离开自动关闭，靠 ✕/热键/再点按钮收起
        private bool _suppressShowAnimation; // 预热时在屏幕外显示，不播动画
        private DispatcherTimer? _hideTimer; // HideSoft 的淡出收尾定时器：淡出途中重新打开时必须取消，否则窗口会在收尾时被 Hide

        public bool CoexistMode => _coexistMode;

        /// <summary>面板是 Topmost 窗口，磨砂拼片必须同在置顶层才能垫在面板之下。</summary>
        protected override bool BlurTilesTopmost => true;


        public QuickPanelWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;

            _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CloseGraceMs) };
            _closeTimer.Tick += CloseTimer_Tick;

            // 悬浮意图定时器：到点把挂起的标签值交给 VM（胶囊已被重建时值可能失效，做一次存在性校验）
            _tabHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TabHoverSwitchMs) };
            _tabHoverTimer.Tick += (_, _) =>
            {
                _tabHoverTimer.Stop();
                if (_pendingTabValue is string value && _viewModel.PanelTabs.Any(t => t.Value == value))
                    _viewModel.PanelSelectedTab = value;
            };

            // 发送发起瞬间立即收起（对齐 QQ 原生面板"点完即关"；粘贴/剪贴板恢复的等待不拖面板）。
            // 共存模式附加同步关掉 QQ 原生表情面板（UIA Invoke 表情按钮，尽力而为）
            viewModel.QuickPanelSendInitiated += (s, e) =>
            {
                HideSoft();
                if (_coexistMode) QqPanelWatcher.TryCloseQqPanel();
            };
        }

        // --- 非激活窗口 ---

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE);
            ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 核心：鼠标点击永不把面板带到前台，键盘焦点始终留在聊天窗口
            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }
            return IntPtr.Zero;
        }

        // --- 进出场动效 ---

        private void PlayShowAnimation()
        {
            if (_suppressShowAnimation)
            {
                _suppressShowAnimation = false;
                return;
            }

            PanelRoot.BeginAnimation(OpacityProperty, null);
            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            PanelScale.ScaleX = PanelScale.ScaleY = 0.92;
            PanelRoot.Opacity = 0;

            var pop = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
            };
            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            PanelRoot.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }

        /// <summary>带淡出的收起：动画结束后再真正隐藏窗口。</summary>
        public void HideSoft()
        {
            QqPanelWatcher.Log($"HideSoft enter: visible={IsVisible}");
            if (!IsVisible)
            {
                _closeTimer.Stop();
                return;
            }

            PanelRoot.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            _closeTimer.Stop();
            _hideTimer?.Stop();
            var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(155) };
            _hideTimer = hideTimer;
            hideTimer.Tick += (s, e) =>
            {
                hideTimer.Stop();
                if (ReferenceEquals(_hideTimer, hideTimer)) _hideTimer = null;
                var willHide = !_suppressShowAnimation && PanelRoot.Opacity < 1;
                QqPanelWatcher.Log($"hide tick: willHide={willHide} opacity={PanelRoot.Opacity:0.00}");
                if (willHide) Hide();
                else if (_suppressShowAnimation) { /* 期间又被打开，交给 Show 流程 */ }
                PanelRoot.BeginAnimation(OpacityProperty, null);
                PanelRoot.Opacity = 1;
                PanelScale.ScaleX = PanelScale.ScaleY = 1;
            };
            hideTimer.Start();
        }

        // --- 呼出与定位 ---

        private bool _warmed;

        /// <summary>
        /// 启动时预热：在屏幕外完成构建与首帧渲染（XAML/HWND/容器物化/图片解码/特效着色器
        /// 都是一次性成本），之后每次打开只是移动+显示，与 QQ 切换隐藏层同量级。
        /// </summary>
        public void WarmUp()
        {
            if (_warmed) return;
            _warmed = true;
            Left = -30000;
            Top = -30000;
            _suppressShowAnimation = true;
            Show();
            ContentRendered += WarmUp_ContentRendered;
        }

        private void WarmUp_ContentRendered(object? sender, EventArgs e)
        {
            ContentRendered -= WarmUp_ContentRendered;
            PanelRoot.BeginAnimation(OpacityProperty, null);
            PanelRoot.Opacity = 1;
            PanelScale.ScaleX = PanelScale.ScaleY = 1;
            Hide();
            QqPanelWatcher.Log("quick panel pre-warmed (built + first frame rendered)");
        }

        /// <summary>
        /// 呼出前按最新热度重排（QuickPanelView 不做实时排序，打开期间顺序冻结、发送不跳格）；
        /// 数据源顺带重建（图库 ∪ QQ 未入库镜像，见 RebuildPanelItems）；视角复位为「全部」，
        /// 不跨会话残留；已可见时（共存链路重复触发）不动，避免面板开着时格子重排。
        /// </summary>
        private void ResortForOpen()
        {
            if (IsVisible) return;
            _viewModel.ResetPanelTab();
            _viewModel.RebuildPanelItems();
            _viewModel.QuickPanelView.Refresh();
        }

        // --- 选项卡悬浮切换 ---

        // 悬浮意图延迟：扫过一排胶囊时途中胶囊不触发切换，停留才算数
        private const int TabHoverSwitchMs = 150;

        private readonly DispatcherTimer _tabHoverTimer;
        private string? _pendingTabValue;

        private void PanelTab_MouseEnter(object sender, MouseEventArgs e)
        {
            _pendingTabValue = ((PanelTabItem)((FrameworkElement)sender).DataContext).Value;
            _tabHoverTimer.Stop();
            _tabHoverTimer.Start();
        }

        private void PanelTab_MouseLeave(object sender, MouseEventArgs e)
        {
            // 离开的正是挂起中的胶囊才撤销；从胶囊 A 直接滑进胶囊 B 时保留 B 的挂起
            if (_pendingTabValue == ((PanelTabItem)((FrameworkElement)sender).DataContext).Value)
            {
                _pendingTabValue = null;
                _tabHoverTimer.Stop();
            }
        }

        private void PanelTab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _pendingTabValue = null;
            _tabHoverTimer.Stop();
            _viewModel.PanelSelectedTab = ((PanelTabItem)((FrameworkElement)sender).DataContext).Value;
        }

        // 胶囊行横向溢出时滚动条是隐藏的，用纵向滚轮横滚（一格 40px）；没溢出就不吃事件
        private void PanelTabScroll_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scroll = (ScrollViewer)sender;
            if (scroll.ScrollableWidth <= 0) return;
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        }

        public void OpenNearCursor() => OpenNearCursorCore(pinned: false);

        /// <summary>
        /// 钉住模式呼出（设置 → 打开快捷表情面板）：光标不在面板上，鼠标一动就会触发
        /// "离开 300ms 自动关闭"，面板活不过一秒。此来源不走鼠标离开关闭，
        /// 收起靠面板 ✕ / 热键 toggle / 再点设置按钮。
        /// </summary>
        public void OpenPinnedNearCursor() => OpenNearCursorCore(pinned: true);

        private void OpenNearCursorCore(bool pinned)
        {
            _coexistMode = false;
            _pinned = pinned;
            _viewModel.SetQuickPanelCoexistTarget(IntPtr.Zero);

            GetCursorPos(out var pt);
            IntPtr monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            PositionOnMonitor(monitor, pt.X, pt.Y);
            ResortForOpen();
            Show();
            PlayShowAnimation();
        }

        /// <summary>
        /// 共存模式：贴靠在 QQ 原生表情面板旁打开（优先左侧，放不下贴右侧），
        /// 并记录 QQ 宿主窗口句柄——发送时需要把焦点还给它的聊天输入框。
        /// </summary>
        public void OpenForCoexist(Rect qqPanelRect, IntPtr qqHwnd)
        {
            _coexistMode = true;
            _viewModel.SetQuickPanelCoexistTarget(qqHwnd);

            IntPtr monitor = MonitorFromPoint(
                new POINT { X = (int)(qqPanelRect.Left + qqPanelRect.Width / 2), Y = (int)(qqPanelRect.Top + qqPanelRect.Height / 2) },
                MONITOR_DEFAULTTONEAREST);
            bool alreadyVisible = IsVisible;
            PositionOnMonitor(monitor, (int)qqPanelRect.Left, (int)qqPanelRect.Top, isCoexistAnchor: true, anchorWidthPx: (int)qqPanelRect.Width);

            if (alreadyVisible)
            {
                // 共存链路会把同一个动作重复调进来（鼠标钩子乐观打开 → 焦点兜底 → QQ 真面板出现各触发一次）：
                // 已显示时只按最新矩形自校正位置，不重播弹出动画，否则用户看到连播两次。
                // 若淡出正在跑（QQ 面板刚关又被立刻点开），取消淡出收尾并恢复不透明，防止收尾定时器把窗口 Hide 掉。
                if (_hideTimer != null)
                {
                    _hideTimer.Stop();
                    _hideTimer = null;
                    PanelRoot.BeginAnimation(OpacityProperty, null);
                    PanelRoot.Opacity = 1;
                }
                QqPanelWatcher.Log("coexist re-open: repositioned only (show animation skipped)");
            }
            else
            {
                ResortForOpen();
                Show();
                PlayShowAnimation();
            }
        }

        // 以物理像素锚点在指定显示器上定位面板；scale 用于物理像素 ↔ DIP 换算
        private void PositionOnMonitor(IntPtr monitor, int anchorX, int anchorY, bool isCoexistAnchor = false, int anchorWidthPx = 0)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(monitor, ref mi);
            GetDpiForMonitor(monitor, MONITOR_DPI_TYPE_EFFECTIVE, out uint dpiX, out _);
            double scale = dpiX / 96.0;

            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : Height;

            double left;
            double top;
            if (isCoexistAnchor)
            {
                // 优先贴 QQ 面板左侧，放不下贴右侧，垂直顶对齐
                double qLeft = anchorX / scale;
                double qTop = anchorY / scale;
                double workLeft = mi.rcWork.Left / scale;
                double workRight = mi.rcWork.Right / scale;
                double workTop = mi.rcWork.Top / scale;
                double workBottom = mi.rcWork.Bottom / scale;

                left = qLeft - width - 8;
                if (left < workLeft + 4) left = qLeft + anchorWidthPx / scale + 8;
                top = qTop;
                if (left + width > workRight - 4) left = workRight - width - 8;
                if (top + height > workBottom - 4) top = workBottom - height - 8;
                if (top < workTop + 4) top = workTop + 4;
                if (left < workLeft + 4) left = workLeft + 4;
            }
            else
            {
                // 热键模式：让光标落在面板内部（标题栏区域），保证“鼠标进入→离开→自动关闭”链路成立
                left = anchorX / scale - 30;
                top = anchorY / scale - 30;
                if (left + width > mi.rcWork.Right / scale) left = mi.rcWork.Right / scale - width - 8;
                if (top + height > mi.rcWork.Bottom / scale) top = mi.rcWork.Bottom / scale - height - 8;
                if (left < mi.rcWork.Left / scale) left = mi.rcWork.Left / scale + 8;
                if (top < mi.rcWork.Top / scale) top = mi.rcWork.Top / scale + 8;
            }

            Left = left;
            Top = top;
        }

        // --- 关闭规则 ---

        private void PanelRoot_MouseEnter(object sender, MouseEventArgs e) => _closeTimer.Stop();

        private void PanelRoot_MouseLeave(object sender, MouseEventArgs e)
        {
            // 共存模式：生命周期跟随 QQ 表情面板（鼠标本来就停在 QQ 侧），不因鼠标离开而收起
            // 钉住模式（设置唤出）：显式关闭制，鼠标离开不收
            if (_coexistMode || _pinned) return;
            _closeTimer.Start();
        }

        private void CloseTimer_Tick(object? sender, EventArgs e)
        {
            _closeTimer.Stop();
            if (!IsMouseOver) HideSoft();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => HideSoft();

        /// <summary>
        /// 搜索按钮：快捷面板是非激活窗口收不到键盘输入，搜索场景跳转到主界面（可输入）
        /// 并自动聚焦搜索框。跳转后清空共存目标，避免残留的粘贴指向。
        /// </summary>
        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.SetQuickPanelCoexistTarget(IntPtr.Zero);
            HideSoft();
            (Application.Current.MainWindow as MainWindow)?.FocusSearchFromQuickPanel();
        }

        // --- Win32 ---

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int MONITOR_DPI_TYPE_EFFECTIVE = 0;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

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
    }
}
