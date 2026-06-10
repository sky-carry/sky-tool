using System.Media;
using System.Windows;

namespace SkyTool.Memo;

/// <summary>到点提醒弹窗（右下角，置顶）。</summary>
public partial class ReminderWindow : Window
{
    private readonly TodoItem _item;
    private static int _stack; // 多个提醒同时弹时往上叠

    public ReminderWindow(TodoItem item)
    {
        InitializeComponent();
        _item = item;
        TaskText.Text = item.Text;
        TimeText.Text = $"提醒时间 {item.RemindAt:HH:mm}";

        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 16;
            Top = wa.Bottom - ActualHeight - 16 - _stack * (ActualHeight + 10);
            _stack++;
            SystemSounds.Exclamation.Play();
        };
        Closed += (_, _) => _stack = Math.Max(0, _stack - 1);
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _item.Done = true;
        Close();
    }

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        _item.RemindAt = DateTime.Now.AddMinutes(5);
        _item.Notified = false;
        Close();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Close();
}
