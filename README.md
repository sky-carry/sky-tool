# Sky 工具箱

Windows 桌面小工具箱（WPF / .NET 8），常驻系统托盘，三大功能：

| 功能 | 说明 | 默认热键 |
|---|---|---|
| ✂ 截图 / 贴图 | 框选截屏 → 标注 → 贴图置顶 / 复制 / 保存 | `F1`（被占用时自动改用 `Ctrl+Alt+A`） |
| 🔍 文件搜索 | 全盘文件名即时搜索（Everything 同原理） | `Ctrl+Alt+F` |
| 📌 桌面备忘录 | 钉在桌面的今日待办，可定时提醒 | `Ctrl+Alt+M`（显示/隐藏） |

## 构建与运行

```powershell
cd SkyTool
dotnet build          # 调试构建
dotnet run            # 直接运行

# 发布单文件版本
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

需要 .NET 8 SDK（运行只需 .NET 8 Desktop Runtime）。

## 功能细节

### 文件搜索
- **管理员模式（推荐）**：直接枚举 NTFS 主文件表（`FSCTL_ENUM_USN_DATA`）秒级建立全盘索引，
  并通过 USN 变更日志实时增量更新（新建/删除/重命名立即反映）—— 与 Everything 同原理。
- **普通模式**：自动降级为后台目录遍历建索引，无需管理员，但首次扫描需要几分钟；
  搜索窗口里有“以管理员身份重启”按钮可一键切换到极速模式。
- 多关键词空格分隔（AND 匹配），结果双击打开，右键可打开所在文件夹 / 复制路径。

### 截图 / 贴图（Snipaste 式）
- 热键呼出全屏蒙层，拖拽框选，实时显示尺寸。
- 标注工具：矩形、椭圆、箭头、画笔、文字、马赛克；6 种颜色、3 档粗细；`Ctrl+Z` 撤销。
- 输出：`Enter`/✔ 复制到剪贴板，`F3`/📌 贴图（置顶悬浮，可拖动、滚轮缩放、中键或 Esc 关闭），
  `Ctrl+S`/💾 保存 PNG/JPG，`Esc` 取消。

### 桌面备忘录
- 挂件钉在桌面层（不遮挡其他窗口），可拖动，位置记忆，标题栏按钮收起/展开。
- 输入任务 + 可选提醒时间（如 `9:30`、`14:00`，已过的时间点自动算到明天）。
- 到点弹窗提醒 + 提示音，可一键“完成”或“5 分钟后再提醒”。
- 任务可勾选完成（划线显示）、删除、一键清除已完成；隔天自动清掉昨日已完成项，未完成的保留。
- 数据存在 `%AppData%\SkyTool\tasks.json`。

## 项目结构

```
SkyTool/
├── App.xaml(.cs)            # 启动、托盘、全局热键、单实例
├── MainWindow.xaml(.cs)     # 工具箱主面板
├── Common/
│   ├── Native.cs            # Win32 P/Invoke（USN/热键/窗口 Z 序）
│   └── HotkeyManager.cs     # 全局热键注册分发
├── Search/
│   ├── FileIndexService.cs  # MFT 枚举 + USN 实时监听 + 降级扫描 + 查询
│   └── SearchWindow.xaml    # 搜索界面
├── Snip/
│   ├── ScreenCapture.cs     # 虚拟屏幕捕获
│   ├── SnipWindow.xaml      # 框选 + 标注
│   └── PinWindow.xaml       # 贴图悬浮窗
└── Memo/
    ├── MemoStore.cs         # 待办模型与持久化
    ├── MemoWindow.xaml      # 桌面挂件
    └── ReminderWindow.xaml  # 到点提醒弹窗
```

## 已知限制（v0.1）

- 混合 DPI 多显示器下，副屏上的截图框选可能有轻微偏移。
- 截图选区拖完后不能再调整边缘（需重新框选）。
- 普通模式索引不会实时更新（管理员模式会）。
- 开机自启尚未内置，可手动把快捷方式放进 `shell:startup`。
