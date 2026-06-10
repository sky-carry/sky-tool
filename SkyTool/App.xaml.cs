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

    public MainWindow MainWin { get; private set; }
    public SearchWindow Search { get; private set; }
    public MemoWindow Memo { get; private set; }

    public string SnipHotkeyLabel { get; private set; } = "未注册";
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

        // 备忘录数据
        MemoStore.Instance.Load();

        // 文件索引后台启动
        FileIndexService.Instance.Start();

        // 窗口
        MainWin = new MainWindow();
        Search = new SearchWindow();
        Memo = new MemoWindow();
        if (MemoStore.Instance.Settings.Visible) Memo.Show();
        MainWin.Show();

        // 全局热键（挂在主窗口）
        _hotkeys = new HotkeyManager();
        _hotkeys.Attach(MainWin);

        SnipHotkeyLabel =
            _hotkeys.Register(0, 0x70 /*F1*/, "F1", SnipWindow.Open) ??
            _hotkeys.Register(Native.MOD_CONTROL | Native.MOD_ALT, 0x41 /*A*/, "Ctrl+Alt+A", SnipWindow.Open) ??
            "未注册";
        SearchHotkeyLabel =
            _hotkeys.Register(Native.MOD_CONTROL | Native.MOD_ALT, 0x46 /*F*/, "Ctrl+Alt+F", () => Search.ShowAndFocus()) ??
            "未注册";
        MemoHotkeyLabel =
            _hotkeys.Register(Native.MOD_CONTROL | Native.MOD_ALT, 0x4D /*M*/, "Ctrl+Alt+M", () => Memo.ToggleVisible()) ??
            "未注册";

        MainWin.UpdateHotkeyLabels();

        SetupTray();
    }

    private void SetupTray()
    {
        _tray = new WF.NotifyIcon
        {
            Icon = SD.SystemIcons.Application,
            Text = "Sky 工具箱",
            Visible = true,
        };
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("打开工具箱", null, (_, _) => Dispatcher.Invoke(ShowMain));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add($"截图（{SnipHotkeyLabel}）", null, (_, _) => Dispatcher.Invoke(SnipWindow.Open));
        menu.Items.Add($"文件搜索（{SearchHotkeyLabel}）", null, (_, _) => Dispatcher.Invoke(() => Search.ShowAndFocus()));
        menu.Items.Add($"备忘录（{MemoHotkeyLabel}）", null, (_, _) => Dispatcher.Invoke(() => Memo.ToggleVisible()));
        menu.Items.Add(new WF.ToolStripSeparator());
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

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }
}
