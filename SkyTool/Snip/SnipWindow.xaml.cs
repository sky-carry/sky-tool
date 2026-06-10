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
    private readonly List<Int32Rect> _windowRects; // 截图瞬间的窗口边界快照（物理像素，Z 序）

    private enum Mode { Selecting, Dragging, Editing }
    private Mode _mode = Mode.Selecting;

    private Point _dragStart;
    private bool _manualDrag;              // 本次按下是否真正拖动了（否则视为单击选中悬停窗口）
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
        var winRects = ScreenCapture.SnapshotWindowRects(); // 在蒙层出现前快照窗口边界
        _current = new SnipWindow(shot, winRects);
        _current.Show();
        _current.Activate();
    }

    /// <summary>全局 F3 按下时：若正在截图且已框选则固定当前选区。
    /// 返回 true 表示按键已被截图窗口消化（不再贴剪贴板图）。</summary>
    public static bool PinCurrent()
    {
        if (_current == null) return false;
        if (_current._mode == Mode.Editing && !_current._sel.IsEmpty)
            _current.FinishPin();
        return true;
    }

    private SnipWindow(ScreenCapture.Shot shot, List<Int32Rect> windowRects)
    {
        InitializeComponent();
        _shot = shot;
        _windowRects = windowRects;
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
            // 蒙层刚出现时就对光标下的窗口做一次吸附高亮
            Native.GetCursorPos(out var pt);
            HoverAt(new Point((pt.X - _shot.X) / PxPerDipX, (pt.Y - _shot.Y) / PxPerDipY));
        };
        Closed += (_, _) => _current = null;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonDown += OnRightDown;
        KeyDown += OnKey;
    }

    /// <summary>右键：标注/已框选阶段 → 退回重新框选；框选阶段 → 退出截图。</summary>
    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        if (Toolbar.IsMouseOver) return;
        if (_mode == Mode.Editing) ResetToSelecting();
        else if (_mode == Mode.Selecting) Close();
    }

    private void ResetToSelecting()
    {
        _editingText = null;          // 丢弃未提交的文字
        _drawing = null;
        AnnotCanvas.Children.Clear();
        _undo.Clear();
        AnnotCanvas.Clip = null;
        Toolbar.Visibility = Visibility.Collapsed;
        _tool = null;
        foreach (var btn in _toolButtons.Values) btn.Background = Brushes.Transparent;
        _mode = Mode.Selecting;
        Cursor = Cursors.Cross;
        _sel = Rect.Empty;
        SelRect.Visibility = Visibility.Collapsed;
        SizeTip.Visibility = Visibility.Collapsed;
        UpdateDim();
        HoverAt(Mouse.GetPosition(Root)); // 立即恢复窗口吸附高亮
    }

    // ---------- 悬停吸附窗口边框 ----------

    /// <summary>找鼠标（DIP 坐标）下最顶层窗口的边界，没有命中则返回整个屏幕。</summary>
    private Rect HitWindowRect(Point pDip)
    {
        double px = _shot.X + pDip.X * PxPerDipX;
        double py = _shot.Y + pDip.Y * PxPerDipY;
        foreach (var r in _windowRects)
        {
            if (px < r.X || px >= r.X + r.Width || py < r.Y || py >= r.Y + r.Height) continue;
            var rect = new Rect(
                (r.X - _shot.X) / PxPerDipX,
                (r.Y - _shot.Y) / PxPerDipY,
                r.Width / PxPerDipX,
                r.Height / PxPerDipY);
            rect.Intersect(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
            if (rect.IsEmpty) continue;
            return rect;
        }
        return new Rect(0, 0, Root.ActualWidth, Root.ActualHeight);
    }

    private void HoverAt(Point pDip)
    {
        if (Root.ActualWidth <= 0) return;
        _sel = HitWindowRect(pDip);
        SelRect.Visibility = Visibility.Visible;
        UpdateSelectionVisual();
    }

    // ---------- 像素 ↔ DIP 换算 ----------
    private double PxPerDipX => _shot.Width / Root.ActualWidth;
    private double PxPerDipY => _shot.Height / Root.ActualHeight;

    // ---------- 颜色面板 ----------
    private void BuildColorPanel()
    {
        Color[] colors =
        {
            Color.FromRgb(0xF3, 0x5B, 0x6A), // 红
            Color.FromRgb(0xF5, 0xC9, 0x7B), // 黄
            Color.FromRgb(0x4E, 0xCB, 0x71), // 绿
            Color.FromRgb(0x7A, 0xA2, 0xF7), // 蓝
            Colors.White,
            Colors.Black,
        };
        foreach (var c in colors)
        {
            var dot = new Ellipse
            {
                Width = 16, Height = 16, Margin = new Thickness(3, 0, 3, 0),
                Fill = new SolidColorBrush(c),
                Stroke = c == _color ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = c == _color ? 2 : 1,
                Cursor = Cursors.Hand,
                Tag = c,
            };
            dot.MouseLeftButtonDown += (s, e) =>
            {
                _color = (Color)((Ellipse)s).Tag;
                foreach (var child in ColorPanel.Children.OfType<Ellipse>())
                {
                    bool sel = (Color)child.Tag == _color;
                    child.Stroke = sel ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
                    child.StrokeThickness = sel ? 2 : 1;
                }
                e.Handled = true;
            };
            ColorPanel.Children.Add(dot);
        }
    }

    // ---------- 选区 ----------
    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (Toolbar.IsMouseOver) return;
        var p = e.GetPosition(Root);

        // 双击取消截图（标注工具激活且点在选区内时除外，避免误关）
        if (e.ClickCount == 2 && (_tool == null || !_sel.Contains(p)))
        {
            Close();
            return;
        }

        if (_mode == Mode.Selecting)
        {
            _mode = Mode.Dragging;
            _dragStart = p;
            _manualDrag = false; // 还没拖动；单击松手就选中当前吸附的窗口
            CaptureMouse();
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
        if (_mode == Mode.Selecting)
        {
            HoverAt(p); // 悬停吸附窗口边框
        }
        else if (_mode == Mode.Dragging)
        {
            if (!_manualDrag && (p - _dragStart).Length > 4)
                _manualDrag = true;
            if (_manualDrag)
            {
                _sel = new Rect(_dragStart, p);
                UpdateSelectionVisual();
            }
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
            // 没拖动 = 单击：直接采用悬停吸附的窗口选区（_sel 已是该窗口边界）
            if (_sel.IsEmpty || _sel.Width < 5 || _sel.Height < 5)
            {
                _mode = Mode.Selecting;
                HoverAt(e.GetPosition(Root));
                return;
            }
            EnterEditing();
        }
        else if (_mode == Mode.Editing && _drawing != null)
        {
            ReleaseMouseCapture();
            FinishShape(_drawing);
            _drawing = null;
        }
    }

    private void EnterEditing()
    {
        _mode = Mode.Editing;
        Cursor = Cursors.Arrow;
        AnnotCanvas.Clip = new RectangleGeometry(_sel);
        ShowToolbar();
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
            btn.Background = key == _tool ? new SolidColorBrush(Color.FromRgb(0x3E, 0x5A, 0x95)) : Brushes.Transparent;
        Cursor = _tool == null ? Cursors.Arrow : Cursors.Cross;
    }

    private void Thick_Click(object sender, RoutedEventArgs e)
    {
        _thickness = double.Parse((string)((Button)sender).Tag);
        foreach (var b in new[] { BtnThin, BtnMid, BtnBold })
            b.Background = Brushes.Transparent;
        ((Button)sender).Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x5A, 0x95));
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
        double headLen = Math.Min(Math.Max(thickness * 4, 10), len); // 箭头很短时收缩箭头头部，不能用 Clamp(min>max 会抛异常)
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
