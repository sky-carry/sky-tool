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

    private string _tool = "move";        // move/rect/ellipse/arrow/pen/text/mosaic
    private Color _color = Color.FromRgb(0xE5, 0x39, 0x35);
    private double _thickness = 4;

    private readonly Stack<UIElement> _undo = new();
    private readonly Stack<UIElement> _redo = new();
    private FrameworkElement _drawing;    // 正在拖画的元素
    private Point _shapeStart;
    private TextBox _editingText;

    // 选择/移动/缩放已有标注
    private enum EditOp { None, Move, Resize }
    private EditOp _op;
    private FrameworkElement _opEl;
    private Point _opStart;                 // 拖拽起点（DIP）
    private Rect _opOrigRect;               // Canvas 定位元素的起始矩形
    private TranslateTransform _opTT;       // 变换移动（箭头/画笔）
    private double _opTTx, _opTTy;
    private string _resizeEdge;             // L/R/T/B 组合，如 "TL"

    // 选区自身的移动/缩放（编辑阶段、选择工具下，可二次调整框选范围）
    private enum SelOp { None, Move, Resize }
    private SelOp _selOp;
    private string _selEdge;
    private Rect _selOrig;
    private Point _selStart;

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
            _toolButtons["move"] = BtnMove;
            _toolButtons["rect"] = BtnRect;
            _toolButtons["ellipse"] = BtnEllipse;
            _toolButtons["arrow"] = BtnArrow;
            _toolButtons["pen"] = BtnPen;
            _toolButtons["text"] = BtnText;
            _toolButtons["mosaic"] = BtnMosaic;
            HighlightTool(); // 默认选择/移动模式高亮
            BuildColorPanel();
#if OCR
            BtnOcr.Click += Ocr_Click;
#else
            // Lite 版不含 OCR：隐藏按钮及其前置分隔条
            BtnOcr.Visibility = Visibility.Collapsed;
            SepOcr.Visibility = Visibility.Collapsed;
#endif
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
        _op = EditOp.None; _opEl = null;
        AnnotCanvas.Children.Clear();
        _undo.Clear();
        _redo.Clear();
        AnnotCanvas.Clip = null;
        Toolbar.Visibility = Visibility.Collapsed;
        _tool = "move";
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

        // 双击（Snipaste 行为）：选区内 = 复制图片并关闭，选区外 = 取消截图。
        // 正在用绘图工具且点在选区内时不拦截，交给绘制逻辑，避免误触。
        if (e.ClickCount == 2 && !(IsDrawingTool(_tool) && _sel.Contains(p)))
        {
            if (!_sel.IsEmpty && _sel.Contains(p)) FinishCopy();
            else Close();
            return;
        }

        if (_mode == Mode.Selecting)
        {
            _mode = Mode.Dragging;
            _dragStart = p;
            _manualDrag = false; // 还没拖动；单击松手就选中当前吸附的窗口
            CaptureMouse();
        }
        else if (_mode == Mode.Editing && IsDrawingTool(_tool) && _sel.Contains(p))
        {
            CommitTextEditing();
            // 鼠标落在已有标注上（边角→缩放、其余→移动）时直接操作它，不画新图形；
            // 这样画框工具一直开着也能就地改大小/挪位置，无需切回选择模式
            if (TryStartManipulation(p))
            {
                // 已抓住现有标注
            }
            else if (_tool == "text")
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
        else if (_mode == Mode.Editing) // 选择/移动模式：先拖已有标注，否则拖选区本身（移动/缩放）
        {
            CommitTextEditing();
            if (TryStartManipulation(p) || TryStartSelEdit(p)) e.Handled = true;
        }
    }

    /// <summary>选择工具下，点在选区边/角则缩放、点在框内则移动整个选区；命中则捕获鼠标返回 true。</summary>
    private bool TryStartSelEdit(Point p)
    {
        if (_sel.IsEmpty) return false;
        string edge = HitEdge(_sel, p, 10);
        if (edge != null)
        {
            _selOp = SelOp.Resize;
            _selEdge = edge;
        }
        else if (_sel.Contains(p))
        {
            _selOp = SelOp.Move;
        }
        else return false;

        _selOrig = _sel;
        _selStart = p;
        CaptureMouse();
        return true;
    }

    /// <summary>若光标命中已有标注，开始移动/缩放它并捕获鼠标，返回 true；未命中返回 false。</summary>
    private bool TryStartManipulation(Point p)
    {
        var el = HitAnnotation(p) as FrameworkElement;
        if (el == null) return false;
        _opEl = el;
        _opStart = p;
        string edge = IsResizable(el) ? HitEdge(ElRect(el), p, 9) : null;
        if (edge != null)
        {
            _op = EditOp.Resize;
            _resizeEdge = edge;
            _opOrigRect = ElRect(el);
        }
        else
        {
            _op = EditOp.Move;
            if (double.IsNaN(Canvas.GetLeft(el))) // 箭头/画笔：用平移变换移动
            {
                _opTT = el.RenderTransform as TranslateTransform ?? new TranslateTransform();
                el.RenderTransform = _opTT; _opTTx = _opTT.X; _opTTy = _opTT.Y;
            }
            else _opOrigRect = ElRect(el); // Canvas 定位：改 Left/Top
        }
        CaptureMouse();
        return true;
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
        else if (_mode == Mode.Editing && _op == EditOp.Move)
        {
            double dx = p.X - _opStart.X, dy = p.Y - _opStart.Y;
            if (double.IsNaN(Canvas.GetLeft(_opEl)))
            {
                _opTT.X = _opTTx + dx; _opTT.Y = _opTTy + dy;
            }
            else
            {
                Canvas.SetLeft(_opEl, _opOrigRect.X + dx);
                Canvas.SetTop(_opEl, _opOrigRect.Y + dy);
            }
        }
        else if (_mode == Mode.Editing && _op == EditOp.Resize)
        {
            var r = _opOrigRect;
            double l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
            if (_resizeEdge.Contains('L')) l = p.X;
            if (_resizeEdge.Contains('R')) rr = p.X;
            if (_resizeEdge.Contains('T')) t = p.Y;
            if (_resizeEdge.Contains('B')) b = p.Y;
            double nl = Math.Min(l, rr), nt = Math.Min(t, b);
            double nw = Math.Max(Math.Abs(rr - l), 4), nh = Math.Max(Math.Abs(b - t), 4);
            Canvas.SetLeft(_opEl, nl); Canvas.SetTop(_opEl, nt);
            _opEl.Width = nw; _opEl.Height = nh;
        }
        else if (_mode == Mode.Editing && _selOp == SelOp.Move)
        {
            double dx = p.X - _selStart.X, dy = p.Y - _selStart.Y;
            double nx = Math.Clamp(_selOrig.X + dx, 0, Math.Max(0, Root.ActualWidth - _selOrig.Width));
            double ny = Math.Clamp(_selOrig.Y + dy, 0, Math.Max(0, Root.ActualHeight - _selOrig.Height));
            _sel = new Rect(nx, ny, _selOrig.Width, _selOrig.Height);
            UpdateSelectionVisual();
            AnnotCanvas.Clip = new RectangleGeometry(_sel);
        }
        else if (_mode == Mode.Editing && _selOp == SelOp.Resize)
        {
            // 把指针限制在屏幕内，缩放后的选区自然不会越界
            double px = Math.Clamp(p.X, 0, Root.ActualWidth);
            double py = Math.Clamp(p.Y, 0, Root.ActualHeight);
            var r = _selOrig;
            double l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
            if (_selEdge.Contains('L')) l = px;
            if (_selEdge.Contains('R')) rr = px;
            if (_selEdge.Contains('T')) t = py;
            if (_selEdge.Contains('B')) b = py;
            double nl = Math.Min(l, rr), nt = Math.Min(t, b);
            double nw = Math.Max(Math.Abs(rr - l), 8), nh = Math.Max(Math.Abs(b - t), 8);
            _sel = new Rect(nl, nt, nw, nh);
            UpdateSelectionVisual();
            AnnotCanvas.Clip = new RectangleGeometry(_sel);
        }
        else if (_mode == Mode.Editing && _op == EditOp.None && _selOp == SelOp.None && _drawing == null)
        {
            UpdateHoverCursor(p); // 悬停时按位置显示 缩放/移动/绘制 光标
        }
    }

    /// <summary>悬停光标：标注上 → 缩放/移动；绘图工具空白处 → 十字；
    /// 选择工具下选区边/角 → 缩放、框内 → 移动；其余 → 箭头。</summary>
    private void UpdateHoverCursor(Point p)
    {
        var el = HitAnnotation(p) as FrameworkElement;
        if (el != null)
        {
            string aedge = IsResizable(el) ? HitEdge(ElRect(el), p, 9) : null;
            Cursor = aedge != null ? EdgeCursor(aedge) : Cursors.SizeAll;
            return;
        }
        if (IsDrawingTool(_tool)) { Cursor = Cursors.Cross; return; }
        if (!_sel.IsEmpty)
        {
            string sedge = HitEdge(_sel, p, 10);
            if (sedge != null) { Cursor = EdgeCursor(sedge); return; }
            if (_sel.Contains(p)) { Cursor = Cursors.SizeAll; return; }
        }
        Cursor = Cursors.Arrow;
    }

    private static bool IsResizable(UIElement el) => el is Rectangle || el is Ellipse;

    private static Rect ElRect(FrameworkElement el)
    {
        double l = Canvas.GetLeft(el); double t = Canvas.GetTop(el);
        if (double.IsNaN(l)) l = 0; if (double.IsNaN(t)) t = 0;
        double w = double.IsNaN(el.Width) ? el.ActualWidth : el.Width;
        double h = double.IsNaN(el.Height) ? el.ActualHeight : el.Height;
        return new Rect(l, t, w, h);
    }

    /// <summary>判断点 p 落在矩形 r 的哪条边/角（容差 tol），返回如 "TL"/"R"，无则 null。</summary>
    private static string HitEdge(Rect r, Point p, double tol)
    {
        if (p.X < r.Left - tol || p.X > r.Right + tol || p.Y < r.Top - tol || p.Y > r.Bottom + tol)
            return null;
        string s = "";
        if (Math.Abs(p.Y - r.Top) <= tol) s += "T";
        else if (Math.Abs(p.Y - r.Bottom) <= tol) s += "B";
        if (Math.Abs(p.X - r.Left) <= tol) s += "L";
        else if (Math.Abs(p.X - r.Right) <= tol) s += "R";
        return s.Length == 0 ? null : s;
    }

    private static Cursor EdgeCursor(string edge) => edge switch
    {
        "TL" or "BR" => Cursors.SizeNWSE,
        "TR" or "BL" => Cursors.SizeNESW,
        "L" or "R" => Cursors.SizeWE,
        "T" or "B" => Cursors.SizeNS,
        _ => Cursors.SizeAll,
    };

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
            // 画完保持当前工具不变，可连续画；要移动/缩放就点工具栏对应按钮切回选择模式
        }
        else if (_mode == Mode.Editing && _op != EditOp.None)
        {
            ReleaseMouseCapture();
            _op = EditOp.None;
            _opEl = null;
        }
        else if (_mode == Mode.Editing && _selOp != SelOp.None)
        {
            ReleaseMouseCapture();
            _selOp = SelOp.None;
            ShowToolbar(); // 选区已变，工具栏跟到新位置
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
        // 再次点击当前工具 → 回到选择/移动模式
        _tool = _tool == tag ? "move" : tag;
        HighlightTool();
    }

    private static bool IsDrawingTool(string t) =>
        t is "rect" or "ellipse" or "arrow" or "pen" or "text" or "mosaic";

    private void HighlightTool()
    {
        foreach (var (key, btn) in _toolButtons)
            btn.Background = key == _tool ? new SolidColorBrush(Color.FromRgb(0x3E, 0x5A, 0x95)) : Brushes.Transparent;
        Cursor = IsDrawingTool(_tool) ? Cursors.Cross : Cursors.Arrow;
    }

    /// <summary>命中光标下的标注元素（AnnotCanvas 的直接子元素）。</summary>
    private UIElement HitAnnotation(Point p)
    {
        var hit = AnnotCanvas.InputHitTest(p) as DependencyObject;
        while (hit != null && hit != AnnotCanvas)
        {
            if (hit is UIElement ue && AnnotCanvas.Children.Contains(ue)) return ue;
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        }
        return null;
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
                // Fill=Transparent 让框内区域也能被点中拖动（视觉仍透明）
                var r = new Rectangle { Stroke = brush, Fill = Brushes.Transparent, StrokeThickness = _thickness, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(r, p.X); Canvas.SetTop(r, p.Y);
                return r;
            }
            case "ellipse":
            {
                var el = new Ellipse { Stroke = brush, Fill = Brushes.Transparent, StrokeThickness = _thickness };
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
                PushUndo(mosaic);
            }
            return;
        }
        PushUndo(el);
    }

    /// <summary>记录一次新增标注（清空重做栈）。</summary>
    private void PushUndo(UIElement el)
    {
        _undo.Push(el);
        _redo.Clear();
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
        PushUndo(block);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        CommitTextEditing();
        if (_undo.Count > 0)
        {
            var el = _undo.Pop();
            AnnotCanvas.Children.Remove(el);
            _redo.Push(el);
        }
    }

    private void Redo()
    {
        CommitTextEditing();
        if (_redo.Count > 0)
        {
            var el = _redo.Pop();
            AnnotCanvas.Children.Add(el);
            _undo.Push(el);
        }
    }

    // ---------- 输出 ----------
    private BitmapSource RenderResult()
    {
        CommitTextEditing();
        var px = DipRectToPixels(_sel);
        // 直接从原始截图按像素裁剪 —— 屏幕内容零重采样，画质无损
        var baseCrop = new CroppedBitmap(_shot.Bitmap, px);

        // 没有标注：直接返回原图裁剪（最高画质）
        if (AnnotCanvas.Children.Count == 0)
        {
            baseCrop.Freeze();
            return baseCrop;
        }

        // 有标注：在原图分辨率上叠加标注，屏幕内容仍保持像素级
        double sx = (double)px.Width / _sel.Width;
        double sy = (double)px.Height / _sel.Height;
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(baseCrop, new Rect(0, 0, px.Width, px.Height));
            dc.PushTransform(new ScaleTransform(sx, sy));
            dc.PushTransform(new TranslateTransform(-_sel.X, -_sel.Y));
            var vb = new VisualBrush(AnnotCanvas)
            {
                Stretch = Stretch.None,
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, Root.ActualWidth, Root.ActualHeight),
                Viewport = new Rect(0, 0, Root.ActualWidth, Root.ActualHeight),
            };
            dc.DrawRectangle(vb, null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
            dc.Pop();
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(px.Width, px.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
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

    // ---------- 文字提取（OCR，仅 Pro 版编译） ----------
#if OCR
    private async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (_sel.IsEmpty) return;
        // OCR 用原始截图按像素裁剪（不含标注），识别更准
        BitmapSource crop;
        try
        {
            crop = new CroppedBitmap(_shot.Bitmap, DipRectToPixels(_sel));
            crop.Freeze();
        }
        catch { return; }

        // 记下选区的物理像素矩形，供结果窗贴到原图右侧
        int selX = _shot.X + (int)Math.Round(_sel.X * PxPerDipX);
        int selY = _shot.Y + (int)Math.Round(_sel.Y * PxPerDipY);
        int selW = (int)Math.Round(_sel.Width * PxPerDipX);
        int selH = (int)Math.Round(_sel.Height * PxPerDipY);

        string text = null;
        try { text = await OcrUtil.RecognizeAsync(crop); }
        catch { /* 识别失败按"无结果"处理 */ }

        Close(); // 关掉截图蒙层再弹结果

        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(
                OcrUtil.IsAvailable ? "没有识别到文字。" : "系统未安装 OCR 语言包，无法识别文字。",
                "Sky 工具箱", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Clipboard.SetText(text);
        new OcrResultWindow(text, selX, selY, selW, selH).Show();
    }
#endif

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
            case Key.Y when Keyboard.Modifiers == ModifierKeys.Control:
            case Key.Z when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                Redo();
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
