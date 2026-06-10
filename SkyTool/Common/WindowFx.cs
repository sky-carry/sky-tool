using System.Windows;
using System.Windows.Interop;

namespace SkyTool.Common;

public static class WindowFx
{
    /// <summary>Win11 圆角 + 深色边框。对 WindowStyle=None 的窗口生效。</summary>
    public static void Modernize(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        int round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int dark = 1;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }
}
