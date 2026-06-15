using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SkyTool.Common;

namespace SkyTool.Snip;

/// <summary>文字提取结果：白色气泡卡片，带指向选区的小尖角，浮在原文旁边（不再复制截图本身）。</summary>
public partial class OcrResultWindow : Window
{
    private readonly int _selX, _selY, _selW, _selH; // 被截区域的物理像素矩形
    private bool _editing;

    public OcrResultWindow(string text, int selX, int selY, int selW, int selH)
    {
        InitializeComponent();
        _selX = selX; _selY = selY; _selW = selW; _selH = selH;
        ResultBox.Text = text ?? "";

        ContentRendered += (_, _) => PositionCallout(); // 此时已按内容定好尺寸
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        // 点标题区域可拖动整个气泡
        HeaderRow.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
    }

    /// <summary>把气泡摆到选区下方（放不下则上方），尖角水平对准选区中心。
    /// 用选区所在显示器的真实 DPI 做物理↔DIP 换算，规避多屏混合 DPI 的坐标歧义。</summary>
    private void PositionCallout()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int cx = _selX + _selW / 2;
        var mon = Native.MonitorFromPoint(new Native.POINT { X = cx, Y = _selY + _selH / 2 },
            Native.MONITOR_DEFAULTTONEAREST);

        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi))
        {
            mi.rcWork = new Native.RECT
            {
                Left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
                Top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
                Right = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN) + Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
                Bottom = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN) + Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN),
            };
        }

        double scale = 1.0;
        if (Native.GetDpiForMonitor(mon, Native.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;

        double wDip = ActualWidth, hDip = ActualHeight;
        int wPhys = (int)Math.Round(wDip * scale);
        int hPhys = (int)Math.Round(hDip * scale);

        const int gap = 2;
        const double apexTopDip = 18;            // 上尖角顶点距窗口顶（外层 Margin）
        double apexBotDip = hDip - 18;           // 下尖角顶点距窗口顶

        // 默认放选区下方，尖角朝上；下方放不下就放上方，尖角朝下
        bool below = true;
        int topPhys = (int)Math.Round(_selY + _selH + gap - apexTopDip * scale);
        if (topPhys + hPhys > mi.rcWork.Bottom)
        {
            below = false;
            topPhys = (int)Math.Round(_selY - gap - apexBotDip * scale);
        }

        int leftPhys = cx - wPhys / 2;
        leftPhys = Math.Clamp(leftPhys, mi.rcWork.Left, Math.Max(mi.rcWork.Left, mi.rcWork.Right - wPhys));
        topPhys = Math.Clamp(topPhys, mi.rcWork.Top, Math.Max(mi.rcWork.Top, mi.rcWork.Bottom - hPhys));

        // 尖角水平位置（DIP）：对准选区中心，夹在卡片身体内
        double tailLeft = (cx - leftPhys) / scale - 11;
        tailLeft = Math.Clamp(tailLeft, 24, Math.Max(24, wDip - 18 - 22 - 6));
        if (below)
        {
            TailUp.Margin = new Thickness(tailLeft, 0, 0, -1);
            TailUp.Visibility = Visibility.Visible;
            TailDown.Visibility = Visibility.Collapsed;
        }
        else
        {
            TailDown.Margin = new Thickness(tailLeft, -1, 0, 0);
            TailDown.Visibility = Visibility.Visible;
            TailUp.Visibility = Visibility.Collapsed;
        }

        // SWP_NOSIZE(0x1) | SWP_NOZORDER(0x4) | SWP_NOACTIVATE(0x10)
        Native.SetWindowPos(hwnd, IntPtr.Zero, leftPhys, topPhys, 0, 0, 0x1 | 0x4 | 0x10);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        _editing = !_editing;
        ResultBox.IsReadOnly = !_editing;
        EditBtn.Content = _editing ? "完成" : "编辑";
        if (_editing)
        {
            ResultBox.Focus();
            ResultBox.CaretIndex = ResultBox.Text.Length;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultBox.Text)) return;
        Clipboard.SetText(ResultBox.Text);
        CopyBtn.Content = "已复制";
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        t.Tick += (_, _) => { CopyBtn.Content = "复制"; t.Stop(); };
        t.Start();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
