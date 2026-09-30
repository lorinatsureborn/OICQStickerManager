using OICQStickerManager.Models;
using OICQStickerManager.Services;
using OICQStickerManager.ViewModels;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfAnimatedGif;

namespace OICQStickerManager.Views
{
    /// <summary>
    /// 主窗口：雾玻璃 + iOS 26 设计语言。浮层（标签编辑器/设置/Alert）统一走
    /// scrim 淡入 + sheet 缩放弹出的呈现动效。
    /// </summary>
    public partial class MainWindow : GlassWindow
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = App.SharedViewModel;

            // 页脚中部可用宽度随窗口缩放变化，正在悬浮的标签需要自适应重排
            HoverTagsSlot.SizeChanged += (_, _) => { if (_hoveredSticker != null) ShowHoverTags(); };
            // 滚动为原生行为；ScrollChanged 仅用于驱动图库入场的方向判定与待播检查
            StickersList.Loaded += (_, _) =>
            {
                _galleryScroll ??= FindDescendant<ScrollViewer>(StickersList);
                if (_galleryScroll != null) _galleryScroll.ScrollChanged += GalleryScroll_Changed;
            };

            // 共存触发器开关、轮询保底、热键变化、选项卡切换时实时生效
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.EnableQqCoexistTrigger)
                        || e.PropertyName == nameof(MainViewModel.EnableWatcherPolling))
                    {
                        Dispatcher.Invoke(UpdateWatcherState);
                    }
                    else if (e.PropertyName == nameof(MainViewModel.HotkeyModifiers)
                        || e.PropertyName == nameof(MainViewModel.HotkeyKey))
                    {
                        Dispatcher.Invoke(RegisterCurrentHotkey);
                    }
                    else if (e.PropertyName == nameof(MainViewModel.CloseToTray)
                        || e.PropertyName == nameof(MainViewModel.SilentStart))
                    {
                        // 托盘驻留规则：关闭进托盘 或 启动静默 开启时，托盘图标必须常驻
                        Dispatcher.Invoke(UpdateTrayIcon);
                    }
                    else if (e.PropertyName == nameof(MainViewModel.EnableGifHoverPreview))
                    {
                        // 关掉预览时立刻收起可能正开着的气泡
                        if (!vm.EnableGifHoverPreview) Dispatcher.Invoke(CloseGifPreview);
                    }
                    else if (e.PropertyName == nameof(MainViewModel.StatusText))
                    {
                        // 状态文字已不再展示（用户定案），但失败不能静默：弹一次 iOS Alert，
                        // 成功/进度不打扰。同一条失败去重（自动保存周期性触发，消息可能重复）。
                        var status = vm.StatusText;
                        if (!status.Contains("失败"))
                        {
                            _lastFailureStatus = null; // 成功/进度重置去重：下次同类失败仍要提醒
                        }
                        else if (status != _lastFailureStatus && _alertTcs == null)
                        {
                            _lastFailureStatus = status;
                            var title = status.StartsWith("配置保存失败", StringComparison.Ordinal) ? "配置保存失败"
                                : status.StartsWith("保存失败", StringComparison.Ordinal) ? "表情数据保存失败"
                                : "出了点问题";
                            _ = ShowAlertAsync(title, status, "知道了", showCancel: false);
                        }
                    }
                    else if (e.PropertyName == nameof(MainViewModel.SelectedTab))
                    {
                        // 切到 QQ（账号）页时网格数据源换成该账号镜像，切回时还原图库视图
                        Dispatcher.Invoke(UpdateGalleryMode);
                    }
                    else if (e.PropertyName == nameof(MainViewModel.StickerCountText))
                    {
                        // QQ 页镜像增删会刷新计数文本，顺带维护空状态
                        if (vm.IsQqTabActive) Dispatcher.Invoke(() => UpdateQqEmptyState(vm.CurrentQqService));
                    }
                }; // ← 修正：+= 事件挂接没有括号，此前误写为 "});"（另一会话遗留笔误）

                vm.QqImportCompleted += (imported, duplicates, unsupported) =>
                    Dispatcher.Invoke(() => OnQqImportCompleted(imported, duplicates, unsupported));

                // 深度同步需要密钥：三选（立即读 / 下次登录自动抓 / 取消）
                vm.KeyAcquisitionRequested += (s, e) => Dispatcher.Invoke(() =>
                    _ = RunKeyAcquisitionFlowAsync());

                // 初始对齐设置面板中色板的选中态
                foreach (var option in vm.ThemeOptions) option.IsSelected = option.Id == ThemeManager.Current.Id;
            }
            // 空闲时预构建并预热快捷面板：把窗口构建、容器物化、图片解码等一次性成本付在启动时
            Loaded += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                try
                {
                    if (_quickPanel == null) _quickPanel = new QuickPanelWindow((MainViewModel)DataContext);
                    _quickPanel.WarmUp();
                }
                catch (Exception ex)
                {
                    QqPanelWatcher.Log("panel warmup failed: " + ex.Message);
                }

                // 启动通知串行弹出（共用一个 Alert 通道，不能并发叠加）：
                // 先 QQ 绑定询问，再 WebP 环境警告
                _ = RunStartupNoticesAsync();

                // 深度同步开着但密钥还没拿到 → 静默续抓（等下次 QQ 登录）
                if (DataContext is MainViewModel vmResume) vmResume.ResumeKeyWatcherIfPending();
            });

            // 发送表情会最小化窗口；窗口失焦时收起 GIF 预览并停止计时，避免残留
            Deactivated += (s, e) =>
            {
                _gifPreviewTimer?.Stop();
                CloseGifPreview();
            };

            // 切换配色后丢弃旧快捷面板（其 BAML 资源表达式不响应运行时换肤），
            // 空闲时按新配色重建并预热
            ThemeManager.ThemeChanged += OnThemeChangedDiscardQuickPanel;
        }

        private void OnThemeChangedDiscardQuickPanel(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (_quickPanel == null) return;
                _quickPanel.Close();
                _quickPanel = null;
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    try
                    {
                        if (_quickPanel == null && DataContext is MainViewModel vm)
                        {
                            _quickPanel = new QuickPanelWindow(vm);
                            _quickPanel.WarmUp();
                        }
                    }
                    catch (Exception ex)
                    {
                        QqPanelWatcher.Log("panel warmup failed: " + ex.Message);
                    }
                });
            });
        }

        // ———— 浮层动效（scrim 淡入淡出 + sheet 缩放弹出） ————

        private static readonly IEasingFunction SheetEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        private void ShowOverlay(Grid scrim, FrameworkElement sheet, ScaleTransform scale, double fromScale)
        {
            sheet.BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            scrim.Visibility = Visibility.Visible;
            scrim.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = SheetEase });

            scale.ScaleX = scale.ScaleY = fromScale;
            sheet.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = SheetEase });
            var grow = new DoubleAnimation(fromScale, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = SheetEase };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        private void HideOverlay(Grid scrim, FrameworkElement sheet, ScaleTransform scale, double toScale)
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = SheetEase };
            fade.Completed += (s, e) => scrim.Visibility = Visibility.Collapsed;
            scrim.BeginAnimation(OpacityProperty, fade);

            sheet.BeginAnimation(OpacityProperty, fade);
            var shrink = new DoubleAnimation(1, toScale, TimeSpan.FromMilliseconds(160)) { EasingFunction = SheetEase };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }

        // ———— 窗口控制 ————

        // 无 WindowChrome，标题栏拖拽手工实现（搜索框/按钮等交互元素自行处理点击）
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); }
            catch { /* 鼠标状态异常时忽略 */ }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ———— 托盘驻留 ————

        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private bool _forceExit;       // 托盘菜单"退出"置位：OnClosing 放行真正关闭
        private bool _trayBalloonShown; // 首次藏进托盘时的提示只弹一次

        /// <summary>
        /// 托盘唤起（托盘图标点击 / 第二实例唤醒共用）：恢复显示并抢回前台。
        /// </summary>
        internal void RestoreFromTray()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        // 关闭按钮：真退出走托盘菜单（_forceExit 放行）；
        // 首次 ✕ 且未做过选择 → 弹一次询问并记住；已选择托盘 → 拦截只藏窗口
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_forceExit && DataContext is MainViewModel vm)
            {
                if (!vm.CloseBehaviorDecided)
                {
                    e.Cancel = true;
                    if (_alertTcs == null) _ = AskCloseBehaviorAsync(vm); // 已有弹窗在前台时只吞掉本次关闭
                    return;
                }
                if (vm.CloseToTray)
                {
                    e.Cancel = true;
                    HideToTray();
                    return;
                }
            }
            base.OnClosing(e);
        }

        // 首次关闭询问：问一次"退出还是托盘"并记住；Esc/点空白关掉弹窗 = 本次不打扰也不记忆
        private async Task AskCloseBehaviorAsync(MainViewModel vm)
        {
            var minimizeToTray = await ShowAlertAsync(
                "要退出飞鸟吗？",
                "可以最小化到系统托盘随时唤起（快捷面板与热键保持可用），也可以完全退出。之后可在 设置 → 通用 修改此行为。",
                confirm: "最小化到托盘",
                showCancel: true,
                cancel: "退出应用");

            if (!minimizeToTray && !_alertCancelClicked) return; // 弹窗被关掉而非做出选择
            vm.DecideCloseBehavior(minimizeToTray);
            if (minimizeToTray) HideToTray();
            else ExitApplication();
        }

        private void HideToTray()
        {
            Hide();
            if (!_trayBalloonShown && _trayIcon != null)
            {
                _trayBalloonShown = true;
                _trayIcon.BalloonTipTitle = "飞鸟仍在运行";
                _trayIcon.BalloonTipText = "已最小化到托盘，点击托盘图标恢复窗口；右键图标可退出。";
                _trayIcon.ShowBalloonTip(3000);
            }
        }

        // 托盘驻留规则：关闭进托盘 或 启动静默 任一开启时图标常驻（静默启动没有图标就唤不回了）
        private void UpdateTrayIcon()
        {
            if (DataContext is not MainViewModel vm) return;
            bool needTray = vm.CloseToTray || vm.SilentStart;
            if (needTray && _trayIcon == null)
            {
                _trayIcon = CreateTrayIcon();
            }
            else if (!needTray && _trayIcon != null)
            {
                _trayIcon.Dispose(); // Dispose 立即移除 shell 图标
                _trayIcon = null;
            }
        }

        private System.Windows.Forms.NotifyIcon CreateTrayIcon()
        {
            var icon = new System.Windows.Forms.NotifyIcon { Text = "飞鸟", Visible = true };
            try
            {
                using var stream = Application.GetResourceStream(
                    new Uri("pack://application:,,,/Assets/Asuka.ico"))!.Stream;
                icon.Icon = new System.Drawing.Icon(stream);
            }
            catch { /* 图标读取失败时托盘功能仍可用，只是没有图形 */ }

            icon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) RestoreFromTray();
                else if (e.Button == System.Windows.Forms.MouseButtons.Right) OpenTrayMenu();
            };
            return icon;
        }

        // 右键托盘：复用 Tokens.xaml 的玻璃样式 ContextMenu（隐式样式对代码创建的元素同样生效）
        private void OpenTrayMenu()
        {
            var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = this };

            var open = new MenuItem { Header = "打开飞鸟" };
            open.Click += (s, e) => RestoreFromTray();
            var exit = new MenuItem { Header = "退出" };
            exit.Click += (s, e) => ExitApplication();
            menu.Items.Add(open);
            menu.Items.Add(exit);

            menu.IsOpen = true;
        }

        private void ExitApplication()
        {
            _forceExit = true;
            Close(); // OnClosed 清理热键/watcher/托盘图标，ShutdownMode=OnMainWindowClose 结束进程
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
            => ShowOverlay(SettingsOverlay, SettingsSheet, SettingsSheetScale, 0.94);

        private void CloseSettings_Click(object sender, RoutedEventArgs e)
            => HideOverlay(SettingsOverlay, SettingsSheet, SettingsSheetScale, 0.94);

        private void OpenQuickPanel_Click(object sender, RoutedEventArgs e)
        {
            HideOverlay(SettingsOverlay, SettingsSheet, SettingsSheetScale, 0.94);
            ToggleQuickPanel();
        }

        private void ThemeSwatch_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.RadioButton radio
                && radio.DataContext is ThemeOption option
                && DataContext is MainViewModel vm)
            {
                vm.SelectedTheme = option.Id;
            }
        }

        // 点击 scrim 空白处关闭浮层（点在 sheet 上时 OriginalSource 是内容元素，不会命中）
        private void OverlayScrim_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not Grid) return;
            if (sender == AlertOverlay) CancelAlert();
            else if (sender == SettingsOverlay) CloseSettings_Click(sender, e);
            else if (sender == TagEditorOverlay) CloseTagEditor_Click(sender, e);
            else if (sender == QqBindOverlay) CloseQqBindDialog_Click(sender, e);
            else if (sender == QqRenameOverlay) CloseQqRename_Click(sender, e);
        }

        // Esc 逐层退出：Alert → 设置 → QQ 绑定 → QQ 重命名 → 标签编辑器
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (AlertOverlay.Visibility == Visibility.Visible) { CancelAlert(); e.Handled = true; }
            else if (SettingsOverlay.Visibility == Visibility.Visible) { CloseSettings_Click(sender, e); e.Handled = true; }
            else if (QqBindOverlay.Visibility == Visibility.Visible) { CloseQqBindDialog_Click(sender, e); e.Handled = true; }
            else if (QqRenameOverlay.Visibility == Visibility.Visible) { CloseQqRename_Click(sender, e); e.Handled = true; }
            else if (TagEditorOverlay.Visibility == Visibility.Visible) { CloseTagEditor_Click(sender, e); e.Handled = true; }
        }

        // ———— iOS 风格 Alert ————

        private TaskCompletionSource<bool>? _alertTcs;
        private Action? _alertNeutralAction;
        private bool _alertCancelClicked; // 区分"点了取消按钮"与"Esc/点空白关掉弹窗"：前者是选择，后者不算
        private string? _lastFailureStatus; // 失败弹窗去重：同一条失败只弹一次

        private Task<bool> ShowAlertAsync(string title, string message, string confirm = "好",
            bool destructive = false, bool showCancel = true,
            string? neutral = null, Action? onNeutral = null,
            string? cancel = null)
        {
            _alertTcs = new TaskCompletionSource<bool>();
            _alertNeutralAction = onNeutral;
            _alertCancelClicked = false;
            AlertTitle.Text = title;
            AlertMessage.Text = message;
            AlertMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            AlertConfirmButton.Content = confirm;
            AlertConfirmButton.Foreground = destructive
                ? FindResource("DangerBrush") as Brush
                : FindResource("AccentBrush") as Brush;
            AlertCancelButton.Content = cancel ?? "取消";
            AlertCancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

            // 中性按钮（如"不再询问"）：显示时展开为三等分；隐藏时折叠回双按钮布局
            if (neutral != null)
            {
                AlertNeutralButton.Content = neutral;
                AlertNeutralButton.Visibility = Visibility.Visible;
                AlertNeutralSeparator.Visibility = Visibility.Visible;
                AlertNeutralColumn.Width = new GridLength(1, GridUnitType.Star);
                AlertNeutralSeparatorColumn.Width = new GridLength(1, GridUnitType.Auto);
            }
            else
            {
                AlertNeutralButton.Visibility = Visibility.Collapsed;
                AlertNeutralSeparator.Visibility = Visibility.Collapsed;
                AlertNeutralColumn.Width = new GridLength(0);
                AlertNeutralSeparatorColumn.Width = new GridLength(0);
            }

            ShowOverlay(AlertOverlay, AlertCard, AlertCardScale, 0.9);
            return _alertTcs.Task;
        }

        private void CancelAlert()
        {
            if (_alertTcs == null) return;
            var tcs = _alertTcs;
            _alertTcs = null;
            HideOverlay(AlertOverlay, AlertCard, AlertCardScale, 0.9);
            tcs.TrySetResult(false);
        }

        private void AlertConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (_alertTcs == null) return;
            var tcs = _alertTcs;
            _alertTcs = null;
            HideOverlay(AlertOverlay, AlertCard, AlertCardScale, 0.9);
            tcs.TrySetResult(true);
        }

        // 中性按钮：结果与取消相同（false），但触发附加动作（如记住"不再询问"）
        private void AlertNeutral_Click(object sender, RoutedEventArgs e)
        {
            var action = _alertNeutralAction;
            _alertNeutralAction = null;
            CancelAlert();
            action?.Invoke();
        }

        private void AlertCancel_Click(object sender, RoutedEventArgs e)
        {
            _alertCancelClicked = true;
            CancelAlert();
        }

        // ———— 全局热键与快捷面板 ————

        private const int HotKeyId = 0xA113;
        private const uint MOD_ALT = 0x1;
        private const uint MOD_CONTROL = 0x2;
        private const uint VK_D = 0x44; // 默认热键 Ctrl+Alt+D（左手单手可按，实机扫描选定；旧默认 E 被 QQ 占用、K 离左手太远）
        private const int WM_HOTKEY = 0x0312;
        private bool _hotkeyRegistered;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private QuickPanelWindow? _quickPanel;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndProc);
            ClipboardCapture.Register(new WindowInteropHelper(this).Handle); // M2 剪贴板图片捕获
            RegisterCurrentHotkey();
            UpdateWatcherState();
        }

        // 按当前设置注册热键：先注销旧组合再注册新组合；冲突时状态栏提示（快捷面板设置按钮仍是可用入口）
        private void RegisterCurrentHotkey()
        {
            if (DataContext is not MainViewModel vm) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (_hotkeyRegistered)
            {
                UnregisterHotKey(hwnd, HotKeyId);
                _hotkeyRegistered = false;
            }
            if (vm.HotkeyKey == 0) return;

            if (RegisterHotKey(hwnd, HotKeyId, vm.HotkeyModifiers, vm.HotkeyKey))
            {
                _hotkeyRegistered = true;
            }
            else
            {
                vm.StatusText = $"快捷键 {vm.HotkeyDisplay} 注册失败（可能被其他程序占用），可在设置中更换";
            }
        }

        // 热键捕获：点击捕获框后按下组合键即完成设置（Esc 取消；纯修饰键忽略）
        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey
                    : e.Key == Key.ImeProcessed ? e.ImeProcessedKey // IME/输入服务处理过的按键，回读底层真实键
                    : e.Key;
            // Key/SystemKey/ImeProcessedKey 都是非可空枚举，key 不可能为 null；只过滤无法作为热键主键的键
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
                or Key.Escape or Key.Tab or Key.ImeProcessed or Key.System)
            {
                return;
            }

            var mods = Keyboard.Modifiers;
            bool isFKey = key is >= Key.F1 and <= Key.F12;
            if (mods == ModifierKeys.None && !isFKey)
            {
                vm.StatusText = "热键需至少包含 Ctrl/Alt/Shift/Win 之一，或使用 F1-F12";
                return;
            }

            vm.HotkeyModifiers = (uint)mods;
            vm.HotkeyKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            vm.StatusText = $"快捷键已更改为 {vm.HotkeyDisplay}";
        }

        private void ResetHotkey_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            vm.HotkeyModifiers = MOD_CONTROL | MOD_ALT;
            vm.HotkeyKey = VK_D;
            vm.StatusText = "快捷键已恢复为 Ctrl + Alt + E";
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotKeyId)
            {
                ToggleQuickPanel();
                handled = true;
            }
            else if (msg == ClipboardCapture.WM_CLIPBOARDUPDATE)
            {
                // 剪贴板更新去抖：部分应用一次复制会连写两次
                _clipboardDebounce ??= NewClipboardDebounce();
                _clipboardDebounce.Stop();
                _clipboardDebounce.Start();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private DispatcherTimer? _clipboardDebounce;

        private DispatcherTimer NewClipboardDebounce()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                EvaluateClipboardCapture();
            };
            return timer;
        }

        // M2：剪贴板里有图片 → 落临时文件 → 右下角非激活轻提示一键入库
        private void EvaluateClipboardCapture()
        {
            try
            {
                if (ClipboardCapture.Suppress) return; // 发送通道写入/恢复期间静默
                if (DataContext is not MainViewModel vm || !vm.CaptureClipboardImages) return;
                if (_alertTcs != null) return; // 有弹窗在前台时不叠加

                var data = Clipboard.GetDataObject();
                if (data == null) return;

                // 优先取文件（聊天工具"复制图片"常给 FileDropList），其次取位图
                string? path = null;
                bool isTemp = false;
                byte[]? pngBytes = null;
                string sig;
                if (data.GetDataPresent(DataFormats.FileDrop)
                    && data.GetData(DataFormats.FileDrop) is string[] files
                    && files.Length > 0 && File.Exists(files[0])
                    && ImageSniffer.Detect(files[0]) != ImageKind.Unknown)
                {
                    path = files[0];
                    var fi = new FileInfo(path);
                    sig = "f|" + path + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                }
                else if (Clipboard.ContainsImage())
                {
                    var src = Clipboard.GetImage();
                    if (src == null) return;

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(src));
                    using var ms = new MemoryStream();
                    encoder.Save(ms);
                    pngBytes = ms.ToArray();

                    var dir = Path.Combine(Path.GetTempPath(), "asuka-capture");
                    Directory.CreateDirectory(dir);
                    path = Path.Combine(dir, $"clip-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                    File.WriteAllBytes(path, pngBytes);
                    isTemp = true;
                    sig = "b|" + Convert.ToHexString(System.Security.Cryptography.MD5.HashData(pngBytes));
                }
                else return;

                if (path == null) return;

                // 内容级去重（2026-10-01 用户实测：部分应用会周期性重写相同剪贴板内容，
                // 同一张图的入库提示反复弹）：
                // ①已入库的图（MD5 与图库对账）直接静默，复制多少遍都不再提示；
                // ②与上一次提示过的内容相同也静默——除非期间它被删出图库（那时 MD5 对不上，会重新提示）
                try
                {
                    var md5Hex = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                        pngBytes ?? File.ReadAllBytes(path)));
                    if (vm.Stickers.Any(s => s.Md5 == md5Hex))
                    {
                        _lastClipboardSig = sig;
                        return;
                    }
                }
                catch { /* 哈希失败不拦截提示 */ }
                if (sig == _lastClipboardSig) return;
                _lastClipboardSig = sig;

                ShowCaptureToast(path, isTemp);
            }
            catch { /* 剪贴板被占用/无图等，忽略本次 */ }
        }

        private string? _lastClipboardSig;

        private ClipboardToastWindow? _captureToast;

        private void ShowCaptureToast(string imagePath, bool isTemp)
        {
            // 新捕获顶掉旧的（旧临时文件顺带清理）
            if (_captureToast != null)
            {
                var old = _captureToast;
                _captureToast = null;
                old.Close();
            }

            ImageSource? thumb = null;
            try { thumb = LoadBitmapScaled(imagePath, 96); } catch { }
            if (thumb == null) return;

            var durationSec = DataContext is MainViewModel vm ? vm.ToastDurationSeconds : 8;
            var toast = new ClipboardToastWindow(thumb, "复制了图片", durationSec * 1000);
            _captureToast = toast;
            toast.ImportClicked += () =>
            {
                if (_captureToast == toast) _captureToast = null;
                _ = HandleCaptureImportAsync(imagePath, isTemp);
            };
            toast.Dismissed += () =>
            {
                if (_captureToast == toast) _captureToast = null;
                if (isTemp) TryDeleteCaptureFile(imagePath);
            };
            toast.Show();
        }

        private async Task HandleCaptureImportAsync(string imagePath, bool isTemp)
        {
            try
            {
                if (DataContext is MainViewModel vm)
                {
                    var report = await vm.AddStickersFromPathAsync(imagePath);
                    OnQqImportCompleted(report.Added, report.Duplicates, report.Unsupported);
                }
            }
            finally
            {
                if (isTemp) TryDeleteCaptureFile(imagePath);
            }
        }

        private void TryDeleteCaptureFile(string path)
        {
            try
            {
                // 只清理自己落在 asuka-capture 里的临时文件，外部文件（如聊天工具给的路径）不动
                if (path.Contains(Path.Combine(Path.GetTempPath(), "asuka-capture"), StringComparison.OrdinalIgnoreCase)
                    && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch { /* 占用则留给系统临时目录清理 */ }
        }

        private QqPanelWatcher? _panelWatcher;

        private void ToggleQuickPanel()
        {
            if (_quickPanel == null) _quickPanel = new QuickPanelWindow((MainViewModel)DataContext);
            if (_quickPanel.IsVisible) _quickPanel.HideSoft();
            else _quickPanel.OpenNearCursor();
        }

        // 共存触发器：监听 QQ 原生表情面板的出现/消失，联动快捷面板
        private void UpdateWatcherState()
        {
            if (DataContext is not MainViewModel vm) return;
            if (vm.EnableQqCoexistTrigger)
            {
                _panelWatcher ??= CreatePanelWatcher();
                _panelWatcher.SetPollingFallback(vm.EnableWatcherPolling);
                _panelWatcher.Start();
            }
            else
            {
                _panelWatcher?.Dispose();
                _panelWatcher = null;
            }
        }

        private QqPanelWatcher CreatePanelWatcher()
        {
            // 这四个回调都跑在 UIA 的 COM 线程上，必须用 BeginInvoke 非阻塞投递：
            // 若用 Invoke，watcher.Dispose 时（UI 线程）RemoveAllEventHandlers 会等在途回调完成，
            // 而在途回调的 Invoke 又在等 UI 线程 → 死锁，进程永远退不出去（非确定复现）
            var watcher = new QqPanelWatcher(msg => Dispatcher.BeginInvoke(() =>
            {
                if (DataContext is MainViewModel vm) vm.StatusText = msg;
            }));

            watcher.PanelAppeared += (s, e) => Dispatcher.BeginInvoke(() =>
            {
                if (_quickPanel == null) _quickPanel = new QuickPanelWindow((MainViewModel)DataContext);
                _quickPanel.OpenForCoexist(e.PanelRect, e.HostHwnd);
            });

            // 按下 QQ 表情按钮的瞬间（乐观路径）：用缓存矩形立即打开，QQ 面板出现后 OpenForCoexist 会以真实矩形自校正；
            // 矩形为 Empty 表示本会话还没有过面板位置，等 PanelAppeared 再打开
            watcher.EmojiButtonClicked += (s, e) => Dispatcher.BeginInvoke(() =>
            {
                if (e.PanelRect == Rect.Empty) return;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (_quickPanel == null) _quickPanel = new QuickPanelWindow((MainViewModel)DataContext);
                _quickPanel.OpenForCoexist(e.PanelRect, e.HostHwnd);
                QqPanelWatcher.Log($"optimistic panel shown in {sw.ElapsedMilliseconds} ms");
            });

            watcher.PanelDisappeared += (s, e) => Dispatcher.BeginInvoke(() =>
            {
                // 仅收起共存模式打开的面板；热键打开的不受影响
                QqPanelWatcher.Log($"disappeared handler: coexist={_quickPanel?.CoexistMode.ToString() ?? "<null>"}, visible={_quickPanel?.IsVisible.ToString() ?? "<null>"}");
                if (_quickPanel != null && _quickPanel.CoexistMode) _quickPanel.HideSoft();
                if (DataContext is MainViewModel vm) vm.SetQuickPanelCoexistTarget(IntPtr.Zero);
            });

            return watcher;
        }

        /// <summary>
        /// 快捷面板搜索按钮的落点：恢复主窗口并把键盘焦点交给搜索框。
        /// 之后用户在主界面双击表情，走既有发送路径（最小化自身→焦点回聊天窗口→粘贴）。
        /// </summary>
        internal void FocusSearchFromQuickPanel()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        protected override void OnClosed(EventArgs e)
        {
            _quickPanel?.Close();
            UnregisterHotKey(new WindowInteropHelper(this).Handle, HotKeyId);
            ClipboardCapture.Unregister(new WindowInteropHelper(this).Handle);
            _captureToast?.Close();
            _captureToast = null;
            _panelWatcher?.Dispose();
            _panelWatcher = null;
            _trayIcon?.Dispose();
            _trayIcon = null;
            ThemeManager.ThemeChanged -= OnThemeChangedDiscardQuickPanel;
            base.OnClosed(e);
        }

        private List<StickerModel> _pendingStickers = new(); // 💡 修改为列表

        // ———— 拖放导入（带雾面反馈遮罩） ————

        private int _dragDepth;

        private void ListBox_DragEnter(object sender, DragEventArgs e)
        {
            _dragDepth++;
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) DropOverlay.Visibility = Visibility.Visible;
        }

        private void ListBox_DragLeave(object sender, DragEventArgs e)
        {
            if (--_dragDepth <= 0)
            {
                _dragDepth = 0;
                DropOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void ListBox_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void ListBox_Drop(object sender, DragEventArgs e)
        {
            _dragDepth = 0;
            DropOverlay.Visibility = Visibility.Collapsed;

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
                var viewModel = (ViewModels.MainViewModel)this.DataContext;

                _pendingStickers.Clear();
                var totalDup = 0;
                var totalUnsupported = 0;
                foreach (var path in paths)
                {
                    var report = await viewModel.AddStickersFromPathAsync(path);
                    _pendingStickers.AddRange(report.Added);
                    totalDup += report.Duplicates;
                    totalUnsupported += report.Unsupported;
                }

                if (_pendingStickers.Count > 0)
                {
                    // 💡 批量编辑模式
                    _editingTags = new List<string>(); // 初始标签为空
                    TagInputBox.Text = "";

                    // UI 显示第一张图片作为预览，并提示“批量编辑”（附跳过摘要）
                    SetTagEditorPreview(_pendingStickers[0]);
                    var summary = $"正在为 {_pendingStickers.Count} 个新表情设置标签";
                    var skips = new List<string>();
                    if (totalDup > 0) skips.Add($"{totalDup} 张重复已跳过");
                    if (totalUnsupported > 0) skips.Add($"{totalUnsupported} 张 WebP 暂不支持");
                    TagEditorSubtitle.Text = skips.Count > 0 ? $"{summary}（{string.Join("，", skips)}）" : summary;
                    viewModel.StatusText = $"正在为 {_pendingStickers.Count} 个新表情设置标签...";

                    RefreshEditorUI();
                    ShowOverlay(TagEditorOverlay, TagEditorSheet, TagEditorSheetScale, 0.94);
                    TagInputBox.Focus();
                }
                else if (totalDup > 0 || totalUnsupported > 0)
                {
                    var message = totalDup > 0
                        ? $"所选图片全部与图库重复（{totalDup} 张），没有新增。"
                        : $"{totalUnsupported} 张 WebP 图片无法导入（暂不支持该格式）。";
                    _ = ShowAlertAsync("没有新增表情", message, "知道了", showCancel: false);
                }
            }
        }

        private async void SaveTag_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingStickers != null && _pendingStickers.Count > 0)
            {
                // 1. 防呆添加
                if (!string.IsNullOrWhiteSpace(TagInputBox.Text)) PerformAddTag();

                // 2. 同步标签给所有待处理对象
                foreach (var sticker in _pendingStickers)
                {
                    sticker.Tags = new List<string>(_editingTags);
                }

                // 3. 核心持久化与视图刷新
                if (this.DataContext is ViewModels.MainViewModel viewModel)
                {
                    await viewModel.SaveDatabaseAsync(); // 存入 JSON
                    viewModel.UpdateTabTags();           // 刷新左侧选项卡列表
                    viewModel.RefreshTagPool();          // 刷新编辑器里的标签池
                }
            }

            ClearTagEditorPreview();
            HideOverlay(TagEditorOverlay, TagEditorSheet, TagEditorSheetScale, 0.94);
        }

        private void CloseTagEditor_Click(object sender, RoutedEventArgs e)
        {
            ClearTagEditorPreview();
            HideOverlay(TagEditorOverlay, TagEditorSheet, TagEditorSheetScale, 0.94);
        }

        private List<string> _editingTags = new(); // 临时存放正在编辑的标签

        private void ShowTagEditor(StickerModel sticker)
        {
            // 💡 统一入口：即使是编辑单个，也放入列表中
            _pendingStickers = new List<StickerModel> { sticker };

            // 拷贝标签用于编辑（如果是批量编辑，这里通常取第一张的标签或清空，按需决定）
            _editingTags = new List<string>(sticker.Tags);

            SetTagEditorPreview(sticker); // 预览图：GIF 播放动画，其他显示静态图
            TagEditorSubtitle.Text = sticker.DisplayName == "未命名表情"
                ? "为表情添加标签，方便搜索与整理"
                : sticker.DisplayName;
            RefreshEditorUI();

            if (this.DataContext is ViewModels.MainViewModel viewModel)
            {
                viewModel.StatusText = "正在编辑表情标签";
            }

            ShowOverlay(TagEditorOverlay, TagEditorSheet, TagEditorSheetScale, 0.94);
            TagInputBox.Focus();
        }

        // 刷新编辑器内的标签显示
        private void RefreshEditorUI()
        {
            CurrentTagsPanel.Children.Clear();
            TagPoolPanel.Children.Clear();

            var viewModel = (ViewModels.MainViewModel)this.DataContext;

            var selectedStyle = FindResource("ChipSelectedButtonStyle");
            var chipStyle = FindResource("ChipButtonStyle");

            // 渲染“已添加”标签（强调胶囊）
            foreach (var tag in _editingTags)
            {
                CurrentTagsPanel.Children.Add(CreateTagButton(tag, selectedStyle));
            }
            NoTagsHint.Visibility = _editingTags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 渲染“库中已有但未添加”标签（中性胶囊）
            foreach (var tag in viewModel.AllExistingTags.Except(_editingTags))
            {
                TagPoolPanel.Children.Add(CreateTagButton(tag, chipStyle));
            }
        }

        private Button CreateTagButton(string tag, object style)
        {
            var btn = new Button
            {
                Content = tag,
                Style = (Style)style,
            };
            btn.Click += (s, e) => {
                if (_editingTags.Contains(tag)) _editingTags.Remove(tag);
                else _editingTags.Add(tag);
                RefreshEditorUI();
            };
            return btn;
        }

        private void EditTag_Click(object sender, RoutedEventArgs e)
        {
            // 从右键菜单的 DataContext 中获取点击的那个 StickerModel
            if (sender is MenuItem menuItem && menuItem.DataContext is StickerModel sticker)
            {
                ShowTagEditor(sticker);
            }
        }

        private async void DeleteSticker_Click(object sender, RoutedEventArgs e)
        {
            var sticker = (sender as MenuItem)?.DataContext as StickerModel;
            if (sticker == null) return;

            Diag("delete: menu clicked, showing confirm");
            if (!await ShowAlertAsync("删除表情", "该表情会从图库和磁盘中移除，无法恢复。", "删除", destructive: true))
                return;
            Diag($"delete: confirmed, path={sticker.FullPath}");

            var viewModel = (ViewModels.MainViewModel)this.DataContext;

            try
            {
                // 1. 先从内存集合移除，UI 停止引用该图片
                viewModel.Stickers.Remove(sticker);
                Diag("delete: removed from collection, saving db");
                await viewModel.SaveDatabaseAsync();
                Diag("delete: db saved");

                // 2. （原 GC.Collect+WaitForPendingFinalizers 已删除）UI 线程等终结器会与 UIA provider
                //    的 Dispatcher 转派互等——2026-09-30 转储实证死锁三环：UI 线程 WaitForPendingFinalizers ←→
                //    finalizer SafeHandle.PInvoke ←→ UIA ElementProxy.get_ProviderOptions 的 Dispatcher.Invoke。
                //    文件占用由下方 3×200ms 重试兜底，无需 GC 护航。

                // 3. 尝试删除物理文件
                if (System.IO.File.Exists(sticker.FullPath))
                {
                    // 增加重试机制，防止系统切换延迟导致删除失败
                    bool deleted = false;
                    for (int i = 0; i < 3; i++) // 尝试 3 次
                    {
                        try
                        {
                            System.IO.File.Delete(sticker.FullPath);
                            deleted = true;
                            break;
                        }
                        catch (System.IO.IOException)
                        {
                            Diag($"delete: file locked, retry {i + 1}");
                            await Task.Delay(200); // 没删掉就等 200 毫秒再试
                        }
                    }

                    if (!deleted)
                        viewModel.StatusText = "文件被占用，已从列表移除但物理文件未删除";
                    else
                        Diag("delete: file deleted");
                }
                Diag("delete: flow complete");
            }
            catch (Exception ex)
            {
                // 💡 鲁棒性：报错不再闪退，而是记录日志或提示
                Diag("delete: EXCEPTION " + ex);
                viewModel.StatusText = $"删除异常: {ex.Message}";
            }
        }

        /// <summary>删除卡死诊断埋点（2026-09-30 用户实测删除即卡死）：逐步落盘定位卡点，稳定后可移除。</summary>
        private static void Diag(string message)
        {
            try { System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "asuka-diag.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n"); } catch { }
        }

        // 💡 提取公共的添加逻辑
        private void PerformAddTag()
        {
            string newTag = TagInputBox.Text.Trim();
            if (!string.IsNullOrEmpty(newTag) && !_editingTags.Contains(newTag))
            {
                _editingTags.Add(newTag);
                TagInputBox.Text = ""; // 自动清空
                RefreshEditorUI();     // 刷新界面上的标签云
            }
        }

        // 1. 回车事件
        private void TagInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                PerformAddTag();
                // 阻止回车键向上冒泡（防止触发窗体默认行为）
                e.Handled = true;
            }
        }

        // 2. 增加按钮点击事件
        private void AddTagButton_Click(object sender, RoutedEventArgs e)
        {
            PerformAddTag();
        }

        private void SearchBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null && !textBox.IsKeyboardFocusWithin)
            {
                // 💡 强行获取焦点
                textBox.Focus();
                // 💡 执行全选
                textBox.SelectAll();
                // 💡 关键：标记事件已处理，阻止系统后续的“光标定位”逻辑
                e.Handled = true;
            }
        }

        // Esc 一键清空搜索
        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || DataContext is not MainViewModel vm) return;
            if (vm.SearchText.Length > 0)
            {
                vm.SearchText = "";
                e.Handled = true;
            }
            else if (_searchOpen)
            {
                CloseSearch(); // 空文本按 Esc：直接收回胶囊
                e.Handled = true;
            }
        }

        // ———— 搜索胶囊 ————

        private const double SearchCapsuleWidth = 112;
        private const double SearchOpenWidth = 420;
        private bool _searchOpen;

        private void SearchCapsule_Click(object sender, MouseButtonEventArgs e)
        {
            if (_searchOpen) return; // 展开态下 TextBox 铺满胶囊，正常点不到这里；防穿透兜底
            OpenSearch();
        }

        // 点击胶囊 → 左右展开成输入框并立即聚焦；展开态无占位文字，光标从最前开始
        private void OpenSearch()
        {
            _searchOpen = true;
            SearchCapsuleContent.Visibility = Visibility.Collapsed;
            SearchBox.Visibility = Visibility.Visible;
            var target = Math.Min(SearchOpenWidth, Math.Max(240, ActualWidth - 320)); // 避让标题栏两端
            SearchHost.BeginAnimation(WidthProperty, new DoubleAnimation(SearchCapsuleWidth, target,
                TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            SearchBox.Focus();
            SearchBox.CaretIndex = 0;
        }

        private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // 输入被清空且失去焦点才收回；有文本时保持展开（结果还在展示）
            if (_searchOpen && SearchBox.Text.Length == 0) CloseSearch();
        }

        private void CloseSearch()
        {
            _searchOpen = false;
            SearchBox.Visibility = Visibility.Collapsed;
            SearchCapsuleContent.Visibility = Visibility.Visible;
            SearchHost.BeginAnimation(WidthProperty, new DoubleAnimation(SearchHost.ActualWidth,
                SearchCapsuleWidth, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }

        private async void DeleteTagAndFiles_Click(object sender, RoutedEventArgs e)
        {
            // 💡 修正 1：sender 是 MenuItem，它的 DataContext 才是我们要删除的标签字符串
            if (!(sender is MenuItem menuItem)) return;
            var tagName = menuItem.DataContext as string;

            if (string.IsNullOrEmpty(tagName) || tagName == "最近")
            {
                await ShowAlertAsync("无法删除", "「最近」是默认视图，不能删除。");
                return;
            }

            // 💡 修正 2：从当前的 Window 或直接从 DataContext 获取 ViewModel
            if (!(this.DataContext is ViewModels.MainViewModel viewModel)) return;

            // 找到所有属于该标签的表情
            var targetStickers = viewModel.Stickers.Where(s => s.Tags.Contains(tagName)).ToList();

            var confirmed = await ShowAlertAsync(
                $"删除标签「{tagName}」？",
                $"将同时从磁盘删除该标签下的 {targetStickers.Count} 张图片，此操作无法恢复。",
                "删除标签", destructive: true);

            if (confirmed)
            {
                try
                {
                    // 💡 修正 3：在循环删除前，暂时断开 UI 的过滤逻辑，避免删除过程中的 UI 闪烁
                    viewModel.StatusText = $"正在清理标签「{tagName}」...";

                    foreach (var sticker in targetStickers)
                    {
                        // 从内存集合移除
                        viewModel.Stickers.Remove(sticker);

                        // 尝试物理删除
                        if (File.Exists(sticker.FullPath))
                        {
                            try { File.Delete(sticker.FullPath); } catch { /* 忽略锁定文件 */ }
                        }
                    }

                    // 保存状态并刷新左侧栏
                    await viewModel.SaveDatabaseAsync();
                    viewModel.UpdateTabTags();
                    viewModel.SelectedTab = "最近";

                    viewModel.StatusText = $"标签「{tagName}」及其图片已成功清除";
                }
                catch (Exception ex)
                {
                    await ShowAlertAsync("操作失败", ex.Message);
                }
            }
        }

        // ———— GIF 动图预览 ————

        private const int GifPreviewHoverDelayMs = 400; // 悬浮延迟：避免快速扫过时乱弹气泡

        private Popup? _gifPreviewPopup;
        private Image? _gifPreviewImage;
        private DispatcherTimer? _gifPreviewTimer;
        private StickerModel? _hoveredSticker;
        private Border? _hoveredBorder;

        private void StickerBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is not Border border || border.DataContext is not StickerModel sticker) return;

            _hoveredSticker = sticker;
            _hoveredBorder = border;
            _gifPreviewTimer ??= CreateGifPreviewTimer();
            _gifPreviewTimer.Stop();
            _gifPreviewTimer.Start();
        }

        private void StickerBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            _gifPreviewTimer?.Stop();
            CloseGifPreview();
            // 页脚标签不走清除：覆写机制，文本常驻到下一次悬浮的标签来覆盖
        }

        private DispatcherTimer CreateGifPreviewTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GifPreviewHoverDelayMs) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                ShowGifPreview();
                ShowHoverTags();
            };
            return timer;
        }

        // 悬浮延迟后弹出气泡，按 GIF 原生尺寸播放；全局仅一个 Popup 实例，关闭即释放解码数据。
        // WPF 的边缘翻转只认屏幕边、不认窗口页脚——气泡从格底展开会盖住页脚标签，
        // 所以这里按“格底 + 气泡最大高度是否越过页脚顶”手动翻到格子上方。
        private void ShowGifPreview()
        {
            if (DataContext is MainViewModel vm && !vm.EnableGifHoverPreview) return;
            if (_hoveredSticker?.IsGif != true || !File.Exists(_hoveredSticker.FullPath)) return;

            _gifPreviewPopup ??= CreateGifPreviewPopup();

            ImageBehavior.SetAnimatedSource(_gifPreviewImage!, LoadBitmap(_hoveredSticker.FullPath));
            // WpfAnimatedGif 设置 AnimatedSource 后默认自动播放

            var transform = _hoveredBorder!.TransformToVisual(this);
            var tileBottom = transform.Transform(new Point(0, _hoveredBorder.ActualHeight)).Y;
            bool openUpward = tileBottom + 6 + GifPopupEstimatedHeight > ActualHeight - FooterHeightDip;
            _gifPreviewPopup.Placement = openUpward ? PlacementMode.Top : PlacementMode.Bottom;
            _gifPreviewPopup.VerticalOffset = openUpward ? -6 : 6;
            _gifPreviewPopup.PlacementTarget = _hoveredBorder;
            _gifPreviewPopup.IsOpen = true;
        }

        private const double GifPopupEstimatedHeight = 340; // MaxHeight 320 + 上下边距，用于翻转向判断

        private Popup CreateGifPreviewPopup()
        {
            _gifPreviewImage = new Image { MaxWidth = 320, MaxHeight = 320, Stretch = Stretch.Uniform, Margin = new Thickness(10) };

            var border = new Border
            {
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1),
                Child = _gifPreviewImage,
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.24, Color = Colors.Black },
            };
            // 弹层材质跟随主题（DynamicResource 在代码侧用 SetResourceReference）
            border.SetResourceReference(Border.BackgroundProperty, "MaterialBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "MaterialBorderBrush");

            return new Popup
            {
                Child = border,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 6,
                PopupAnimation = PopupAnimation.Fade,
                AllowsTransparency = true,
                StaysOpen = true
            };
        }

        private void CloseGifPreview()
        {
            if (_gifPreviewPopup != null) _gifPreviewPopup.IsOpen = false;
            if (_gifPreviewImage != null) ImageBehavior.SetAnimatedSource(_gifPreviewImage, null);
            _hoveredSticker = null;
            _hoveredBorder = null;
        }

        // ———— 页脚悬浮标签 ————

        private const double FooterHeightDip = 32; // 与页脚行高一致（RootGrid 第三行）

        // 与 GIF 预览同一节奏（悬浮 400ms 后出现）。
        // 覆写机制：文本常驻页脚，等下一次悬浮的标签来覆盖；悬浮到无标签表情时用空串覆盖，
        // 避免残留上一条的标签造成误导。移开鼠标不清除。
        // 页脚中部宽度有限：整条标签逐个放入，放不下的整条舍弃、以 … 结尾（不硬挤）。
        private void ShowHoverTags()
        {
            var tags = _hoveredSticker?.Tags;
            if (tags == null || tags.Count == 0) { HoverTagsText.Text = string.Empty; return; }

            double maxWidth = HoverTagsSlot.ActualWidth - 36; // 两侧留白 + 测量余量
            if (maxWidth < 24) { HoverTagsText.Text = string.Empty; return; }

            var shown = new List<string>();
            foreach (var tag in tags)
            {
                shown.Add(tag);
                if (MeasureFooterText(string.Join(" · ", shown)) > maxWidth)
                {
                    shown.RemoveAt(shown.Count - 1);
                    break;
                }
            }
            if (shown.Count == 0) { HoverTagsText.Text = string.Empty; return; }

            var text = string.Join(" · ", shown);
            PresentFooterTags(shown.Count < tags.Count ? text + " …" : text);
        }

        // 进出场动效：显示 = 淡入 + 轻微上浮（160/180ms）；被无标签表情覆盖 = 保留旧字仅淡出（120ms），
        // 避免空串闪烁。覆写切换时直接换字重新淡入。
        // 显式起点：HoverTagsText 常态就是 Opacity=1 / Y=0，不设 From 时 To 写 1/0 是 1→1、0→0，
        // 首次显示完全看不到动效（实测踩坑，与表情入场同一类错误）。
        private void PresentFooterTags(string text)
        {
            bool showing = !string.IsNullOrEmpty(text);
            if (showing) HoverTagsText.Text = text;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            HoverTagsText.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = showing ? 0 : (double?)null,
                To = showing ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(showing ? 160 : 120),
                EasingFunction = ease,
            });
            HoverTagsTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                From = showing ? 4 : (double?)null,
                To = showing ? 0 : 3,
                Duration = TimeSpan.FromMilliseconds(showing ? 180 : 120),
                EasingFunction = ease,
            });
        }

        // ———— 图库动效 ————

        // —— 图库入场（用户规则）：图片初始不可见；某列实际上屏越过 25%（列高）后该列才
        // 入场——下滚自左向右依次上浮，上滚自右向左依次下沉；筛选/切标签（视图 Reset 全新
        // 生成容器）套用同一规则，方向取当前滚向。列级联延迟 = 列位差 × 45ms。
        // 滚动本身回归原生（用户定案：无平滑滚动、无过冲、步长不定制），ScrollChanged
        // 只用于驱动方向判定与待播检查。 ——
        private enum GalleryDir { Down, Up }

        private const double TileColumnPitch = 130; // Tile 120 + Margin 10

        private const double CascadeStartDelayMs = 90; // 列级联的统一启动延迟

        private const double TileRowPitch = 130; // 方形 Tile，行距与列距相同

        // VWP 的 IScrollInfo 原生滚轮 = WheelScrollLines(3) × 一行，一格窜三行太夸张（用户实测）。
        // 只改图库：一格滚一行、即时到位无动画；侧栏保持原生。
        private void Gallery_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_galleryScroll == null || _galleryScroll.ScrollableHeight <= 0) return;
            var step = e.Delta / 120.0 * TileRowPitch;
            _galleryScroll.ScrollToVerticalOffset(_galleryScroll.VerticalOffset - step);
            e.Handled = true;
        }

        private ScrollViewer? _galleryScroll;
        private GalleryDir _galleryDir = GalleryDir.Down;
        private double _lastGalleryOffset;
        private readonly List<Border> _pendingTiles = new();

        private void GalleryScroll_Changed(object sender, ScrollChangedEventArgs e)
        {
            if (_galleryScroll == null) return;
            _galleryDir = _galleryScroll.VerticalOffset >= _lastGalleryOffset ? GalleryDir.Down : GalleryDir.Up;
            _lastGalleryOffset = _galleryScroll.VerticalOffset;
            CheckPendingEntrances();
        }

        private void StickerTile_Loaded(object sender, RoutedEventArgs e) => PendTile(sender as Border);

        private void StickerTile_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is StickerModel) PendTile(sender as Border);
        }

        // 入场前不可见并进待播队列，位置越线后由 CheckPendingEntrances 级联放出
        private void PendTile(Border? tile)
        {
            if (tile == null) return;
            // 必须先摘残留动画再写本地值：上一次入场动画 Hold 在 Opacity=1（终值驻留），
            // 动画优先级高于本地值，不清掉的话回收换绑后的新图片会直接可见、
            // “未过 25% 不应看到图片”就被击穿（用户实测踩坑）
            tile.BeginAnimation(OpacityProperty, null);
            var translate = (TranslateTransform)((TransformGroup)tile.RenderTransform).Children[1];
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
            tile.Opacity = 0;
            if (!_pendingTiles.Contains(tile)) _pendingTiles.Add(tile);
            Dispatcher.BeginInvoke(new Action(CheckPendingEntrances), DispatcherPriority.Loaded);
        }

        private void CheckPendingEntrances()
        {
            if (_galleryScroll == null || _pendingTiles.Count == 0) return;
            var viewportH = _galleryScroll.ViewportHeight;
            if (viewportH <= 0) return;

            List<Border>? remove = null;
            List<(Border Tile, int Col)>? firing = null;
            var minCol = int.MaxValue;
            var maxCol = int.MinValue;
            foreach (var tile in _pendingTiles)
            {
                if (!tile.IsLoaded || tile.DataContext is not StickerModel)
                {
                    (remove ??= new List<Border>()).Add(tile); // 已被回收/清空：出队
                    continue;
                }
                var pos = tile.TransformToVisual(_galleryScroll).Transform(new Point(0, 0));
                var overlap = Math.Min(pos.Y + tile.ActualHeight, viewportH) - Math.Max(pos.Y, 0);
                if (tile.ActualHeight <= 0 || overlap / tile.ActualHeight < 0.25) continue;

                (firing ??= new List<(Border, int)>()).Add((tile, (int)Math.Round(pos.X / TileColumnPitch)));
                var col = (int)Math.Round(pos.X / TileColumnPitch);
                if (col < minCol) minCol = col;
                if (col > maxCol) maxCol = col;
            }
            if (remove != null) foreach (var tile in remove) _pendingTiles.Remove(tile);
            if (firing == null) return;
            foreach (var (tile, _) in firing) _pendingTiles.Remove(tile);

            var down = _galleryDir == GalleryDir.Down;
            foreach (var (tile, col) in firing)
            {
                var offsetCols = down ? col - minCol : maxCol - col;
                // 启动延迟：首列 BeginTime=0 会在触发瞬间直接放完，感知不到动效（用户实测），
                // 全部列统一先押后 90ms，级联波次从 90ms 起步
                AnimateTileIn(tile, down, TimeSpan.FromMilliseconds(CascadeStartDelayMs + offsetCols * 45));
            }
        }

        private void AnimateTileIn(Border? tile, bool fromBelow, TimeSpan begin)
        {
            if (tile == null) return;
            var translate = (TranslateTransform)((TransformGroup)tile.RenderTransform).Children[1];
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            tile.Opacity = 0;
            translate.Y = fromBelow ? 12 : -12;
            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = ease,
                BeginTime = begin,
            };
            var move = new DoubleAnimation
            {
                From = fromBelow ? 12 : -12,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = ease,
                BeginTime = begin,
            };
            tile.BeginAnimation(OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.YProperty, move);
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T match) return match;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        // 用与页脚文字相同的字体/字号/抗锯齿参数精确测量宽度（FormattedText）
        private double MeasureFooterText(string text)
        {
            var typeface = new Typeface(HoverTagsText.FontFamily, HoverTagsText.FontStyle,
                HoverTagsText.FontWeight, HoverTagsText.FontStretch);
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, HoverTagsText.FontSize,
                HoverTagsText.Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return formatted.Width;
        }

        // ———— 标签编辑器预览图 ————

        // GIF 播放原生动画；其他格式用静态缩略图缓存。打开/关闭都走这里，避免编辑器隐藏后动画仍在后台解码播放。
        private void SetTagEditorPreview(StickerModel sticker)
        {
            TagEditorOverlay.DataContext = sticker;

            ClearTagEditorPreview();
            if (!File.Exists(sticker.FullPath)) return;

            if (sticker.IsGif)
            {
                // 设置 AnimatedSource 后 WpfAnimatedGif 默认自动播放
                ImageBehavior.SetAnimatedSource(TagEditorImage, LoadBitmap(sticker.FullPath));
            }
            else
            {
                TagEditorImage.Source = sticker.ImageSource;
            }
        }

        private void ClearTagEditorPreview()
        {
            ImageBehavior.SetAnimatedSource(TagEditorImage, null);
            TagEditorImage.Source = null;
        }

        private static BitmapImage LoadBitmap(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 立即读完并释放文件句柄，不锁文件
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private static BitmapImage LoadBitmapScaled(string path, int decodeWidth)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        // ———— QQ（账号）页：数据源切换与空状态 ————

        // 切到 QQ 页时网格换成该账号镜像 + QqTileTemplate；切回图库时还原
        private void UpdateGalleryMode()
        {
            if (DataContext is not MainViewModel vm) return;
            var service = vm.CurrentQqService;
            if (service != null)
            {
                StickersList.ItemTemplate = (DataTemplate)FindResource("QqTileTemplate");
                StickersList.ItemsSource = service.Mirror;
                UpdateQqEmptyState(service);
            }
            else
            {
                StickersList.ItemTemplate = (DataTemplate)FindResource("GalleryTileTemplate");
                StickersList.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.Stickers)));
                // QQ 页用本地值覆写过空状态，还原图库样式触发器接管
                EmptyState.ClearValue(VisibilityProperty);
                EmptyIcon.ClearValue(TextBlock.TextProperty);
                EmptyTitleText.ClearValue(TextBlock.TextProperty);
                EmptyHintText.ClearValue(TextBlock.TextProperty);
            }
        }

        private void UpdateQqEmptyState(QqEmojiService? service)
        {
            var count = service?.Mirror.Count ?? 0;
            EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyIcon.Text = "\uE76E";
            EmptyTitleText.Text = "该账号还没有QQ收藏表情";
            EmptyHintText.Text = "在 QQ 里右键图片选「添加到表情」，这里就会出现";
        }

        // ———— QQ 选项卡右键：重命名 / 解绑 ————

        private string? _renameUin;

        private void RenameQqTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi || mi.DataContext is not TabItemModel tab || !tab.IsQq
                || DataContext is not MainViewModel vm) return;

            var binding = vm.QqBindings.FirstOrDefault(b => "qq:" + b.Uin == tab.Value);
            if (binding == null) return;

            _renameUin = binding.Uin;
            QqRenameSubtitle.Text = $"账号 {binding.Uin} · 当前显示：{vm.QqTabLabel(binding)}";
            QqRenameBox.Text = binding.Alias;
            ShowOverlay(QqRenameOverlay, QqRenameSheet, QqRenameSheetScale, 0.94);
            QqRenameBox.Focus();
            QqRenameBox.SelectAll();
        }

        private async void SaveQqRename_Click(object sender, RoutedEventArgs e)
        {
            if (_renameUin != null && DataContext is MainViewModel vm)
            {
                // 留空 = 恢复默认（uin 尾号）
                await vm.RenameQqAccountAsync(_renameUin, QqRenameBox.Text);
            }
            CloseQqRename_Click(sender, e);
        }

        private void QqRenameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SaveQqRename_Click(sender, e);
                e.Handled = true;
            }
        }

        private void CloseQqRename_Click(object sender, RoutedEventArgs e)
        {
            _renameUin = null;
            HideOverlay(QqRenameOverlay, QqRenameSheet, QqRenameSheetScale, 0.94);
        }

        private async void UnbindQqTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi || mi.DataContext is not TabItemModel tab || !tab.IsQq
                || DataContext is not MainViewModel vm) return;

            var uin = tab.Value[3..];
            bool confirmed = await ShowAlertAsync("解除绑定",
                $"解绑后「{tab.Label}」页会移除；已导入图库的表情不受影响。",
                "解绑", destructive: true);
            if (confirmed) await vm.UnbindQqAccountAsync(uin);
        }

        // ———— QQ 格子右键：导入 / 复制到图库 / 全部导入 / 定位文件 ————

        private void ImportQq_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.DataContext is QqStickerModel sticker && DataContext is MainViewModel vm)
                vm.ImportQqStickerCommand.Execute(sticker);
        }

        // 通用「复制到自带图库中管理」：不带 QQ 标签；已在图库时提示（手动操作给反馈）
        private async void CopyQqToLibrary_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.DataContext is QqStickerModel sticker && DataContext is MainViewModel vm)
            {
                bool added = await vm.CopyQqStickerToLibraryAsync(sticker);
                if (!added)
                {
                    _ = ShowAlertAsync("已在图库", "这个表情已经导入过图库，没有重复添加。", "知道了", showCancel: false);
                }
            }
        }

        // 存量孤儿批量收编（仅在挪图库档可见）：确认 → 批量 → 结果汇报
        private async void AdoptOrphansBatch_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;

            var orphanCount = vm.QqBindings
                .Select(b => vm.CurrentQqService)
                .Count(); // 占位计数在确认弹窗里用文案表达即可；精确数在执行时统计

            bool confirmed = await ShowAlertAsync("批量移入图库",
                "把所有 QQ 页中带「已移除」角标的表情复制进图库（不带 QQ 标签），并从 QQ 页移除。\n" +
                "已在图库中的会自动跳过。QQ 的缓存文件不会被删除。",
                "开始移入");
            if (!confirmed) return;

            var (adopted, skipped) = await vm.AdoptAllOrphansAsync();
            if (adopted == 0 && skipped == 0)
            {
                _ = ShowAlertAsync("没有待处理的表情", "当前没有带「已移除」角标的表情。", "知道了", showCancel: false);
            }
            else if (adopted == 0)
            {
                _ = ShowAlertAsync("全部已在图库", $"{skipped} 个表情都已在图库中，已从 QQ 页移除。", "知道了", showCancel: false);
            }
            else
            {
                var message = skipped > 0
                    ? $"已移入 {adopted} 个表情（另 {skipped} 个已在图库，已从 QQ 页移除）。"
                    : $"已移入 {adopted} 个表情。";
                _ = ShowAlertAsync("批量移入完成", message, "知道了", showCancel: false);
            }
        }

        private void ImportAllQq_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && vm.IsQqTabActive)
                vm.ImportAllQqCommand.Execute(null);
        }

        private void RevealQq_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.DataContext is QqStickerModel sticker && File.Exists(sticker.FullPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select, \"{sticker.FullPath}\"",
                    UseShellExecute = true,
                });
            }
        }

        // 删除缓存残留（仅孤儿可见，2026-09-30 用户定案）：QQ 已取消收藏，本地缓存可删
        private async void DeleteQqOrphan_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi || mi.DataContext is not QqStickerModel sticker) return;
            if (DataContext is not MainViewModel vm) return;

            bool confirmed = await ShowAlertAsync("删除缓存残留",
                "这条表情在 QQ 中已取消收藏，本地文件属于缓存残留。\n删除后不可恢复（不进回收站），确定删除吗？",
                confirm: "删除", destructive: true);
            if (!confirmed) return;

            if (await vm.DeleteQqOrphanAsync(sticker))
            {
                vm.StatusText = "已删除缓存残留";
            }
            else
            {
                _ = ShowAlertAsync("删除失败", "文件可能正被 QQ 占用，稍后再试。", "知道了", showCancel: false);
            }
        }

        // QQ 导入完成：有新表情 → 进既有批量标签编辑器（附跳过摘要）；没有 → 说明原因
        private void OnQqImportCompleted(List<StickerModel> imported, int duplicates, int unsupported)
        {
            if (imported.Count > 0)
            {
                _pendingStickers = imported;
                _editingTags = new List<string>();
                TagInputBox.Text = "";

                SetTagEditorPreview(imported[0]);
                var summary = $"正在为 {imported.Count} 个新表情设置标签";
                var skips = new List<string>();
                if (duplicates > 0) skips.Add($"{duplicates} 张已在图库");
                if (unsupported > 0) skips.Add($"{unsupported} 张 WebP 暂不支持");
                TagEditorSubtitle.Text = skips.Count > 0 ? $"{summary}（{string.Join("，", skips)}）" : summary;

                RefreshEditorUI();
                ShowOverlay(TagEditorOverlay, TagEditorSheet, TagEditorSheetScale, 0.94);
                TagInputBox.Focus();
            }
            else if (unsupported > 0)
            {
                _ = ShowAlertAsync("暂不支持 WebP",
                    $"{unsupported} 张表情是 WebP 格式，暂无法导入图库。", "知道了", showCancel: false);
            }
            else if (duplicates > 0)
            {
                _ = ShowAlertAsync("全部已在图库",
                    "选中的表情都已导入过，没有新增。", "知道了", showCancel: false);
            }
        }

        // ———— QQ 绑定对话框（设置 → 绑定 QQ 表情） ————

        private List<QqAccountRow>? _qqBindRows;

        private async void OpenQqBindingDialog_Click(object sender, RoutedEventArgs e)
        {
            HideOverlay(SettingsOverlay, SettingsSheet, SettingsSheetScale, 0.94);
            ShowOverlay(QqBindOverlay, QqBindSheet, QqBindSheetScale, 0.94);
            await RefreshQqBindRowsAsync();
        }

        private void CloseQqBindDialog_Click(object sender, RoutedEventArgs e)
            => HideOverlay(QqBindOverlay, QqBindSheet, QqBindSheetScale, 0.94);

        private async Task RefreshQqBindRowsAsync()
        {
            if (DataContext is not MainViewModel vm) return;
            QqBindList.Children.Clear();
            QqBindSelectedButton.IsEnabled = false;

            var accounts = await QqEmojiService.ScanAccountsAsync();
            _qqBindRows = accounts
                .OrderByDescending(a => a.LastActive)
                .ThenByDescending(a => a.StickerCount)
                .Select(a => new QqAccountRow(a, vm.QqBindings.Any(b => b.Uin == a.Uin)))
                .ToList();

            QqBindEmptyHint.Visibility = _qqBindRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var row in _qqBindRows) QqBindList.Children.Add(BuildQqBindRow(row));
        }

        private FrameworkElement BuildQqBindRow(QqAccountRow row)
        {
            var outer = new Border
            {
                Background = FindResource("FieldBrush") as Brush,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 0, 0, 10),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock
            {
                Text = $"账号 {row.Uin}",
                FontSize = 13.5,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (row.IsBound)
            {
                title.Children.Add(new TextBlock
                {
                    Text = "已绑定",
                    FontSize = 10.5,
                    Foreground = FindResource("AccentBrush") as Brush,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            var meta = new TextBlock
            {
                Text = row.Meta,
                FontSize = 11.5,
                Foreground = FindResource("TextSecondaryBrush") as Brush,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            var preview = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            info.Children.Add(title);
            info.Children.Add(meta);
            info.Children.Add(preview);
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);
            FillQqPreviewStrip(preview, row.Scan);

            if (row.IsBound)
            {
                var unbind = new Button
                {
                    Style = FindResource("TextButtonStyle") as Style,
                    Content = "解绑",
                    Tag = row.Uin,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                unbind.Click += QqRowUnbind_Click;
                Grid.SetColumn(unbind, 1);
                grid.Children.Add(unbind);
            }
            else
            {
                var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                check.Checked += (_, _) => { row.IsSelected = true; UpdateQqBindButtonState(); };
                check.Unchecked += (_, _) => { row.IsSelected = false; UpdateQqBindButtonState(); };
                Grid.SetColumn(check, 1);
                grid.Children.Add(check);
            }

            outer.Child = grid;
            return outer;
        }

        // 预览条：优先 Thumb 静态缩略图（gif 没有），后台解码 8 张后回 UI 追加
        private async void FillQqPreviewStrip(StackPanel strip, QqAccountScanResult account)
        {
            try
            {
                var thumbs = await Task.Run(() =>
                {
                    var list = new List<BitmapImage>();
                    if (account.OriDir.Length == 0 || !Directory.Exists(account.OriDir)) return list;
                    foreach (var file in Directory.EnumerateFiles(account.OriDir))
                    {
                        if (list.Count >= 8) break;
                        try
                        {
                            var name = Path.GetFileNameWithoutExtension(file);
                            var thumb = Path.Combine(Path.GetDirectoryName(account.OriDir)!, "Thumb", name + ".png");
                            list.Add(LoadBitmapScaled(File.Exists(thumb) ? thumb : file, 96));
                        }
                        catch { /* 单张预览失败跳过 */ }
                    }
                    return list;
                });
                foreach (var bmp in thumbs)
                {
                    strip.Children.Add(new Image
                    {
                        Source = bmp,
                        Width = 44,
                        Height = 44,
                        Stretch = Stretch.UniformToFill,
                        Margin = new Thickness(0, 0, 6, 0),
                    });
                }
            }
            catch { /* 预览失败不影响账号行 */ }
        }

        private void UpdateQqBindButtonState()
        {
            QqBindSelectedButton.IsEnabled = _qqBindRows?.Any(r => r.IsSelected && !r.IsBound) == true;
        }

        private async void QqBindSelected_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            var uins = _qqBindRows?.Where(r => r.IsSelected && !r.IsBound).Select(r => r.Uin).ToList();
            if (uins == null || uins.Count == 0) return;
            await vm.BindQqAccountsAsync(uins);
            await RefreshQqBindRowsAsync();
        }

        private async void QqRowUnbind_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string uin || DataContext is not MainViewModel vm) return;
            await vm.UnbindQqAccountAsync(uin);
            await RefreshQqBindRowsAsync();
        }

        // ———— 深度同步密钥获取流：说明 Alert → 官方脚本（调试版 QQ 等登录）→ 回填 ————

        private async Task RunKeyAcquisitionFlowAsync()
        {
            MainViewModel? vm = DataContext as MainViewModel;
            try
            {
                if (vm is null) return;

                // 三选：立即读取（脚本） / 下次登录自动抓取（静默） / 取消
                // 文案按 2026-09-30 UX 定案：明说"额外的窗口、当前 QQ 不受影响"，并把
                // "弹窗前要分析 1-2 分钟"前置说明（实测等待黑洞被用户抓过）
                bool? startWatcherNow = null;
                bool proceed = await ShowAlertAsync("读取 QQ 收藏索引",
                    "方式一（立即）：接下来会弹出一个额外的 QQ 登录窗口，在里面登录一次你的账号即可，" +
                    "登录完成后它会自动关闭。如果此时你已经登录着 QQ，可以不用管它——正在用的 QQ 不受任何影响。\n" +
                    "注意：登录窗口弹出前需要先分析 QQ 的模块文件（约 1-2 分钟），请耐心等待。\n\n" +
                    "方式二（自动）：点「下次自动」，之后任意一次你登录 QQ 时（包括开关机后）" +
                    "自动完成读取，QQ 完全无感；期间保持飞鸟在托盘运行即可。\n\n" +
                    "密钥只保存在本机，仅用于读取收藏索引。",
                    confirm: "立即读取",
                    neutral: "下次自动",
                    onNeutral: () => startWatcherNow = true);
                if (!proceed && startWatcherNow != true)
                {
                    vm.CompleteKeyAcquisition(null);
                    vm.QqDeepSyncEnabled = false;
                    return;
                }
                if (startWatcherNow == true)
                {
                    vm.StartKeyWatcher(); // 静默等待下次 QQ 登录，抓到后自动回填
                    return;
                }

                // 等待黑洞补丁：脚本输出直落盘 + 每秒轮询，密钥一出现立即回填；
                // onProgress 按脚本输出的阶段特征回报当前进度（检测/分析/拉起/登录/解析）
                vm.SetKeyFlowStatus("正在启动 QQ 登录窗口：先分析 QQ 模块（约 1-2 分钟），弹出后请在其中登录…");
                string? key;
                try
                {
                    key = await Task.Run(() =>
                        Services.QqKeyExtractor.RunScriptAndExtractAsync(msg =>
                        {
                            _ = Dispatcher.InvokeAsync(() => vm.SetKeyFlowStatus(msg));
                        }).GetAwaiter().GetResult());
                }
                finally
                {
                    vm.SetKeyFlowStatus(null); // 完成或失败都撤掉瞬态提示（CompleteKeyAcquisition 也会清，双保险）
                }
                vm.CompleteKeyAcquisition(key);
                if (key == null)
                {
                    _ = ShowAlertAsync("未能获取密钥",
                        "没有读到密钥（可能超时未登录，或 QQ 版本结构变化）。\n" +
                        "可选择「下次自动」等下次登录时静默完成，或稍后重试。" +
                        "详细日志见 %TEMP%\\asuka-keyflow.log。",
                        "知道了", showCancel: false);
                    vm.QqDeepSyncEnabled = false;
                }
                else
                {
                    vm.StatusText = "QQ 收藏索引密钥读取成功";
                }
            }
            catch (Exception ex)
            {
                if (vm is not null)
                {
                    vm.CompleteKeyAcquisition(null); // 撤瞬态状态
                    _ = ShowAlertAsync("密钥读取异常", ex.Message, "知道了", showCancel: false);
                }
            }
        }

        // ———— 启动通知：QQ 绑定询问 → WebP 环境警告（串行，避免 Alert 叠加） ————

        private async Task RunStartupNoticesAsync()
        {
            await TryPromptQqBindingAsync();
            await TryWarnWebpSupportAsync();
        }

        // WebP 环境警告：解不动 WebP 时一次性提醒（可直达商店安装页），处理过就永久记住。
        // QQ 收藏同步不受影响（QQ 收藏管线不用 WebP），只有拖放导入 WebP 受限。
        private async Task TryWarnWebpSupportAsync()
        {
            try
            {
                if (DataContext is not MainViewModel vm) return;
                if (WebpSupportProbe.IsSupported) return;
                if (vm.WebpNoticeDismissed) return;

                bool openStore = await ShowAlertAsync("WebP 支持不完整",
                    "本机未安装微软官方的「WebP 图像扩展」（免费），WebP 图片暂时无法导入图库。\n\n" +
                    "QQ 收藏同步不受影响。安装该扩展后，拖入的 WebP 也会自动转成 PNG 保存。",
                    confirm: "打开安装页", showCancel: true);

                await vm.DismissWebpNoticeAsync();
                if (openStore) WebpSupportProbe.OpenStorePage();
            }
            catch { /* 提醒失败不影响主流程 */ }
        }

        // 设置 → 资源库 →「WebP 图片支持」：手动重新探测（启动警告被永久记住后，这里是唯一入口）
        private void RecheckWebp_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm) vm.RecheckWebpSupport();
        }

        private void OpenWebpStore_Click(object sender, RoutedEventArgs e)
            => WebpSupportProbe.OpenStorePage();

        // ———— 启动询问（检测 → 猜测 → 询问，绝不擅自绑定） ————

        private async Task TryPromptQqBindingAsync()
        {
            try
            {
                if (DataContext is not MainViewModel vm) return;
                var candidate = await vm.GetStartupPromptCandidateAsync();
                if (candidate == null) return;

                var active = QqAccountRow.FormatLastActive(candidate.LastActive);
                bool bind = await ShowAlertAsync("绑定 QQ 表情",
                    $"检测到 QQ 账号 {candidate.Uin}\n{candidate.StickerCount} 个收藏表情 · 最近活跃：{active}\n\n要把它绑定到图库吗？绑定后会出现「QQ（账号）」页，可直接发送与导入。",
                    confirm: "绑定",
                    neutral: "不再询问",
                    onNeutral: () => _ = vm.DismissQqPromptAsync(candidate.Uin));
                if (bind) await vm.BindQqAccountsAsync(new[] { candidate.Uin });
            }
            catch { /* 询问失败不影响主流程 */ }
        }
    }
}
