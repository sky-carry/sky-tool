# Sky 工具箱

Windows 桌面小工具箱（WPF / .NET 8），常驻系统托盘，三大功能：

| 功能 | 说明 | 默认热键 |
|---|---|---|
| ✂ 截图 | 框选截屏 → 标注 → 复制 / 保存 / 固定 | `F1`（被占用时自动改用 `Ctrl+Alt+A`） |
| 📍 贴图 | 截图中按下 = 固定当前选区；平时按下 = 把剪贴板图片钉到屏幕 | `F3`（被占用时 `Ctrl+Alt+P`） |
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

## 两个版本（Edition）

同一套源码用 `-p:Edition=` 切换产出两个版本：

| 版本 | Edition | OCR | TFM | 备注 |
|---|---|---|---|---|
| 极简版 | `Lite` | 无 | `net8.0-windows` | 精简包仅 ~0.3MB，主推、永久免费 |
| Pro 版 | `Pro`（默认） | 有 | `net8.0-windows10.0.19041.0` | 含文字提取(OCR)及后续高级功能 |

`Edition=Pro` 定义 `OCR` 编译常量并启用 Windows SDK 投影；`Edition=Lite` 排除 `OcrUtil.cs` /
`OcrResultWindow`，OCR 按钮在截图工具栏里自动隐藏。本地 `dotnet build/run` 默认 Pro（OCR 可用）。

## 发布新版本（更新流程）

程序内置「检查更新」：拉取下载站清单比对版本，发现新版可一键下载、校验 SHA256、改名替换正在运行的
exe 并自动重启。**Pro 查 `latest.json`、极简版查 `latest-lite.json`，互不串包。** 发版步骤：

1. **改版本号**：`SkyTool.csproj` 的 `<Version>`（如 `1.2.0`）。
2. **发布 4 份 exe**（每版各「自包含/精简」两种包）：
   ```powershell
   $p="SkyTool/SkyTool.csproj"; $o="publish/dist"
   # Pro 自包含 / 精简
   dotnet publish $p -c Release -r win-x64 -p:Edition=Pro  --self-contained true  -p:PublishSingleFile=true -o publish/pro-full
   dotnet publish $p -c Release -r win-x64 -p:Edition=Pro  --self-contained false -p:PublishSingleFile=true -o publish/pro-rt
   # 极简版 自包含 / 精简
   dotnet publish $p -c Release -r win-x64 -p:Edition=Lite --self-contained true  -p:PublishSingleFile=true -o publish/mini-full
   dotnet publish $p -c Release -r win-x64 -p:Edition=Lite --self-contained false -p:PublishSingleFile=true -o publish/mini-rt
   # 收集并改名：SkyTool-Pro(.exe/-rt.exe)、SkyTool-Mini(.exe/-rt.exe)
   ```
3. **算两个自包含包的 SHA256**（清单只指向自包含包，对两种包用户都通用）：
   `(Get-FileHash publish/dist/SkyTool-Pro.exe -Algorithm SHA256).Hash.ToLower()`，Mini 同理。
4. **更新清单与页面**：版本号 / SHA256 / notes / date 填进 `web/latest.json`（Pro）和
   `web/latest-lite.json`（极简版），并在 `web/index.html` 顶部加一条更新日志；下载体积有变一并更新。
5. **上传到下载站** `/home/code/sky-tool/`（服务器 `myserver`，`http://124.223.55.175/sky-tool/`）：
   `SkyTool-Pro.exe`、`SkyTool-Pro-rt.exe`、`SkyTool-Mini.exe`、`SkyTool-Mini-rt.exe`、
   `latest.json`、`latest-lite.json`、`index.html`。

> 历史 1.0.0 用户仍查 `latest.json`（被视为 Pro，自动升级到 Pro 自包含包）。
> 清单里 version 比本地 `<Version>` 更大才会触发更新提示。

## 功能细节

### 文件搜索
- **管理员模式（推荐）**：直接枚举 NTFS 主文件表（`FSCTL_ENUM_USN_DATA`）秒级建立全盘索引，
  并通过 USN 变更日志实时增量更新（新建/删除/重命名立即反映）—— 与 Everything 同原理。
- **普通模式**：自动降级为后台目录遍历建索引，无需管理员，但首次扫描需要几分钟；
  搜索窗口里有“以管理员身份重启”按钮可一键切换到极速模式。
- 多关键词空格分隔（AND 匹配），结果双击打开，右键可打开所在文件夹 / 复制路径。

### 截图 / 贴图（Snipaste 式）
- `F1` 呼出全屏蒙层；**鼠标悬停自动吸附窗口边框**（DWM 真实边界），单击即选中整个窗口，也可拖拽手动框选，实时显示尺寸。
- **双击选区内**：复制图片并关闭（Snipaste 行为）；**双击选区外 / `Esc`**：取消。
- **右键**：已框选时退回重新框选，未框选时退出。
- 标注工具：矩形、椭圆、箭头、画笔、文字、马赛克；6 种颜色、3 档粗细；`Ctrl+Z` 撤销。
- 输出：`Enter`/✔ 复制到剪贴板，`F3`/📍 固定到屏幕（置顶悬浮，可拖动、滚轮缩放、中键或 Esc 关闭），
  `Ctrl+S` 保存 PNG/JPG。
- 全局 `F3`：不在截图时按下，把剪贴板里的图片直接钉到光标处（Snipaste 的"贴图"）。

### 桌面备忘录（Microsoft To Do 风格）
- 挂件钉在桌面层（不遮挡其他窗口），可拖动，位置记忆，标题栏按钮收起/展开，带完成进度条。
- 输入任务 + 可选提醒时间（如 `9:30`、`14:00`，已过的时间点自动算到明天）。
- **每个任务可单独设置/修改提醒时间**：悬停任务行点时钟图标，弹出小面板改时间或清除。
- 到点弹窗提醒 + 提示音，可一键“完成”“5 分钟后再提醒”。
- 圆形勾选框，完成后变**绿色对勾** + 划线置灰；隔天自动清掉昨日已完成项，未完成的保留。
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

## 已知限制

- 混合 DPI 多显示器下，副屏上的截图框选可能有轻微偏移。
- 普通模式索引不会实时更新（管理员模式会）。
- 文件索引按需构建、空闲约 3 分钟后释放：久未搜索后第一次搜索需等待重建（管理员 MFT 秒级，普通模式较久）。
- 开机自启尚未内置，可手动把快捷方式放进 `shell:startup`。
