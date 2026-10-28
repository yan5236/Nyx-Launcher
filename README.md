# Nyx Launcher

使用 C#、.NET 10 和 WinUI 3 重新构建的 Windows 启动器。

当前为纯界面原型，包含首页、版本管理、下载，以及从左下角账户弹层进入的账户和设置页。使用原生 WinUI 3 控件，不包含 WebView。

侧栏仅放导航与底部头像、用户名，不显示应用图标或应用名。界面使用浅色主题，正文 15、导航 16；各页面不再显示页面标题，减少副标题和描述性文本。账户菜单从上到下为“账户”、分隔线、“设置”。

下载页顶部为“游戏、模组、整合包、资源包、数据包、光影”六个分类 Tab，下方是搜索框与类型筛选（全部类型、正式版、快照版、远古版、愚人节版）。各分类的版本演示统一为列表：左侧图标，右侧显示版本名称和发布时间，行尾显示版本类型。

版本号、Steve 账户和头像都是布局示例，不代表真实安装或登录状态。页面切换、Tab 切换、搜索、类型筛选、列表选择与版本下拉可操作；启动、下载、导入、账户切换和设置均未接入业务，相关按钮禁用。

## 项目结构

- `NyxLauncher.slnx`：解决方案。
- `src/NyxLauncher/App.xaml.cs`：应用生命周期与主窗口创建。
- `src/NyxLauncher/MainWindow.xaml`：标题栏与页面容器。
- `src/NyxLauncher/MainPage.xaml`：侧栏、账户菜单与页面切换容器。
- `src/NyxLauncher/Views/`：首页、版本管理与下载界面。
- `src/NyxLauncher/Assets/`：图标和本地 SVG 原型插图。
- `src/NyxLauncher/Views/DownloadCatalog.cs`、`DownloadEntry.cs`：下载页各分类的静态演示数据。
- `scripts/Test-Prototype.ps1`：导航、弹层顺序、页面标题移除与下载页结构的静态检查。

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

- 初始工程提交为 `4062b38`，浅色原型界面提交为 `0308b8c`。
- Debug 构建通过：0 个警告、0 个错误。
- `pwsh -File scripts/Test-Prototype.ps1` 静态检查通过：导航、账户弹层顺序、页面标题移除、下载页 Tab/搜索/筛选/列表结构与图片资源。
- 启动实际窗口并通过 Windows UI Automation 驱动验收：
  - 首页、版本管理、账户、设置页均无页面标题；首页、版本管理、账户、设置截图核验通过。
  - 下载页六个 Tab 均可切换，列表条目数分别为 7、6、6、6、5、6，搜索占位文本随分类更新。
  - 搜索与类型筛选生效：游戏分类按全部/正式/快照/远古/愚人节筛选得到 7/3/1/2/1 条；“24w”检索得到 `24w14potato`，清空后恢复 7 条；无结果时显示“没有匹配的内容”。
- XML 语法与 Git 差异空白检查通过。

未验证其他 Windows 版本、其他 CPU 架构或安装包发布。
