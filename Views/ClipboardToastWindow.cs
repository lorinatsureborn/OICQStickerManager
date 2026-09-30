using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace OICQStickerManager.Views;

/// <summary>
/// 剪贴板捕获轻提示（M2）/通用迷你 toast：非激活窗口（不抢聊天焦点）、右下角贴托盘、
/// 8 秒自动淡出。纯代码构建无 XAML；素材经 SetResourceReference 跟随主题。
/// </summary>
public class ClipboardToastWindow : Window
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    /// <summary>用户点了「入库」。</summary>
    public event Action? ImportClicked;

    /// <summary>超时/✕ 关闭（未入库）。</summary>
    public event Action? Dismissed;

    private const int AutoDismissMs = 8000;

    public ClipboardToastWindow(ImageSource thumbnail, string title)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        var thumb = new Image
        {
            Width = 40,
            Height = 40,
            Source = thumbnail,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var importButton = new Button
        {
            Content = "入库",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            MinWidth = 64,
            Height = 30,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        importButton.Click += (_, _) => Finish(ImportClicked);

        var closeButton = new Button
        {
            Content = "\uE711",
            FontFamily = (FontFamily)Application.Current.Resources["IconFontFamily"],
            FontSize = 10,
            Style = (Style)Application.Current.Resources["IconButtonStyle"],
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "忽略",
        };
        closeButton.Click += (_, _) => Finish(Dismissed);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(thumb);
        row.Children.Add(label);
        row.Children.Add(importButton);
        row.Children.Add(closeButton);

        var border = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 10, 10),
            Child = row,
        };
        border.SetResourceReference(Border.BackgroundProperty, "MaterialBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "MaterialBorderBrush");

        Content = border;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 非激活：不抢聊天窗口焦点（与快捷面板同一套互斥样式）
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    // 定位放渲染后：SizeToContent 的 ActualWidth/Height 此时才有值；底边贴工作区上方（避让任务栏）
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth - 16;
        Top = work.Bottom - ActualHeight - 16;
    }

    private DispatcherTimer? _autoDismiss;

    public new void Show()
    {
        base.Show();
        // 8 秒不处理即自动关闭（视为忽略）
        _autoDismiss = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoDismissMs) };
        _autoDismiss.Tick += (_, _) => Finish(Dismissed);
        _autoDismiss.Start();
    }

    private bool _finished;

    private void Finish(Action? raise)
    {
        if (_finished) return;
        _finished = true;
        _autoDismiss?.Stop();
        raise?.Invoke();
        Close();
    }
}
