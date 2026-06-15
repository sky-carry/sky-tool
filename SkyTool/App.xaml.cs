using System.Windows;
using SkyTool.Common;
using SkyTool.Memo;
using SkyTool.Search;
using SkyTool.Snip;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace SkyTool;

public partial class App : Application
{
    private Mutex _mutex;
    private WF.NotifyIcon _tray;
    private HotkeyManager _hotkeys;
    private System.Windows.Threading.DispatcherTimer _hotkeyRetry;
    private bool _snipReg, _pinReg, _searchReg, _memoReg, _conflictNotified;

    /// <summary>应用图标（WPF 窗口用）。</summary>
    public static System.Windows.Media.ImageSource AppIcon { get; } =
        System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri("pack://application:,,,/app.ico", UriKind.Absolute));

    public MainWindow MainWin { get; private set; }
    public SearchWindow Search { get; private set; }
    public MemoWindow Memo { get; private set; }

    public string SnipHotkeyLabel { get; private set; } = "未注册";
    public string PinHotkeyLabel { get; private set; } = "未注册";
    public string SearchHotkeyLabel { get; private set; } = "未注册";
    public string MemoHotkeyLabel { get; private set; } = "未注册";

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "SkyTool_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Sky 工具箱已经在运行了（看看系统托盘）。", "Sky 工具箱");
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 全局兜底：单个功能出错记日志提示，不让整个工具箱闪退
        DispatcherUnhandledException += (_, ex) =>
        {
            LogError(ex.Exception);
            MessageBox.Show($"出了点小问题，已记录日志：\n{ex.Exception.Message}", "Sky 工具箱",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogError(ex.ExceptionObject as Exception);

        // 备忘录数据
        MemoStore.Instance.Load();

        // 文件索引后台启动
        FileIndexService.Instance.Start();

        // 窗口
        MainWin = new MainWindow { Icon = AppIcon };
        Search = new SearchWindow { Icon = AppIcon };
        Memo = new MemoWindow();
        if (MemoStore.Instance.Settings.Visible) Memo.Show();
        MainWin.Show();

        // 全局热键（挂在主窗口）。固定 F1=截图、F3=贴图、Ctrl+Alt+F=搜索、Ctrl+Alt+M=备忘录。
        _hotkeys = new HotkeyManager();
        _hotkeys.Attach(MainWin);

        SnipHotkeyLabel = "F1";
        PinHotkeyLabel = "F3";
        SearchHotkeyLabel = "Ctrl+Alt+F";
        MemoHotkeyLabel = "Ctrl+Alt+M";
        MainWin.UpdateHotkeyLabels();

        SetupTray();
        TryRegisterHotkeys();

        // 清理上次更新遗留的旧 exe；启动后稍等再静默检查更新（每天至多一次）
        UpdateService.CleanupOld();
        var updTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        updTimer.Tick += (_, _) =>
        {
            updTimer.Stop();
            if (UpdateService.ShouldAutoCheck()) CheckForUpdates(silent: true);
        };
        updTimer.Start();
    }

    /// <summary>检查更新。silent=true 时只有发现新版才打扰用户（用于每日自动检查）。</summary>
    private async void CheckForUpdates(bool silent)
    {
        var info = await UpdateService.FetchAsync();
        if (info == null)
        {
            if (!silent)
                _tray?.ShowBalloonTip(2500, "Sky 工具箱", "检查更新失败，请检查网络后再试。", WF.ToolTipIcon.Warning);
            return;
        }
        if (UpdateService.IsNewer(info.Version))
        {
            var win = new Update.UpdateWindow(info) { Icon = AppIcon };
            win.Show();
            win.Activate();
        }
        else if (!silent)
        {
            _tray?.ShowBalloonTip(2500, "Sky 工具箱",
                $"当前已是最新版本（v{UpdateService.CurrentVersion}）。", WF.ToolTipIcon.Info);
        }
    }

    /// <summary>
    /// 注册全局热键（固定 F1/F3 等，无备用键）。若某个键被其他程序占用（如 Snipaste），
    /// 会每 3 秒自动重试，等占用程序关闭后立即接管，无需重启工具箱。
    /// </summary>
    private void TryRegisterHotkeys()
    {
        if (!_snipReg && _hotkeys.Register(0, 0x70 /*F1*/, "F1", SnipWindow.Open) != null) _snipReg = true;
        if (!_pinReg && _hotkeys.Register(0, 0x72 /*F3*/, "F3", PinAction) != null) _pinReg = true;
        if (!_searchReg && _hotkeys.Register(Native.MOD_CONTROL | Native.MOD_ALT, 0x46 /*F*/, "Ctrl+Alt+F", () => Search.ShowAndFocus()) != null) _searchReg = true;
        if (!_memoReg && _hotkeys.Register(Native.MOD_CONTROL | Native.MOD_ALT, 0x4D /*M*/, "Ctrl+Alt+M", () => Memo.ToggleVisible()) != null) _memoReg = true;

        if (_snipReg && _pinReg && _searchReg && _memoReg)
        {
            _hotkeyRetry?.Stop();
            return;
        }

        // 还有热键没抢到：第一次提示一下，并启动自动重试
        if (!_conflictNotified && (!_snipReg || !_pinReg))
        {
            _conflictNotified = true;
            _tray?.ShowBalloonTip(4000, "Sky 工具箱",
                "F1/F3 暂被其他程序占用（如 Snipaste）。关闭它后会自动生效，无需重启。", WF.ToolTipIcon.Warning);
        }
        if (_hotkeyRetry == null)
        {
            _hotkeyRetry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _hotkeyRetry.Tick += (_, _) => TryRegisterHotkeys();
            _hotkeyRetry.Start();
        }
    }

    /// <summary>全局 F3：截图中 → 固定当前选区；平时 → 把剪贴板里的图片钉到屏幕（光标处）。</summary>
    private void PinAction()
    {
        if (SnipWindow.PinCurrent()) return;

        if (Clipboard.ContainsImage())
        {
            var img = Clipboard.GetImage();
            if (img != null)
            {
                Native.GetCursorPos(out var pt);
                new PinWindow(img, pt.X, pt.Y).Show();
                return;
            }
        }
        _tray?.ShowBalloonTip(1500, "Sky 工具箱", "剪贴板里没有图片，先截一张（F1）或复制图片再按 F3。", WF.ToolTipIcon.Info);
    }

    private static SD.Icon LoadTrayIcon()
    {
        try
        {
            var info = GetResourceStream(new Uri("app.ico", UriKind.Relative));
            if (info != null)
            {
                using var stream = info.Stream;
                return new SD.Icon(stream, new SD.Size(32, 32));
            }
        }
        catch { /* 资源缺失时退回系统图标 */ }
        return SD.SystemIcons.Application;
    }

    private void SetupTray()
    {
        _tray = new WF.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Sky 工具箱",
            Visible = true,
        };
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("打开工具箱", null, (_, _) => Dispatcher.Invoke(ShowMain));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add($"截图（{SnipHotkeyLabel}）", null, (_, _) => Dispatcher.Invoke(SnipWindow.Open));
        menu.Items.Add($"贴剪贴板图片（{PinHotkeyLabel}）", null, (_, _) => Dispatcher.Invoke(PinAction));
        menu.Items.Add($"文件搜索（{SearchHotkeyLabel}）", null, (_, _) => Dispatcher.Invoke(() => Search.ShowAndFocus()));
        menu.Items.Add($"备忘录（{MemoHotkeyLabel}）", null, (_, _) => Dispatcher.Invoke(() => Memo.ToggleVisible()));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("检查更新", null, (_, _) => Dispatcher.Invoke(() => CheckForUpdates(silent: false)));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMain);
    }

    private void ShowMain()
    {
        MainWin.Show();
        if (MainWin.WindowState == WindowState.Minimized) MainWin.WindowState = WindowState.Normal;
        MainWin.Activate();
    }

    public void ExitApp()
    {
        FileIndexService.Instance.Stop();
        MemoStore.Instance.Save();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _hotkeys?.Dispose();
        Shutdown();
    }

    private static void LogError(Exception ex)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyTool");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* 日志失败就算了 */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }
}
