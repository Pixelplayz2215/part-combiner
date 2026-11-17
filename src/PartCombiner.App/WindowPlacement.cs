using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PartCombiner;

/// <summary>Saved window position: the normal (restored) bounds in device pixels, plus whether it was maximized.</summary>
public sealed class SavedPlacement
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Saves and restores the window with Get/SetWindowPlacement. Windows moves a restored window back on-screen by
/// itself if the monitor it was on is gone, and the bounds stay correct across monitors with different DPI.
/// </summary>
public static class WindowPlacement
{
    public static SavedPlacement? Read(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (hwnd == IntPtr.Zero || !GetWindowPlacement(hwnd, ref wp))
            return null;

        var r = wp.rcNormalPosition;
        if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0)
            return null;

        bool maximized = wp.showCmd == SW_SHOWMAXIMIZED ||
                         (wp.showCmd == SW_SHOWMINIMIZED && (wp.flags & WPF_RESTORETOMAXIMIZED) != 0);
        return new SavedPlacement { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom, Maximized = maximized };
    }

    /// <summary>Call from SourceInitialized, before the window is first shown. Never restores minimized.</summary>
    public static void Apply(Window window, SavedPlacement saved)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || saved.Right - saved.Left <= 0 || saved.Bottom - saved.Top <= 0)
            return;

        var wp = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            showCmd = saved.Maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL,
            ptMinPosition = new POINT { X = -1, Y = -1 },
            ptMaxPosition = new POINT { X = -1, Y = -1 },
            rcNormalPosition = new RECT { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom },
        };
        SetWindowPlacement(hwnd, ref wp);
    }

    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int WPF_RESTORETOMAXIMIZED = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
}
