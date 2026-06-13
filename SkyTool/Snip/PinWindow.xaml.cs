using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkyTool.Common;

namespace SkyTool.Snip;

/// <summary>贴图窗口：把截图钉在屏幕最前。可拖动、滚轮缩放、四角拖拽改大小（保持比例）、双击/中键/Esc 关闭。</summary>
public partial class PinWindow : Window
{
    private const double MinScale = 0.08, MaxScale = 8.0;

    private readonly BitmapSource _image;
    private double _scale = 1.0;
    private readonly double _baseW, _baseH;

    // 拖拽缩放状态
    private bool _resizing;
    private double _anchorX, _anchorY;   // 拖拽时保持不动的对角（屏幕 DIP 坐标）
    private bool _leftFixed, _topFixed;

    // 拖动窗口状态（手动实现，不用 DragMove 的模态循环，以免吃掉双击）
    private bool _moving;
    private Point _moveStartCursor;
    private double _moveStartLeft, _moveStartTop;

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
            Native.SetWindowPos(hwnd, IntPtr.Zero,
                physX - (int)Math.Round(12 * d.DpiScaleX), physY - (int)Math.Round(12 * d.DpiScaleY), 0, 0,
                Native.SWP_NOSIZE | 0x0004 /*SWP_NOZORDER*/);
        };

        var accent = new SolidColorBrush(Color.FromRgb(0x7A, 0xA2, 0xF7));
        MouseLeftButtonDown += OnBodyLeftDown;
        MouseMove += OnBodyMove;
        MouseLeftButtonUp += OnBodyUp;
        MouseWheel += OnWheel;
        MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) Close(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseEnter += (_, _) => { Frame.BorderBrush = accent; ShowGrips(true); };
        MouseLeave += (_, _) => { if (!_resizing) { Frame.BorderBrush = Brushes.Transparent; ShowGrips(false); } };
    }

    private void ShowGrips(bool show)
    {
        double o = show ? 1 : 0;
        GripTL.Opacity = o; GripTR.Opacity = o; GripBL.Opacity = o; GripBR.Opacity = o;
    }

    // 图片本体：双击关闭，单击拖动窗口（手动拖动，保证双击可靠）
    private void OnBodyLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Close(); return; }
        _moving = true;
        _moveStartCursor = CursorDip();
        _moveStartLeft = Left;
        _moveStartTop = Top;
        CaptureMouse();
    }

    private void OnBodyMove(object sender, MouseEventArgs e)
    {
        if (!_moving) return;
        var c = CursorDip();
        Left = _moveStartLeft + (c.X - _moveStartCursor.X);
        Top = _moveStartTop + (c.Y - _moveStartCursor.Y);
    }

    private void OnBodyUp(object sender, MouseButtonEventArgs e)
    {
        if (!_moving) return;
        _moving = false;
        ReleaseMouseCapture();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), MinScale, MaxScale);
        Img.Width = _baseW * _scale;
        Img.Height = _baseH * _scale;
    }

    // ---------- 四角拖拽缩放（保持比例，对角固定） ----------

    private void Grip_Down(object sender, MouseButtonEventArgs e)
    {
        var grip = (FrameworkElement)sender;
        double imgL = Left + 12, imgT = Top + 12;
        double imgR = imgL + Img.ActualWidth, imgB = imgT + Img.ActualHeight;
        switch ((string)grip.Tag)
        {
            case "BR": _anchorX = imgL; _anchorY = imgT; _leftFixed = true;  _topFixed = true;  break;
            case "TL": _anchorX = imgR; _anchorY = imgB; _leftFixed = false; _topFixed = false; break;
            case "TR": _anchorX = imgL; _anchorY = imgB; _leftFixed = true;  _topFixed = false; break;
            case "BL": _anchorX = imgR; _anchorY = imgT; _leftFixed = false; _topFixed = true;  break;
        }
        _resizing = true;
        grip.CaptureMouse();
        e.Handled = true;
    }

    private void Grip_Move(object sender, MouseEventArgs e)
    {
        if (!_resizing) return;
        var cur = CursorDip();
        double dW = _leftFixed ? cur.X - _anchorX : _anchorX - cur.X;
        double dH = _topFixed ? cur.Y - _anchorY : _anchorY - cur.Y;

        // 保持比例：取两个方向需要的缩放中较大者，让拖拽角贴近光标
        double scale = Math.Clamp(Math.Max(dW / _baseW, dH / _baseH), MinScale, MaxScale);
        double newW = _baseW * scale, newH = _baseH * scale;

        Img.Width = newW;
        Img.Height = newH;
        _scale = scale;

        // 让固定的对角保持不动（SizeToContent 会从左上扩展，这里再校正窗口位置）
        double tlx = _leftFixed ? _anchorX : _anchorX - newW;
        double tly = _topFixed ? _anchorY : _anchorY - newH;
        Left = tlx - 12;
        Top = tly - 12;
        e.Handled = true;
    }

    private void Grip_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        ((FrameworkElement)sender).ReleaseMouseCapture();
        if (!IsMouseOver) { Frame.BorderBrush = Brushes.Transparent; ShowGrips(false); }
        e.Handled = true;
    }

    private Point CursorDip()
    {
        Native.GetCursorPos(out var p);
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
            return src.CompositionTarget.TransformFromDevice.Transform(new Point(p.X, p.Y));
        return new Point(p.X, p.Y);
    }

    // ---------- 右键菜单 ----------

    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetImage(_image);

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _scale = 1.0;
        Img.Width = _baseW;
        Img.Height = _baseH;
    }

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
