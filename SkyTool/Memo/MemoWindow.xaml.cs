using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SkyTool.Common;

namespace SkyTool.Memo;

/// <summary>钉在桌面的待办挂件：贴底层不挡窗口，可收起/展开，每个任务可单独设提醒时间。</summary>
public partial class MemoWindow : Window
{
    private readonly MemoStore _store = MemoStore.Instance;
    private readonly DispatcherTimer _reminderTimer;
    private DateTime _shownDate = DateTime.Today;
    private TodoItem _timeEditItem;

    public MemoWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => SendToBottom();
        Deactivated += (_, _) => SendToBottom();
        // 防误关（Alt+F4 等）：隐藏而不是销毁；应用退出时 WPF 会忽略取消，正常关闭
        Closing += (_, e) => { e.Cancel = true; Hide(); };

        Loaded += (_, _) =>
        {
            var s = _store.Settings;
            if (!double.IsNaN(s.Left) && !double.IsNaN(s.Top))
            {
                Left = s.Left;
                Top = s.Top;
            }
            else
            {
                ResetToDefaultPos();
            }
            EnsureOnScreen();
            if (s.Collapsed) SetCollapsed(true);
            RefreshView();
        };

        LocationChanged += (_, _) =>
        {
            _store.Settings.Left = Left;
            _store.Settings.Top = Top;
            _store.Save();
        };

        TaskList.ItemsSource = _store.Items;
        _store.Items.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (TodoItem it in e.NewItems)
                    it.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RefreshStats);
            RefreshStats();
        };
        foreach (var item in _store.Items)
            item.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RefreshStats);

        // 每 20 秒检查一次提醒 & 跨天刷新
        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _reminderTimer.Tick += (_, _) => Tick();
        _reminderTimer.Start();
    }

    private void SendToBottom()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void RefreshView()
    {
        var culture = new CultureInfo("zh-CN");
        DateText.Text = $"{DateTime.Today:M月d日} {culture.DateTimeFormat.GetAbbreviatedDayName(DateTime.Today.DayOfWeek)}";
        RefreshStats();
    }

    private void RefreshStats()
    {
        int total = _store.Items.Count;
        int done = _store.Items.Count(t => t.Done);
        CountText.Text = total == 0 ? "" : $"{done}/{total}";
        DoneStat.Text = total == 0 ? "" : $"已完成 {done} · 未完成 {total - done}";
        EmptyHint.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress(total, done);
    }

    private void UpdateProgress(int total, int done)
    {
        double w = ProgressTrack.ActualWidth;
        if (w <= 0) { Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => UpdateProgress(total, done)); return; }
        ProgressFill.Width = total == 0 ? 0 : w * done / total;
    }

    private void Tick()
    {
        // 跨天：刷新标题、清掉昨天已完成的任务（未完成的保留继续显示）
        if (DateTime.Today != _shownDate)
        {
            _shownDate = DateTime.Today;
            var doneOld = _store.Items.Where(t => t.Done && t.CreatedDate < DateTime.Today).ToList();
            foreach (var t in doneOld) _store.Items.Remove(t);
            RefreshView();
        }

        foreach (var t in _store.Items.Where(t =>
                     !t.Done && !t.Notified && t.RemindAt.HasValue && t.RemindAt <= DateTime.Now).ToList())
        {
            t.Notified = true;
            new ReminderWindow(t).Show();
        }
    }

    // ---------- 交互 ----------

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
        => SetCollapsed(Body.Visibility == Visibility.Visible);

    private void SetCollapsed(bool collapsed)
    {
        Body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseBtn.Content = collapsed ? "\uE70D" : "\uE70E"; // ChevronDown / ChevronUp
        _store.Settings.Collapsed = collapsed;
        _store.Save();
    }

    private void NewTaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddTask();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddTask();

    /// <summary>解析 "9:30" / "14:00" 这类时间，返回今天（已过则明天）的提醒时刻；解析失败返回 false。</summary>
    private static bool TryParseRemindTime(string text, out DateTime remindAt)
    {
        remindAt = default;
        if (!TimeOnly.TryParseExact(text, new[] { "H:mm", "H:m", "HHmm" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            return false;
        remindAt = DateTime.Today.Add(time.ToTimeSpan());
        if (remindAt < DateTime.Now) remindAt = remindAt.AddDays(1);
        return true;
    }

    private void AddTask()
    {
        string text = NewTaskBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        DateTime? remindAt = null;
        string timeText = NewTimeBox.Text?.Trim();
        if (!string.IsNullOrEmpty(timeText))
        {
            if (TryParseRemindTime(timeText, out var dt)) remindAt = dt;
            else
            {
                MessageBox.Show("提醒时间格式不对，请用 24 小时制，比如 9:30 或 14:00", "Sky 工具箱",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        _store.Items.Add(new TodoItem { Text = text, RemindAt = remindAt, CreatedDate = DateTime.Today });
        NewTaskBox.Clear();
        NewTimeBox.Clear();
        NewTaskBox.Focus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TodoItem item)
            _store.Items.Remove(item);
    }

    private void ClearDone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _store.Items.Where(t => t.Done).ToList())
            _store.Items.Remove(t);
    }

    // ---------- 每任务时间编辑 ----------

    private bool _timeListInit;
    private bool _suppressTimeSel;

    private void EnsureTimeLists()
    {
        if (_timeListInit) return;
        for (int h = 0; h < 24; h++) HourList.Items.Add(h.ToString("00"));
        for (int m = 0; m < 60; m += 5) MinList.Items.Add(m.ToString("00"));
        _timeListInit = true;
    }

    private void Clock_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not TodoItem item) return;
        EnsureTimeLists();
        _timeEditItem = item;

        // 默认选中：任务已有提醒则用它，否则用当前时间的下一个整点附近
        var t = item.RemindAt ?? DateTime.Now.AddMinutes(30);
        _suppressTimeSel = true;
        HourList.SelectedIndex = t.Hour;
        MinList.SelectedIndex = Math.Clamp(t.Minute / 5, 0, MinList.Items.Count - 1);
        _suppressTimeSel = false;
        UpdateTimePreview();

        TimePopup.PlacementTarget = (UIElement)sender;
        TimePopup.IsOpen = true;
        HourList.ScrollIntoView(HourList.SelectedItem);
        MinList.ScrollIntoView(MinList.SelectedItem);
    }

    private void TimeSel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTimeSel) return;
        UpdateTimePreview();
    }

    private void UpdateTimePreview()
    {
        if (HourList.SelectedItem == null || MinList.SelectedItem == null) { TimePreview.Text = ""; return; }
        TimePreview.Text = $"{HourList.SelectedItem} : {MinList.SelectedItem}";
    }

    private void TimePreset_Click(object sender, RoutedEventArgs e)
    {
        var parts = ((string)((FrameworkElement)sender).Tag).Split(':');
        _suppressTimeSel = true;
        HourList.SelectedIndex = int.Parse(parts[0]);
        MinList.SelectedIndex = int.Parse(parts[1]) / 5;
        _suppressTimeSel = false;
        UpdateTimePreview();
        HourList.ScrollIntoView(HourList.SelectedItem);
    }

    private void TimeApply_Click(object sender, RoutedEventArgs e)
    {
        if (_timeEditItem == null || HourList.SelectedItem == null || MinList.SelectedItem == null)
        {
            TimePopup.IsOpen = false;
            return;
        }
        int h = int.Parse((string)HourList.SelectedItem);
        int m = int.Parse((string)MinList.SelectedItem);
        var dt = DateTime.Today.AddHours(h).AddMinutes(m);
        if (dt < DateTime.Now) dt = dt.AddDays(1); // 已过的时间点算到明天
        _timeEditItem.RemindAt = dt;
        _timeEditItem.Notified = false;            // 重设时间后重新生效
        TimePopup.IsOpen = false;
    }

    private void TimeClear_Click(object sender, RoutedEventArgs e)
    {
        if (_timeEditItem != null) _timeEditItem.RemindAt = null;
        TimePopup.IsOpen = false;
    }

    public void ToggleVisible()
    {
        if (IsVisible) Hide();
        else { Show(); EnsureOnScreen(); SendToBottom(); }
        _store.Settings.Visible = IsVisible;
        _store.Save();
    }

    private void ResetToDefaultPos()
    {
        Left = SystemParameters.WorkArea.Right - Width - 16; // 主屏右上角
        Top = SystemParameters.WorkArea.Top + 48;
    }

    /// <summary>挂件若不在主显示器上（如曾拖到副屏或屏幕外），自动拉回主屏右上角，避免"点了不见"。
    /// 用 Win32 按物理位置判断当前所在显示器，复位则用 WPF 主屏工作区坐标，规避混合 DPI 下的坐标歧义。</summary>
    private void EnsureOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return; // 窗口句柄未就绪
        var mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var primary = Native.MonitorFromPoint(new Native.POINT { X = 0, Y = 0 }, Native.MONITOR_DEFAULTTOPRIMARY);
        if (mon != primary)
        {
            ResetToDefaultPos();
            _store.Settings.Left = Left;
            _store.Settings.Top = Top;
            _store.Save();
        }
    }
}
