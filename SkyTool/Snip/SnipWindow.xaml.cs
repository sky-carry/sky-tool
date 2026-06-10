using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SkyTool.Common;

namespace SkyTool.Snip;

/// <summary>截图窗口：全屏蒙层 → 框选 → 标注（矩形/椭圆/箭头/画笔/文字/马赛克）→ 贴图/复制/保存。</summary>
public partial class SnipWindow : Window
{
    private static SnipWindow _current;

    private readonly ScreenCapture.Shot _shot;

    private enum Mode { Selecting, Dragging, Editing }
    private Mode _mode = Mode.Selecting;

    private Point _dragStart;
    private Rect _sel = Rect.Empty;

    private string _tool;                 // rect/ellipse/arrow/pen/text/mosaic/null
    private Color _color = Color.FromRgb(0xE5, 0x39, 0x35);
    private double _thickness = 4;

    private readonly Stack<UIElement> _undo = new();
    private FrameworkElement _drawing;    // 正在拖画的元素
    private Point _shapeStart;
    private TextBox _editingText;

    private readonly Dictionary<string, Button> _toolButtons = new();

    public static void Open()
    {
        if (_current != null) return; // 已经在截图中
        var shot = ScreenCapture.CaptureVirtualScreen();
        _current = new SnipWindow(shot);
        _current.Show();
        _current.Activate();
    }

    private SnipWindow(ScreenCapture.Shot shot)
    {
        InitializeComponent();
        _shot = shot;
        ScreenImg.Source = shot.Bitmap;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // 用物理像素直接铺满整个虚拟屏幕
            Native.SetWindowPos(hwnd, IntPtr.Zero, shot.X, shot.Y, shot.Width, shot.Height, 0x0004 /*SWP_NOZORDER*/);
        };

        Loaded += (_, _) =>
        {
            UpdateDim();
            Keyboard.Focus(this);
            _toolButtons["rect"] = BtnRect;
            _toolButtons["ellipse"] = BtnEllipse;
            _toolButtons["arrow"] = BtnArrow;
            _toolButtons["pen"] = BtnPen;
            _toolButtons["text"] = BtnText;
            _toolButtons["mosaic"] = BtnMosaic;
            BuildColorPanel();
        };
        Closed += (_, _) => _current = null;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += OnKey;
    }

    // ---------- 像素 ↔ DIP 换算 ----------
    private double PxPerDipX => _shot.Width / Root.ActualWidth;
    private double PxPerDipY => _shot.Height / Root.ActualHeight;

    // ---------- 颜色面板 ----------
    private void BuildColorPanel()
    {
        Color[] colors =
        {
            Color.FromRgb(0xE5, 0x39, 0x35), // 红
            Color.FromRgb(0xFF, 0xB3, 0x00), // 黄
            Color.FromRgb(0x43, 0xA0, 0x47), // 绿
            Color.FromRgb(0x1E, 0x88, 0xE5), // 蓝
            Colors.White,
            Colors.Black,
        };
        foreach (var c in colors)
        {
            var btn = new Button
            {
                Width = 18, Height = 18, Margin = new Thickness(2, 0, 2, 0),
                Background = new SolidColorBrush(c),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(c == _color ? 2 : 1),
                Cursor = Cursors.Arrow,
                Tag = c,
            };
            btn.Click += (s, _) =>
            {
                _color = (Color)((Button)s).Tag;
                foreach (var child in ColorPanel.Children.OfType<Button>())
                    child.BorderThickness = new Thickness(((Color)child.Tag) == _color ? 2.5 : 1);
            };
            ColorPanel.Children.Add(btn);
        }
    }

    // ---------- 选区 ----------
    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (Toolbar.IsMouseOver) return;
        var p = e.GetPosition(Root);

        if (_mode == Mode.Selecting)
        {
            _mode = Mode.Dragging;
            _dragStart = p;
            _sel = new Rect(p, p);
            SelRect.Visibility = Visibility.Visible;
            CaptureMouse();
            UpdateSelectionVisual();
        }
        else if (_mode == Mode.Editing && _tool != null && _sel.Contains(p))
        {
            CommitTextEditing();
            if (_tool == "text")
            {
                StartTextAt(p);
            }
            else
            {
                _shapeStart = p;
                _drawing = CreateShape(p);
                if (_drawing != null) AnnotCanvas.Children.Add(_drawing);
                CaptureMouse();
            }
            e.Handled = true;
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Root);
        if (_mode == Mode.Dragging)
        {
            _sel = new Rect(_dragStart, p);
            UpdateSelectionVisual();
        }
        else if (_mode == Mode.Editing && _drawing != null)
        {
            UpdateShape(_drawing, _shapeStart, ClampToSel(p));
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == Mode.Dragging)
        {
            ReleaseMouseCapture();
            if (_sel.Width < 5 || _sel.Height < 5)
            {
                _mode = Mode.Selecting;
                SelRect.Visibility = Visibility.Collapsed;
                _sel = Rect.Empty;
                UpdateDim();
                return;
            }
            _mode = Mode.Editing;
            Cursor = Cursors.Arrow;
            AnnotCanvas.Clip = new RectangleGeometry(_sel);
            ShowToolbar();
        }
        else if (_mode == Mode.Editing && _drawing != null)
        {
            ReleaseMouseCapture();
            FinishShape(_drawing);
            _drawing = null;
        }
    }

    private Point ClampToSel(Point p) => new(
        Math.Clamp(p.X, _sel.Left, _sel.Right),
        Math.Clamp(p.Y, _sel.Top, _sel.Bottom));

    private void UpdateSelectionVisual()
    {
        Canvas.SetLeft(SelRect, _sel.X);
        Canvas.SetTop(SelRect, _sel.Y);
        SelRect.Width = _sel.Width;
        SelRect.Height = _sel.Height;
        UpdateDim();

        int pw = (int)Math.Round(_sel.Width * PxPerDipX);
        int ph = (int)Math.Round(_sel.Height * PxPerDipY);
        SizeTipText.Text = $"{pw} × {ph}";
        SizeTip.Visibility = Visibility.Visible;
        Canvas.SetLeft(SizeTip, _sel.X);
        Canvas.SetTop(SizeTip, Math.Max(0, _sel.Y - 26));
    }

    private void UpdateDim()
    {
        var full = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        if (_sel.IsEmpty || _sel.Width <= 0)
        {
            DimPath.Data = full;
        }
        else
        {
            var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
            g.Children.Add(full);
            g.Children.Add(new RectangleGeometry(_sel));
            DimPath.Data = g;
        }
    }

    private void ShowToolbar()
    {
        Toolbar.Visibility = Visibility.Visible;
        Toolbar.UpdateLayout();
        double tw = Toolbar.ActualWidth, th = Toolbar.ActualHeight;
        double x = Math.Min(Math.Max(0, _sel.Right - tw), Root.ActualWidth - tw);
        double y = _sel.Bottom + 8;
        if (y + th > Root.ActualHeight) y = Math.Max(0, _sel.Top - th - 8);
        Toolbar.Margin = new Thickness(x, y, 0, 0);
    }

    // ---------- 标注工具 ----------
    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        string tag = (string)((Button)sender).Tag;
        _tool = _tool == tag ? null : tag;
        foreach (var (key, btn) in _toolButtons)
            btn.Background = key == _tool ? new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF)) : Brushes.Transparent;
        Cursor = _tool == null ? Cursors.Arrow : Cursors.Cross;
    }

    private void Thick_Click(object sender, RoutedEventArgs e)
    {
        _thickness = double.Parse((string)((Button)sender).Tag);
        foreach (var b in new[] { BtnThin, BtnMid, BtnBold })
            b.Background = Brushes.Transparent;
        ((Button)sender).Background = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
    }

    private FrameworkElement CreateShape(Point p)
    {
        var brush = new SolidColorBrush(_color);
        switch (_tool)
        {
            case "rect":
            {
                var r = new Rectangle { Stroke = brush, StrokeThickness = _thickness, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(r, p.X); Canvas.SetTop(r, p.Y);
                return r;
            }
            case "ellipse":
            {
                var el = new Ellipse { Stroke = brush, StrokeThickness = _thickness };
                Canvas.SetLeft(el, p.X); Canvas.SetTop(el, p.Y);
                return el;
            }
            case "arrow":
            {
                var path = new System.Windows.Shapes.Path { Stroke = brush, Fill = brush, StrokeThickness = _thickness, StrokeLineJoin = PenLineJoin.Round };
                return path;
            }
            case "pen":
            {
                var pl = new Polyline
                {
                    Stroke = brush, StrokeThickness = _thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                pl.Points.Add(p);
                return pl;
            }
            case "mosaic":
            {
                var r = new Rectangle { Stroke = Brushes.Gray, StrokeDashArray = new DoubleCollection { 3, 3 }, StrokeThickness = 1 };
                Canvas.SetLeft(r, p.X); Canvas.SetTop(r, p.Y);
                return r;
            }
        }
        return null;
    }

    private void UpdateShape(FrameworkElement el, Point a, Point b)
    {
        switch (_tool)
        {
            case "rect" or "ellipse" or "mosaic":
            {
                var r = new Rect(a, b);
                Canvas.SetLeft(el, r.X); Canvas.SetTop(el, r.Y);
                el.Width = r.Width; el.Height = r.Height;
                break;
            }
            case "arrow":
                ((System.Windows.Shapes.Path)el).Data = BuildArrow(a, b, _thickness);
                break;
            case "pen":
                ((Polyline)el).Points.Add(b);
                break;
        }
    }

    private void FinishShape(FrameworkElement el)
    {
        if (_tool == "mosaic")
        {
            AnnotCanvas.Children.Remove(el); // 删掉虚线预览框，换成真正的马赛克块
            double x = Canvas.GetLeft(el), y = Canvas.GetTop(el);
            var region = new Rect(x, y, el.Width is double.NaN ? 0 : el.Width, el.Height is double.NaN ? 0 : el.Height);
            if (region.Width < 4 || region.Height < 4) return;
            var mosaic = BuildMosaic(region);
            if (mosaic != null)
            {
                AnnotCanvas.Children.Add(mosaic);
                _undo.Push(mosaic);
            }
            return;
        }
        _undo.Push(el);
    }

    private static Geometry BuildArrow(Point a, Point b, double thickness)
    {
        var v = b - a;
        double len = v.Length;
        if (len < 1) return new LineGeometry(a, b);
        v.Normalize();
        var perp = new Vector(-v.Y, v.X);
        double headLen = Math.Clamp(thickness * 4, 10, len);
        double headW = headLen * 0.6;
        Point basePt = b - v * headLen;

        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(a, basePt));
        var tri = new PathGeometry();
        var fig = new PathFigure { StartPoint = b, IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(basePt + perp * headW / 2, true));
        fig.Segments.Add(new LineSegment(basePt - perp * headW / 2, true));
        tri.Figures.Add(fig);
        g.Children.Add(tri);
        return g;
    }

    private Image BuildMosaic(Rect regionDip)
    {
        // 选区域对应的原图像素 → 缩小再放大（最近邻）得到马赛克
        var px = DipRectToPixels(regionDip);
        if (px.Width < 2 || px.Height < 2) return null;
        try
        {
            var cropped = new CroppedBitmap(_shot.Bitmap, px);
            double factor = Math.Max(8.0, Math.Max(px.Width, px.Height) / 24.0);
            var small = new TransformedBitmap(cropped, new ScaleTransform(1 / factor, 1 / factor));
            var img = new Image
            {
                Source = small,
                Width = regionDip.Width,
                Height = regionDip.Height,
                Stretch = Stretch.Fill,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(img, regionDip.X);
            Canvas.SetTop(img, regionDip.Y);
            return img;
        }
        catch { return null; }
    }

    private Int32Rect DipRectToPixels(Rect dip)
    {
        int x = (int)Math.Round(dip.X * PxPerDipX);
        int y = (int)Math.Round(dip.Y * PxPerDipY);
        int w = (int)Math.Round(dip.Width * PxPerDipX);
        int h = (int)Math.Round(dip.Height * PxPerDipY);
        x = Math.Clamp(x, 0, _shot.Bitmap.PixelWidth - 1);
        y = Math.Clamp(y, 0, _shot.Bitmap.PixelHeight - 1);
        w = Math.Clamp(w, 1, _shot.Bitmap.PixelWidth - x);
        h = Math.Clamp(h, 1, _shot.Bitmap.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    // ---------- 文字 ----------
    private void StartTextAt(Point p)
    {
        var tb = new TextBox
        {
            Foreground = new SolidColorBrush(_color),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            FontSize = 12 + _thickness * 3,
            FontWeight = FontWeights.Bold,
            MinWidth = 30,
            AcceptsReturn = false,
            CaretBrush = new SolidColorBrush(_color),
        };
        Canvas.SetLeft(tb, p.X);
        Canvas.SetTop(tb, p.Y);
        AnnotCanvas.Children.Add(tb);
        _editingText = tb;
        tb.LostKeyboardFocus += (_, _) => CommitTextEditing();
        tb.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { CommitTextEditing(); ke.Handled = true; }
        };
        Dispatcher.BeginInvoke(() => tb.Focus());
    }

    private void CommitTextEditing()
    {
        var tb = _editingText;
        if (tb == null) return;
        _editingText = null;
        double x = Canvas.GetLeft(tb), y = Canvas.GetTop(tb);
        string text = tb.Text;
        AnnotCanvas.Children.Remove(tb);
        if (string.IsNullOrWhiteSpace(text)) return;

        var block = new TextBlock
        {
            Text = text,
            Foreground = tb.Foreground,
            FontSize = tb.FontSize,
            FontWeight = FontWeights.Bold,
        };
        Canvas.SetLeft(block, x + 2);
        Canvas.SetTop(block, y + 2);
        AnnotCanvas.Children.Add(block);
        _undo.Push(block);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        CommitTextEditing();
        if (_undo.Count > 0)
            AnnotCanvas.Children.Remove(_undo.Pop());
    }

    // ---------- 输出 ----------
    private BitmapSource RenderResult()
    {
        CommitTextEditing();
        int pw = _shot.Bitmap.PixelWidth, ph = _shot.Bitmap.PixelHeight;
        var rtb = new RenderTargetBitmap(pw, ph,
            96.0 * pw / Root.ActualWidth, 96.0 * ph / Root.ActualHeight, PixelFormats.Pbgra32);
        rtb.Render(RenderHost);
        var cropped = new CroppedBitmap(rtb, DipRectToPixels(_sel));
        cropped.Freeze();
        return cropped;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => FinishCopy();

    private void FinishCopy()
    {
        if (_sel.IsEmpty) { Close(); return; }
        Clipboard.SetImage(RenderResult());
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_sel.IsEmpty) return;
        var img = RenderResult();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
            FileName = $"截图_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dlg.ShowDialog(this) == true)
        {
            BitmapEncoder encoder = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder { QualityLevel = 95 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(img));
            using var fs = File.Create(dlg.FileName);
            encoder.Save(fs);
            Close();
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => FinishPin();

    private void FinishPin()
    {
        if (_sel.IsEmpty) return;
        var img = RenderResult();
        int physX = _shot.X + (int)Math.Round(_sel.X * PxPerDipX);
        int physY = _shot.Y + (int)Math.Round(_sel.Y * PxPerDipY);
        Close();
        var pin = new PinWindow(img, physX, physY);
        pin.Show();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- 键盘 ----------
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_editingText != null && e.Key != Key.Escape) return;
        switch (e.Key)
        {
            case Key.Escape:
                if (_editingText != null) { CommitTextEditing(); }
                else Close();
                break;
            case Key.Enter:
                FinishCopy();
                break;
            case Key.F3:
                FinishPin();
                break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                Undo();
                break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.Control:
                Save_Click(null, null);
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                FinishCopy();
                break;
        }
    }
}
