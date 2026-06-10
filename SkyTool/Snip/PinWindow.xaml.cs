using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SkyTool.Snip;

/// <summary>贴图窗口：把截图钉在屏幕最前，可拖动、滚轮缩放、Esc/中键关闭。</summary>
public partial class PinWindow : Window
{
    private readonly BitmapSource _image;
    private double _scale = 1.0;
    private readonly double _baseW, _baseH;

    public PinWindow(BitmapSource image, int physX, int physY)
    {
        InitializeComponent();
        _image = image;
        Img.Source = image;

        // 按物理像素 1:1 显示（换算成 DIP）
        var dpi = VisualTreeHelper.GetDpi(this);
        _baseW = image.PixelWidth / dpi.DpiScaleX;
        _baseH = image.PixelHeight / dpi.DpiScaleY;
        Img.Width = _baseW;
        Img.Height = _baseH;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            // 抵消外层 12 DIP 的阴影边距，让图像正好盖在原选区位置
            var d = VisualTreeHelper.GetDpi(this);
            SkyTool.Common.Native.SetWindowPos(hwnd, IntPtr.Zero,
                physX - (int)Math.Round(12 * d.DpiScaleX), physY - (int)Math.Round(12 * d.DpiScaleY), 0, 0,
                SkyTool.Common.Native.SWP_NOSIZE | 0x0004 /*SWP_NOZORDER*/);
        };

        var accent = new SolidColorBrush(Color.FromRgb(0x7A, 0xA2, 0xF7));
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        MouseWheel += OnWheel;
        MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) Close(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseEnter += (_, _) => Frame.BorderBrush = accent;
        MouseLeave += (_, _) => Frame.BorderBrush = Brushes.Transparent;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.1, 8.0);
        Img.Width = _baseW * _scale;
        Img.Height = _baseH * _scale;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetImage(_image);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 图片|*.png",
            FileName = $"贴图_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dlg.ShowDialog() == true)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_image));
            using var fs = File.Create(dlg.FileName);
            encoder.Save(fs);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
