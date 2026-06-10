using System.Windows;
using SkyTool.Common;
using SkyTool.Search;
using SkyTool.Snip;

namespace SkyTool;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowFx.Modernize(this);
        FileIndexService.Instance.StatusChanged += () =>
            Dispatcher.BeginInvoke(() => IndexStatus.Text = FileIndexService.Instance.Status);
        // 关闭 = 收进托盘
        Closing += (s, e) => { e.Cancel = true; Hide(); };
    }

    public void UpdateHotkeyLabels()
    {
        var app = (App)Application.Current;
        SnipKey.Text = $"{app.SnipHotkeyLabel} / {app.PinHotkeyLabel}";
        SearchKey.Text = app.SearchHotkeyLabel;
        MemoKey.Text = app.MemoHotkeyLabel;
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private void Snip_Click(object sender, RoutedEventArgs e)
    {
        Hide(); // 别把工具箱自己截进去
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, SnipWindow.Open);
    }

    private void Search_Click(object sender, RoutedEventArgs e)
        => ((App)Application.Current).Search.ShowAndFocus();

    private void Memo_Click(object sender, RoutedEventArgs e)
        => ((App)Application.Current).Memo.ToggleVisible();
}
