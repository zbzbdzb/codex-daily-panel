using System;
using System.Drawing;

namespace CodexUsage.DailyPanel;

internal static class TrayPopupGeometry
{
    /// <summary>All coordinates and dimensions are physical pixels, including on monitors with negative origins.</summary>
    internal static Rectangle Place(Point anchor, Rectangle workingArea, Size popupPhysicalSize, int gap = 8)
    {
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workingArea));
        if (popupPhysicalSize.Width <= 0 || popupPhysicalSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(popupPhysicalSize));
        ArgumentOutOfRangeException.ThrowIfNegative(gap);

        var width = Math.Min(popupPhysicalSize.Width, workingArea.Width);
        var height = Math.Min(popupPhysicalSize.Height, workingArea.Height);
        // Preserve the gap when there is room; tiny work areas still contain the entire popup.
        var horizontalGap = Math.Min(gap, (workingArea.Width - width) / 2);
        var verticalGap = Math.Min(gap, (workingArea.Height - height) / 2);
        var minX = workingArea.Left + horizontalGap;
        var maxX = workingArea.Right - horizontalGap - width;
        var minY = workingArea.Top + verticalGap;
        var maxY = workingArea.Bottom - verticalGap - height;
        var x = Math.Clamp(anchor.X - width / 2, minX, maxX);
        var y = Math.Clamp(anchor.Y - height / 2, minY, maxY);

        // The taskbar is outside WorkingArea. The nearest work edge also handles auto-hide,
        // where the click can be just inside it, without relying on a private NotifyIcon field.
        var edge = 0; // bottom, top, left, right (bottom wins ties at tray corners)
        var distance = Math.Abs((long)anchor.Y - workingArea.Bottom);
        var topDistance = Math.Abs((long)anchor.Y - workingArea.Top);
        var leftDistance = Math.Abs((long)anchor.X - workingArea.Left);
        var rightDistance = Math.Abs((long)anchor.X - workingArea.Right);
        if (topDistance < distance) { edge = 1; distance = topDistance; }
        if (leftDistance < distance) { edge = 2; distance = leftDistance; }
        if (rightDistance < distance) edge = 3;

        switch (edge)
        {
            case 0: y = maxY; break;
            case 1: y = minY; break;
            case 2: x = minX; break;
            case 3: x = maxX; break;
        }
        return new Rectangle(x, y, width, height);
    }

    internal static Size ScaleFromDip(double width, double height, uint dpi)
    {
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (dpi == 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        return new Size(checked((int)Math.Ceiling(width * dpi / 96d)), checked((int)Math.Ceiling(height * dpi / 96d)));
    }
}
