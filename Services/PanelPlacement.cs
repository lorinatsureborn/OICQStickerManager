using System.Windows;

namespace OICQStickerManager.Services;

internal static class PanelPlacement
{
    internal static Rect Place(Rect workArea, Size sizeDip, Point anchorPixels, double scale, bool coexist, double anchorWidthPixels = 0)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0) return Rect.Empty;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        double margin = Math.Min(8 * scale, Math.Min(workArea.Width, workArea.Height) / 4);
        double width = Math.Min(sizeDip.Width * scale, workArea.Width - 2 * margin);
        double height = Math.Min(sizeDip.Height * scale, workArea.Height - 2 * margin);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return Rect.Empty;
        double left = coexist ? anchorPixels.X - width - margin : anchorPixels.X - 30 * scale;
        double top = coexist ? anchorPixels.Y : anchorPixels.Y - 30 * scale;
        if (coexist && left < workArea.Left + margin)
        {
            double rightStart = anchorPixels.X + anchorWidthPixels + margin;
            double leftSpace = anchorPixels.X - margin - (workArea.Left + margin);
            double rightSpace = workArea.Right - margin - rightStart;
            // Keep the native panel clear when neither side fits the preferred width.
            if (rightSpace >= width)
                left = rightStart;
            else if (rightSpace > leftSpace && rightSpace > 0)
            {
                width = rightSpace;
                left = rightStart;
            }
            else if (leftSpace > 0)
            {
                width = leftSpace;
                left = anchorPixels.X - width - margin;
            }
            else
                left = rightStart;
        }
        left = Math.Clamp(left, workArea.Left + margin, workArea.Right - width - margin);
        top = Math.Clamp(top, workArea.Top + margin, workArea.Bottom - height - margin);
        return new Rect(Math.Round(left), Math.Round(top), Math.Floor(width), Math.Floor(height));
    }
}
