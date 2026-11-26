using System.Windows;
using System.Windows.Interop;
using GameTranslate.Native;

namespace GameTranslate.Services;

public sealed class GlobalHotkeyManager : IDisposable
{
    public const int HotkeyToggleTranslate = 9001; // Alt + T
    public const int HotkeyToggleOverlay = 9002;   // Alt + H
    public const int HotkeySelectRegion = 9003;    // Alt + R
    public const int HotkeyForceTranslate = 9004;  // Alt + O

    private IntPtr _hwnd;
    private HwndSource? _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private bool _disposed;

    public void Attach(Window hostWindow)
    {
        var helper = new WindowInteropHelper(hostWindow);
        _hwnd = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    public bool Register(int id, uint modifiers, uint virtualKey, Action callback)
    {
        if (_hwnd == IntPtr.Zero)
            return false;

        Win32.UnregisterHotKey(_hwnd, id);
        bool ok = Win32.RegisterHotKey(_hwnd, id, modifiers | Win32.MOD_NOREPEAT, virtualKey);
        if (ok)
        {
            _callbacks[id] = callback;
        }
        return ok;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out Action? action))
            {
                handled = true;
                action.Invoke();
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            foreach (int id in _callbacks.Keys)
            {
                Win32.UnregisterHotKey(_hwnd, id);
            }
        }

        _callbacks.Clear();
        _source?.RemoveHook(WndProc);
    }
}
