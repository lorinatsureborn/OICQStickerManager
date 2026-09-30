using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OICQStickerManager.Services;

/// <summary>true → Collapsed / false → Visible（QQ 页右键菜单项与普通标签菜单项互斥显示用）。</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
