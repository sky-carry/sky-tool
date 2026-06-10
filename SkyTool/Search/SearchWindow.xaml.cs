using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SkyTool.Search;

public partial class SearchWindow : Window
{
    public class ResultRow
    {
        public string Icon { get; set; }
        public string Name { get; set; }
        public string FullPath { get; set; }
    }

    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource _queryCts;

    public SearchWindow()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunQuery(); };

        var svc = FileIndexService.Instance;
        svc.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        Loaded += (_, _) =>
        {
            UpdateStatus();
            AdminBanner.Visibility = svc.IsAdminMode ? Visibility.Collapsed : Visibility.Visible;
            SearchBox.Focus();
        };
        // 关闭时隐藏而不是销毁，索引常驻
        Closing += (s, e) => { e.Cancel = true; Hide(); };
    }

    public void ShowAndFocus()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void UpdateStatus()
    {
        var svc = FileIndexService.Instance;
        StatusText.Text = svc.Status;
        AdminBanner.Visibility = svc.IsAdminMode ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private async void RunQuery()
    {
        _queryCts?.Cancel();
        var cts = _queryCts = new CancellationTokenSource();
        string text = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ResultList.ItemsSource = null;
            UpdateStatus();
            return;
        }

        var sw = Stopwatch.StartNew();
        List<SearchResult> results;
        try
        {
            results = await Task.Run(() => FileIndexService.Instance.Query(text, 500, cts.Token));
        }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;

        ResultList.ItemsSource = results.Select(r => new ResultRow
        {
            Icon = r.IsDir ? "📁" : "📄",
            Name = r.Name,
            FullPath = r.FullPath,
        }).ToList();
        StatusText.Text = $"{results.Count} 条结果（上限 500） · 用时 {sw.ElapsedMilliseconds} ms · 索引 {FileIndexService.Instance.TotalCount:N0} 项";
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && ResultList.Items.Count > 0)
        {
            ResultList.SelectedIndex = 0;
            ((ListViewItem)ResultList.ItemContainerGenerator.ContainerFromIndex(0))?.Focus();
        }
        else if (e.Key == Key.Enter && ResultList.Items.Count > 0)
        {
            OpenRow(ResultList.SelectedItem as ResultRow ?? ResultList.Items[0] as ResultRow);
        }
        else if (e.Key == Key.Escape)
        {
            Hide();
        }
    }

    private void ResultList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OpenRow(ResultList.SelectedItem as ResultRow);
        else if (e.Key == Key.Escape) Hide();
    }

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => OpenRow(ResultList.SelectedItem as ResultRow);

    private ResultRow Selected => ResultList.SelectedItem as ResultRow;

    private static void OpenRow(ResultRow row)
    {
        if (row == null) return;
        try { Process.Start(new ProcessStartInfo(row.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"无法打开：{ex.Message}", "Sky 工具箱"); }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenRow(Selected);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        try
        {
            if (File.Exists(Selected.FullPath) || Directory.Exists(Selected.FullPath))
                Process.Start("explorer.exe", $"/select,\"{Selected.FullPath}\"");
        }
        catch { /* ignore */ }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (Selected != null) Clipboard.SetText(Selected.FullPath);
    }

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch { /* 用户取消了 UAC */ }
    }
}
