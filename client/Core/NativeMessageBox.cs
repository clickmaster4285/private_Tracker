using System.Runtime.InteropServices;

namespace client.Core;

/// <summary>Minimal Win32 message box for early-startup messages (before any UI framework).</summary>
public static class NativeMessageBox
{
    private const uint MB_OK = 0x0;
    private const uint MB_ICONINFORMATION = 0x40;
    private const uint MB_TOPMOST = 0x40000;
    private const uint MB_SETFOREGROUND = 0x10000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void ShowInfo(string caption, string text)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONINFORMATION | MB_TOPMOST | MB_SETFOREGROUND); }
        catch { /* best-effort */ }
    }
}
