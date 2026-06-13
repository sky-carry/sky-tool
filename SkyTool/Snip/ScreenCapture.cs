using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SkyTool.Common;

namespace SkyTool.Snip;

public static class ScreenCapture
{
    public record Shot(BitmapSource Bitmap, int X, int Y, int Width, int Height);

    /// <summary>抓取整个虚拟屏幕（所有显示器），坐标为物理像素。</summary>
    public static Shot CaptureVirtualScreen()
    {
        int x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int w = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int h = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));

        IntPtr hBitmap = bmp.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return new Shot(source, x, y, w, h);
        }
        finally
        {
            Native.DeleteObject(hBitmap);
        }
    }

    // 桌面 / 壳层窗口：吸附到它们等于吸附整屏，没有意义，直接排除
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Progman",                 // 桌面（Program Manager）
        "WorkerW",                 // 桌面壁纸层
        "Shell_TrayWnd",           // 主任务栏
        "Shell_SecondaryTrayWnd",  // 副屏任务栏
        "SysListView32",           // 桌面图标
        "Windows.UI.Core.CoreWindow",
        "Progman.Tray",
    };

    /// <summary>按 Z 序（最上层在前）快照当前所有可见窗口的真实边界（物理像素），
    /// 截图时用来做“悬停自动吸附窗口边框”。</summary>
    public static List<Int32Rect> SnapshotWindowRects()
    {
        var list = new List<Int32Rect>();
        var sb = new System.Text.StringBuilder(256);
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;

            // 鼠标穿透的覆盖层（动态壁纸 / 桌面挂件 / 透明置顶层）：系统不会把点击给它，
            // 但它常常近乎全屏且置顶，会把所有悬停命中“吃掉”导致只能选到整屏，必须排除。
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TRANSPARENT) != 0) return true;

            // UWP 挂起等“隐身”窗口
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                && cloaked != 0) return true;

            // 排除桌面 / 任务栏等壳层窗口
            sb.Clear();
            if (Native.GetClassName(hwnd, sb, sb.Capacity) > 0 && ExcludedClasses.Contains(sb.ToString()))
                return true;

            // 优先取 DWM 真实边界（不含 Win10/11 的隐形拖拽边框和阴影）
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r,
                    System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) != 0)
                Native.GetWindowRect(hwnd, out r);

            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 24 || h < 24) return true; // 忽略 tooltip 等小杂窗

            list.Add(new Int32Rect(r.Left, r.Top, w, h));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
