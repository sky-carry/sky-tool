using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SkyTool.Common;

namespace SkyTool.Memo;

/// <summary>钉在桌面的待办挂件：贴底层不挡窗口，可收起/展开，到点提醒。</summary>
public partial class MemoWindow : Window
{
    private readonly MemoStore _store = MemoStore.Instance;
    private readonly DispatcherTimer _reminderTimer;
    private DateTime _shownDate = DateTime.Today;

    public MemoWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => SendToBottom();
        Deactivated += (_, _) => SendToBottom();

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
                Left = SystemParameters.WorkArea.Right - Width - 24;
                Top = SystemParameters.WorkArea.Top + 60;
            }
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
        ((INotifyCollectionChanged)_store.Items).CollectionChanged += (_, _) => RefreshStats();
        foreach (var item in _store.Items)
            item.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RefreshStats);
        _store.Items.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (TodoItem it in e.NewItems)
                    it.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RefreshStats);
        };

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
        TitleText.Text = $"今日待办 · {DateTime.Today:M月d日} {culture.DateTimeFormat.GetAbbreviatedDayName(DateTime.Today.DayOfWeek)}";
        RefreshStats();
    }

    private void RefreshStats()
    {
        int total = _store.Items.Count;
        int done = _store.Items.Count(t => t.Done);
        CountText.Text = total == 0 ? "" : $"{done}/{total}";
        DoneStat.Text = total == 0 ? "" : $"已完成 {done} · 未完成 {total - done}";
        EmptyHint.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        CollapseBtn.Content = collapsed ? "▸" : "▾";
        _store.Settings.Collapsed = collapsed;
        _store.Save();
    }

    private void NewTaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddTask();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddTask();

    private void AddTask()
    {
        string text = NewTaskBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        DateTime? remindAt = null;
        string timeText = NewTimeBox.Text?.Trim();
        if (!string.IsNullOrEmpty(timeText))
        {
            if (TimeOnly.TryParseExact(timeText, new[] { "H:mm", "H:m", "HHmm" }, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time))
            {
                var dt = DateTime.Today.Add(time.ToTimeSpan());
                if (dt < DateTime.Now) dt = dt.AddDays(1); // 已过的时间点视为明天
                remindAt = dt;
            }
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

    public void ToggleVisible()
    {
        if (IsVisible) Hide();
        else { Show(); SendToBottom(); }
        _store.Settings.Visible = IsVisible;
        _store.Save();
    }
}
