using System.Runtime.InteropServices;
using GameTranslate.Models;

namespace GameTranslate.Native;

internal static class Win32
{
    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_MONITOR = 0x00000001;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int SRCCOPY = 0x00CC0020;
    public const uint GA_ROOT = 2;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(
        IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    public static bool SupportsExcludeFromCapture => Environment.OSVersion.Version.Build >= 19041;

    public static void SetOverlayWindowStyles(IntPtr hwnd, bool clickThrough, bool noActivate)
    {
        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;

        if (clickThrough)
            exStyle |= WS_EX_TRANSPARENT;
        else
            exStyle &= ~WS_EX_TRANSPARENT;

        if (noActivate)
            exStyle |= WS_EX_NOACTIVATE;
        else
            exStyle &= ~WS_EX_NOACTIVATE;

        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
    }

    public static bool TrySetExcludeFromCapture(IntPtr hwnd, bool exclude)
    {
        if (!SupportsExcludeFromCapture)
            return false;

        return SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    public static void EnsureTopmostNoActivate(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static (int X, int Y, int Width, int Height) GetVirtualScreenBounds()
    {
        return (
            GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN),
            GetSystemMetrics(SM_CYVIRTUALSCREEN)
        );
    }

    public static (int Width, int Height) GetPrimaryScreenSize()
    {
        return (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
    }

    /// <summary>
    /// Gets the physical screen client area (excluding Windows titlebar and borders)
    /// of the top-level window located at the given screen coordinate.
    /// </summary>
    public static ScreenRect? GetWindowClientRectAtPoint(int screenX, int screenY)
    {
        IntPtr hwnd = WindowFromPoint(new POINT { X = screenX, Y = screenY });
        if (hwnd == IntPtr.Zero)
            return null;

        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        if (root != IntPtr.Zero)
            hwnd = root;

        if (!GetClientRect(hwnd, out RECT client))
            return null;

        int cw = client.Right - client.Left;
        int ch = client.Bottom - client.Top;
        if (cw < 40 || ch < 40)
            return null;

        var topLeft = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref topLeft))
            return null;

        return new ScreenRect { X = topLeft.X, Y = topLeft.Y, Width = cw, Height = ch };
    }

    /// <summary>
    /// If the user's dragged rectangle accidentally includes a window's top titlebar (e.g. "CONTACT"),
    /// automatically trims the top edge down to the window's client area so the titlebar is never OCR'd.
    /// </summary>
    public static ScreenRect TrimWindowTitleBarIfOverlapping(ScreenRect selected)
    {
        if (!selected.IsValid)
            return selected;

        int probeX = selected.X + selected.Width / 2;
        int probeY = selected.Y + Math.Min(selected.Height / 2, 120);

        IntPtr hwnd = WindowFromPoint(new POINT { X = probeX, Y = probeY });
        if (hwnd == IntPtr.Zero)
            return selected;

        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        if (root != IntPtr.Zero)
            hwnd = root;

        if (!GetWindowRect(hwnd, out RECT winRect) || !GetClientRect(hwnd, out RECT clientRect))
            return selected;

        var clientOrigin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref clientOrigin))
            return selected;

        int titleBarHeight = clientOrigin.Y - winRect.Top;
        // Standard Windows titlebar is ~24-56px tall
        if (titleBarHeight >= 18 && titleBarHeight <= 70)
        {
            int selectedBottom = selected.Y + selected.Height;
            // If selection starts inside (or just above) the titlebar and extends well into the client area
            if (selected.Y >= winRect.Top - 12 &&
                selected.Y < clientOrigin.Y &&
                selectedBottom > clientOrigin.Y + 40)
            {
                int trimY = clientOrigin.Y + 2;
                int newHeight = selectedBottom - trimY;
                if (newHeight >= 30)
                {
                    return new ScreenRect
                    {
                        X = selected.X,
                        Y = trimY,
                        Width = selected.Width,
                        Height = newHeight
                    };
                }
            }
        }

        return selected;
    }

    /// <summary>
    /// Computes the cinema-style bottom-of-screen subtitle strip on the monitor containing the reference rect.
    /// Uses a generous transparent height (42% of work area, anchored bottom) so even long 4-5 line
    /// RPG paragraphs grow cleanly upward from the bottom edge without ever clipping.
    /// </summary>
    public static ScreenRect GetBottomOfMonitorSubtitleRect(ScreenRect referenceRegion)
    {
        var center = new POINT
        {
            X = referenceRegion.IsValid ? referenceRegion.X + referenceRegion.Width / 2 : 200,
            Y = referenceRegion.IsValid ? referenceRegion.Y + referenceRegion.Height / 2 : 200
        };

        IntPtr hMon = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hMon != IntPtr.Zero && GetMonitorInfo(hMon, ref info))
        {
            int monX = info.rcWork.Left;
            int monW = info.rcWork.Right - info.rcWork.Left;
            int monH = info.rcWork.Bottom - info.rcWork.Top;

            int subW = (int)(monW * 0.90);
            int subH = Math.Max(260, (int)(monH * 0.42));
            int subX = monX + (monW - subW) / 2;
            int subY = info.rcWork.Bottom - subH - 12;
            return new ScreenRect { X = subX, Y = subY, Width = subW, Height = subH };
        }

        var (pw, ph) = GetPrimaryScreenSize();
        return AppSettings.CreateDefaultBottomSubtitleRect(0, 0, pw > 0 ? pw : 1920, ph > 0 ? ph : 1080);
    }

    /// <summary>
    /// Computes a subtitle strip pinned cleanly to the bottom edge of the captured region / game window.
    /// </summary>
    public static ScreenRect GetBottomOfRegionSubtitleRect(ScreenRect captureRegion)
    {
        if (!captureRegion.IsValid)
            return GetBottomOfMonitorSubtitleRect(captureRegion);

        int subW = Math.Max(520, (int)(captureRegion.Width * 0.96));
        int subH = Math.Clamp((int)(captureRegion.Height * 0.42), 200, 420);
        int subX = captureRegion.X + (captureRegion.Width - subW) / 2;
        int subY = captureRegion.Y + captureRegion.Height - subH - 10;
        return new ScreenRect { X = subX, Y = subY, Width = subW, Height = subH };
    }
}
