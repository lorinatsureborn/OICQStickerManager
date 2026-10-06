using OICQStickerManager.Models;
using OICQStickerManager.Services;
using OICQStickerManager.ViewModels;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfAnimatedGif;

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
        private readonly QqCoexistLifetime _coexistLifetime;
        private readonly Size _preferredSize;
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
            _preferredSize = new Size(Width, Height);
            _viewModel = viewModel;
            DataContext = viewModel;
            _coexistLifetime = new QqCoexistLifetime((host, reason) =>
            {
                QqPanelWatcher.DismissCoexist(host, reason);
                _viewModel.SetQuickPanelCoexistTarget(IntPtr.Zero);
                HideImmediately();
            }, action => Dispatcher.BeginInvoke(DispatcherPriority.Send, action));

            // 面板隐藏/关闭时停掉格子内 GIF 动画（Hide 不触发 Unloaded，动画会在幕后空转耗 CPU）
            // 可见性同时镜像给 QqPanelWatcher.CoexistPanelShowing：鼠标钩子在后台线程判断
            // 表情按钮点击是开是关，只读这个 int（依赖属性不能跨线程读）。CoexistMode 的赋值
            // 都发生在 Show 之前（OpenForCoexist/OpenNearCursorCore），镜像不会漏记模式。
            IsVisibleChanged += (_, e) =>
            {
                Volatile.Write(ref QqPanelWatcher.CoexistPanelShowing,
                    (bool)e.NewValue && _coexistMode ? 1 : 0);
                if (!(bool)e.NewValue) { StopPanelGifAnimation(); _coexistLifetime.Stop(); }
            };

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
            // 关 QQ 原生表情面板不在这里做：它的 UIA Invoke 会把 QQ 焦点搬到表情按钮上，
            // 与共存发送的焦点修复并发互踩，会把搜狗输入法的上下文搞脱钩（候选框跑屏幕边缘）——
            // 由 CoexistPasteAsync 串行完成预关闭、焦点恢复和粘贴。
            viewModel.QuickPanelSendInitiated += OnSendInitiated;
        }

        private void OnSendInitiated(object? sender, EventArgs e)
        {
            HideImmediately();
        }

        // VM 是 App 静态单例、面板会随换肤整体重建：不退订的话旧窗体整棵对象图被静态根吊住
        protected override void OnClosed(EventArgs e)
        {
            _coexistLifetime.Dispose();
            if (_viewModel != null) _viewModel.QuickPanelSendInitiated -= OnSendInitiated;
            base.OnClosed(e);
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
            _coexistLifetime.Stop();
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

        internal void HideImmediately()
        {
            _closeTimer.Stop(); _hideTimer?.Stop(); _hideTimer = null;
            _coexistLifetime.Stop();
            Hide();
            PanelRoot.BeginAnimation(OpacityProperty, null); PanelRoot.Opacity = 1;
            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PanelScale.ScaleX = PanelScale.ScaleY = 1;
        }

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
            ScrollToTop();
        }

        // 每次唤出都回到顶部（2026-10-02 用户定案：不要滚动记忆——置顶区就是最近用过的，
        // 唤出即见）。Show 后布局未完成时 ScrollToTop 会被忽略，故排到 Loaded 优先级之后执行
        private void ScrollToTop()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var scroll = FindDescendant<System.Windows.Controls.ScrollViewer>(PanelList);
                scroll?.ScrollToTop();
            });
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T match) return match;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = FindDescendant<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        // ———— GIF 悬浮原地动画（与图库共用开关 EnableGifHoverPreview；2026-10-02 用户定案）————
        // 图库悬浮弹气泡预览，面板空间紧凑改为格子内原地播放：悬浮时把动画挂到覆盖层 Image 上
        // 盖住静态首帧。绝不直接动 StaticFrame 的 Source——WpfAnimatedGif 的动画会接管 Source
        // 属性且摘除后不回填绑定值（实测格子直接空掉）。移开/隐藏/容器回收即隐藏覆盖层回落首帧，
        // 同一时刻至多一张在动，不影响面板呼出性能。

        private Image? _animatedGifHost;    // 触发动画的静态图（事件源，用于配对清理）
        private Image? _animatedGifOverlay; // 正在播动画的覆盖层

        private void PanelGifImage_MouseEnter(object sender, MouseEventArgs e)
        {
            StopPanelGifAnimation();
            if (sender is not Image img) return;
            if (DataContext is not MainViewModel vm || !vm.EnableGifHoverPreview) return;
            if (img.DataContext is not StickerModel sticker || !sticker.IsGif || !File.Exists(sticker.FullPath)) return;
            if (img.FindName("AnimatedFrame") is not Image overlay) return;

            // ImageSource 是缩放解码的首帧且已冻结，带不动动画；须从文件新解一份原始 GIF
            // （WpfAnimatedGif 要读解码器帧序列：不可 Freeze，也不可设 DecodePixelWidth）
            var animated = new BitmapImage();
            animated.BeginInit();
            animated.CacheOption = BitmapCacheOption.OnLoad;
            animated.UriSource = new Uri(sticker.FullPath);
            animated.EndInit();
            _animatedGifHost = img;
            _animatedGifOverlay = overlay;
            overlay.Visibility = Visibility.Visible;
            ImageBehavior.SetAnimatedSource(overlay, animated); // 挂上即自动循环播放
        }

        private void PanelGifImage_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Image img && ReferenceEquals(_animatedGifHost, img)) StopPanelGifAnimation();
        }

        // VirtualizationMode=Recycling：格子容器滚动回收时必须停动画并隐藏覆盖层，否则旧 GIF 会借别的表情格子还魂
        private void PanelGifImage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Image img &&
                (ReferenceEquals(_animatedGifHost, img) || ReferenceEquals(_animatedGifOverlay, img)))
                StopPanelGifAnimation();
        }

        private void StopPanelGifAnimation()
        {
            if (_animatedGifOverlay == null) return;
            ImageBehavior.SetAnimatedSource(_animatedGifOverlay, null); // 释放 GIF 解码与帧动画
            _animatedGifOverlay.Visibility = Visibility.Collapsed;      // 露出底下静态首帧绑定
            _animatedGifOverlay = null;
            _animatedGifHost = null;
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
            _coexistLifetime.Stop();
            _coexistMode = false;
            _pinned = pinned;
            _viewModel.SetQuickPanelCoexistTarget(IntPtr.Zero);
            if (IsVisible) Volatile.Write(ref QqPanelWatcher.CoexistPanelShowing, 0);

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
        public void OpenForCoexist(Rect qqPanelRect, IntPtr qqHwnd, Rect buttonRect = default)
        {
            _coexistMode = true;
            _viewModel.SetQuickPanelCoexistTarget(qqHwnd);
            // 已可见路径不触发 IsVisibleChanged（如热键面板转共存），镜像在此补记
            if (IsVisible) Volatile.Write(ref QqPanelWatcher.CoexistPanelShowing, 1);

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
            _coexistLifetime.Follow(qqHwnd, new WindowInteropHelper(this).Handle, qqPanelRect,
                buttonRect.Width > 0 ? buttonRect : QqPanelWatcher.CachedButtonRect(qqHwnd));
        }

        // 以物理像素锚点在指定显示器上定位面板；scale 用于物理像素 ↔ DIP 换算
        private void PositionOnMonitor(IntPtr monitor, int anchorX, int anchorY, bool isCoexistAnchor = false, int anchorWidthPx = 0)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(monitor, ref mi)) return;
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            // Move first so GetDpiForWindow observes the destination monitor, not the previous one.
            if (!SetWindowPos(hwnd, IntPtr.Zero, anchorX, anchorY, 0, 0, 0x0015)) return;
            uint dpiX = GetDpiForWindow(hwnd);
            double scale = dpiX / 96.0;

            var workArea = new Rect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
            var placement = PanelPlacement.Place(workArea, _preferredSize, new Point(anchorX, anchorY), scale, isCoexistAnchor, anchorWidthPx);
            if (!placement.IsEmpty)
                SetWindowPos(hwnd, IntPtr.Zero, (int)placement.Left, (int)placement.Top, (int)placement.Width, (int)placement.Height, 0x0014);
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
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
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
