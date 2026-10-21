using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexUsage.DailyPanel;

internal readonly record struct TrayPopupAnchor(Point Cursor, Rectangle WorkingArea, uint Dpi);

/// <summary>Native monitor capture and movement; WPF retains ownership of layout and visibility.</summary>
internal static class TrayPopupPlacement
{
    internal static TrayPopupAnchor CaptureAnchor() => CaptureAnchor(System.Windows.Forms.Cursor.Position);

    internal static TrayPopupAnchor CaptureAnchor(Point physicalCursor)
    {
        var workingArea = Screen.FromPoint(physicalCursor).WorkingArea;
        var monitor = MonitorFromPoint(new NativePoint(physicalCursor.X, physicalCursor.Y), 2 /* nearest */);
        var dpi = 96u;
        // Windows 8.1+ supports per-monitor DPI. The application's PerMonitorV2 manifest
        // makes Cursor.Position, Screen.WorkingArea and SetWindowPos use physical pixels.
        try
        {
            if (GetDpiForMonitor(monitor, 0 /* effective DPI */, out var monitorDpi, out _) == 0 && monitorDpi > 0)
                dpi = monitorDpi;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return new TrayPopupAnchor(physicalCursor, workingArea, dpi);
    }

    internal static void MoveWindow(IntPtr hwnd, Rectangle physicalBounds)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("A created window handle is required.", nameof(hwnd));
        if (!SetWindowPos(hwnd, IntPtr.Zero, physicalBounds.X, physicalBounds.Y, physicalBounds.Width,
            physicalBounds.Height, 0x0004 /* no z-order */ | 0x0010 /* no activate */))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
