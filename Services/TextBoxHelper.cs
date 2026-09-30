using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace OICQStickerManager.Services;

/// <summary>
/// 输入框辅助：把一个按钮挂接为"清空文本"按钮（按钮的 Tag 需指向目标 TextBox）。
/// </summary>
public static class TextBoxHelper
{
    public static readonly DependencyProperty ClearOnClickProperty =
        DependencyProperty.RegisterAttached(
            "ClearOnClick", typeof(bool), typeof(TextBoxHelper),
            new PropertyMetadata(false, OnClearOnClickChanged));

    public static bool GetClearOnClick(DependencyObject obj) => (bool)obj.GetValue(ClearOnClickProperty);
    public static void SetClearOnClick(DependencyObject obj, bool value) => obj.SetValue(ClearOnClickProperty, value);

    private static void OnClearOnClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        if ((bool)e.NewValue) button.Click += ClearOnClick;
        else button.Click -= ClearOnClick;
    }

    private static void ClearOnClick(object sender, RoutedEventArgs e)
    {
        if (sender is ButtonBase { Tag: TextBox target })
        {
            target.Clear();
            target.Focus();
            target.CaretIndex = target.Text.Length;
        }
    }
}
