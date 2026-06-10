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
}
