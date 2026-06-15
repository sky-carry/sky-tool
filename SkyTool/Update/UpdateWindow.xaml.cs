using System.Windows;
using SkyTool.Common;

namespace SkyTool.Update;

/// <summary>“发现新版本”对话框：展示更新内容，一键下载校验并替换重启。</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly System.Threading.CancellationTokenSource _cts = new();

    public UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        TitleText.Text = $"发现新版本 v{info.Version}";
        SubText.Text = string.IsNullOrWhiteSpace(info.Date)
            ? $"当前版本 v{UpdateService.CurrentVersion}"
            : $"当前版本 v{UpdateService.CurrentVersion}　·　发布于 {info.Date}";
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes) ? "（本次更新无说明）" : info.Notes;

        SourceInitialized += (_, _) => WindowFx.Modernize(this);
        Closed += (_, _) => _cts.Cancel();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        BtnRow.Visibility = Visibility.Collapsed;
        ProgressRow.Visibility = Visibility.Visible;

        var progress = new Progress<double>(p =>
        {
            Bar.Value = p * 100;
            PercentText.Text = $"{p * 100:0}%";
        });

        try
        {
            string newExe = await UpdateService.DownloadAsync(_info, progress, _cts.Token);
            StatusText.Text = "下载完成，正在重启…";
            UpdateService.ApplyAndRestart(newExe); // 成功则进程退出、新版启动
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"更新失败：{ex.Message}\n你也可以到下载页手动更新。", "Sky 工具箱",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ProgressRow.Visibility = Visibility.Collapsed;
            BtnRow.Visibility = Visibility.Visible;
        }
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();
}
