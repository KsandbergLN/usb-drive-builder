using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace LaptopQaUsbBuilder;

internal static class BorderlessWindowResizer
{
    private const int WmNcHitTest = 0x0084;
    private const int WmSystemCommand = 0x0112;
    private const int ScSize = 0xF000;
    private const int GwlStyle = -16;
    private const long WsThickFrame = 0x00040000L;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const double GripDip = 14;

    public static void Attach(Window window)
    {
        HwndSource? source = null;
        HwndSourceHook? hook = null;
        window.ResizeMode = ResizeMode.CanResize;
        window.PreviewMouseMove += (_, e) =>
        {
            var hit = GetWpfHit(window, e.GetPosition(window));
            window.ForceCursor = hit != 0;
            window.Cursor = CursorFor(hit);
        };
        window.MouseLeave += (_, _) =>
        {
            window.ForceCursor = false;
            window.Cursor = null;
        };
        window.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || e.ClickCount != 1) return;
            var hit = GetWpfHit(window, e.GetPosition(window));
            if (hit == 0) return;
            e.Handled = true;
            window.ForceCursor = true;
            window.Cursor = CursorFor(hit);
            ReleaseCapture();
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
                SendMessage(hwnd, WmSystemCommand, new IntPtr(ScSize + SizeDirection(hit)), IntPtr.Zero);
            window.ForceCursor = false;
            window.Cursor = null;
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style | WsThickFrame));
            source = HwndSource.FromHwnd(hwnd);
            hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                HitTest(window, hwnd, message, lParam, ref handled);
            source?.AddHook(hook);
        };
        window.Closed += (_, _) =>
        {
            if (source is not null && hook is not null) source.RemoveHook(hook);
        };
    }

    private static int GetWpfHit(Window window, Point point)
    {
        if (window.WindowState != WindowState.Normal ||
            window.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize)
            return 0;
        var left = point.X >= 0 && point.X <= GripDip;
        var right = point.X <= window.ActualWidth && point.X >= window.ActualWidth - GripDip;
        var top = point.Y >= 0 && point.Y <= GripDip;
        var bottom = point.Y <= window.ActualHeight && point.Y >= window.ActualHeight - GripDip;
        return top && left ? HtTopLeft :
            top && right ? HtTopRight :
            bottom && left ? HtBottomLeft :
            bottom && right ? HtBottomRight :
            left ? HtLeft : right ? HtRight : top ? HtTop : bottom ? HtBottom : 0;
    }

    private static Cursor? CursorFor(int hit) => hit switch
    {
        HtTopLeft or HtBottomRight => Cursors.SizeNWSE,
        HtTopRight or HtBottomLeft => Cursors.SizeNESW,
        HtLeft or HtRight => Cursors.SizeWE,
        HtTop or HtBottom => Cursors.SizeNS,
        _ => null
    };

    private static int SizeDirection(int hit) => hit switch
    {
        HtLeft => 1,
        HtRight => 2,
        HtTop => 3,
        HtTopLeft => 4,
        HtTopRight => 5,
        HtBottom => 6,
        HtBottomLeft => 7,
        HtBottomRight => 8,
        _ => 0
    };

    private static IntPtr HitTest(Window window, IntPtr hwnd, int message, IntPtr lParam, ref bool handled)
    {
        if (message != WmNcHitTest || window.WindowState != WindowState.Normal ||
            window.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize)
            return IntPtr.Zero;
        if (!GetWindowRect(hwnd, out var rect)) return IntPtr.Zero;

        var pointValue = lParam.ToInt64();
        var x = unchecked((short)(pointValue & 0xffff));
        var y = unchecked((short)((pointValue >> 16) & 0xffff));
        var dpi = GetDpiForWindow(hwnd);
        var grip = Math.Max(5, (int)Math.Ceiling(GripDip * (dpi == 0 ? 1 : dpi / 96d)));
        var left = x >= rect.Left && x < rect.Left + grip;
        var right = x < rect.Right && x >= rect.Right - grip;
        var top = y >= rect.Top && y < rect.Top + grip;
        var bottom = y < rect.Bottom && y >= rect.Bottom - grip;

        var result = top && left ? HtTopLeft :
            top && right ? HtTopRight :
            bottom && left ? HtBottomLeft :
            bottom && right ? HtBottomRight :
            left ? HtLeft : right ? HtRight : top ? HtTop : bottom ? HtBottom : 0;
        if (result == 0) return IntPtr.Zero;
        handled = true;
        return new IntPtr(result);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
