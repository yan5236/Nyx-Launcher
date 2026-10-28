# Nyx Launcher

使用 C#、.NET 10 和 WinUI 3 重新构建的 Windows 启动器。

当前仅包含应用入口、桌面主窗口和首页，尚未接入账号、游戏下载或游戏启动逻辑。

## 项目结构

- `NyxLauncher.slnx`：解决方案。
- `src/NyxLauncher/App.xaml.cs`：应用生命周期与主窗口创建。
- `src/NyxLauncher/MainWindow.xaml`：标题栏与页面容器。
- `src/NyxLauncher/MainPage.xaml`：启动器首页。

## 开发环境

- Windows x64。项目的 Windows API 最低版本设为 10.0.17763.0；其他系统版本尚未实机验证。
- .NET SDK 10.0.403（通过 `global.json` 固定，允许同功能带补丁升级）。
- Windows App SDK 2.6.4（包含 WinUI 3），Windows SDK BuildTools 10.0.28000.2705，通过 NuGet 还原。

本机已在 `.dotnet/` 中准备独立 SDK，该目录不会纳入 Git。其他开发机需自行安装匹配的 .NET SDK。

## 构建与运行

在项目根目录执行 PowerShell 命令：

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.local\dotnet-home'
.\.dotnet\dotnet.exe build NyxLauncher.slnx -c Debug
.\.dotnet\dotnet.exe run --project src\NyxLauncher\NyxLauncher.csproj -c Debug
```

若系统已安装匹配的 SDK，可将 `.\.dotnet\dotnet.exe` 替换为 `dotnet`。

项目采用无 MSIX 包的桌面运行方式，并将 .NET 与 Windows App SDK 运行时复制到输出目录。首次构建需要联网还原依赖；运行不需要开发者模式或单独安装这些运行时。请保留完整输出目录，不能只复制 EXE。

当前未配置安装包、自动更新或代码签名。应用图标沿用微软官方空白模板，后续替换为项目品牌资源。

## 验证记录

2026-10-28，在本机 Windows 10 x64（内部版本 19044）完成：

- Debug 构建通过：0 个警告、0 个错误。
- 窗口启动检查通过：主窗口标题为 `NyxLauncher`，UI 自动化能读取首页中文状态文本；随后正常关闭测试窗口。
- XML / JSON 语法与 Git 差异空白检查通过。

未验证其他 Windows 版本、其他 CPU 架构或安装包发布。
