# Pi 桌面（pi-desktop）

pi-web（本地 fork，含图片/视频背景切换）的**原生桌面壳**。双击桌面上的「Pi 桌面」图标，自动完成：启动 pi-web 服务 → 打开独立应用窗口 → 关窗自动停服务。

## 使用方法

1. 双击桌面上的 **「Pi 桌面」** 快捷方式（或 `bin\pi-desktop.exe`）
2. 等待几秒，窗口内出现 pi-web 界面（首次启动服务约 5~20 秒）
3. 需要 pi 命令行时，用 pi-web 界面自带的终端面板运行 `pi`
4. 关闭窗口 = 停止服务，无后台残留

### 背景切换（重点提醒）

背景文件保存在**应用自己的存储**里（`%LOCALAPPDATA%\PiDesktop\UserData`），和浏览器互不相通。

- **首次在 Pi 桌面里使用时，需要重新添加一次背景文件**（设置 → 背景 → 选择图片/视频）
- 之后刷新、重启 Pi 桌面，背景都会保留
- 在浏览器里设置过的背景不会自动出现在 Pi 桌面里（反之亦然）

### 重复打开

再开一个「Pi 桌面」不会重复启动服务，而是直接连到已运行的服务；关掉后来开的这个窗口不会停服务（只有"拥有"服务的那个窗口关闭时才会停）。

## 文件说明

| 路径 | 用途 |
|---|---|
| `bin\pi-desktop.exe` | 主程序（日常双击这个或桌面快捷方式） |
| `src\pi-desktop.cs` | 壳源码（C# WinForms + WebView2） |
| `build.cmd` | 重新构建（改代码后双击运行） |
| `assets\` | 图标资源（π 官方图标，7 个尺寸的 app.ico） |
| `tools\make-icon.js` | 图标生成脚本（换图标用） |

## 维护

### 改代码后重新构建

双击 `build.cmd`，产物在 `bin\pi-desktop.exe`（覆盖旧的）。桌面快捷方式指向该路径，无需重建快捷方式。

### 排查问题

- 运行日志：`%LOCALAPPDATA%\PiDesktop\pi-desktop.log`（服务输出）
- 崩溃日志：`%LOCALAPPDATA%\PiDesktop\crash.log`
- 服务起不来的常见原因：30141 端口被占用（杀掉旧进程即可，应用会自动复用健康的服务）

## 技术要点（为什么这么做）

- **不用 Electron**：WebView2 运行时 Win11 自带，壳只有 ~40KB，省 ~200MB 体积
- **DPI 感知（PerMonitorV2）**：`src\app.manifest` 声明逐监视器高 DPI 感知，避免 Windows 位图拉伸导致窗口/文字模糊（高分辨率屏必配）
- **显式指定 WebView2 运行时路径**：这台机器上运行时自动发现会挂起（联想软件干扰），代码里 `ResolveRuntimeDir()` 探测两个安装位置取最高版本
- **只复制 x64 版 WebView2Loader.dll**：x86 版同名会覆盖 x64 版导致 `BadImageFormatException`
- **进程树清理**：关闭窗口时 `taskkill /T` 结束 pi-web.js → next start 整棵树
- 服务启动命令：`node bin/pi-web.js --no-open`（fork 自带入口，`--no-open` 阻止它自己开浏览器）
