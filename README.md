# Nyx Launcher

使用 C#、.NET 10 和 WinUI 3 重新构建的 Windows 启动器。

当前已接入真实 Java 游戏版本清单，包含首页、版本管理、下载，以及从左下角账户弹层进入的账户和设置页。使用原生 WinUI 3 控件，不包含 WebView。

侧栏仅放导航与底部头像、用户名，不显示应用图标或应用名。界面使用浅色主题，正文 15、导航 16；各页面不再显示页面标题，减少副标题和描述性文本。账户菜单从上到下为“账户”、分隔线、“设置”。

下载页顶部为“游戏、模组、整合包、资源包、数据包、光影”六个分类 Tab，下方是搜索框与类型筛选（全部类型、正式版、快照版、远古版、愚人节版）。游戏分类从 Mojang / BMCLAPI 获取真实版本；其他分类保留明确标注的演示数据。列表左侧为图标，右侧为版本名称与发布时间，行尾显示类型。

首页、版本管理中的版本号，以及 Steve 账户和头像仍是布局示例，不代表真实安装或登录状态。真实游戏启动、文件下载、导入和账户切换尚未接入，相关按钮保持禁用。

## 版本列表与下载设置

设置页沿用下载页的 SelectorBar，分“通用 / 下载”，不提供目标文件夹选项。文件下载源和版本列表源可分别选择：

- 尽量使用镜像源：先尝试 BMCLAPI，失败再尝试官方。
- 优先使用官方源，在加载缓慢时换用镜像源：官方超过 3 秒时同时请求镜像，使用首先返回的有效清单；官方提前失败则立即尝试镜像。
- 尽量使用官方源：先尝试官方，失败再尝试镜像。

单源请求含正文读取最多等待 15 秒；正文上限为 8 MiB；空清单、无效 JSON、HTTP 错误和超时均触发回退。成功后取消剩余请求。保留 HTTPS 证书校验，不提供关闭选项。镜像映射依据 [BMCLAPI 文档](https://bmclapidoc.bangbang93.com/)。

打开游戏分类时显示旋转圆弧及“正在加载版本”，成功后淡出状态并淡入列表；双源失败时圆弧先变红，再过渡成交叉，显示错误原因与重试。离开页面或切换分类会取消旧请求，避免旧结果覆盖新页面。

最大下载线程数滑块为 1–64（默认 16）；速率滑块为 0–100 MiB/s（0 表示不限速）。这两个值及文件下载源目前仅保存，待文件下载接入后生效；版本清单请求使用独立的版本列表源策略。

设置保存到 `%LOCALAPPDATA%/NyxLauncher/download-settings.json`，采用同目录临时文件替换；读取损坏或写入失败会显示错误。版本清单不写入磁盘缓存。

愚人节版本使用独立精确 ID 清单，不按四月一日日期猜测，见 [维护列表与来源](docs/april-fools-versions.md)。

## 项目结构

- `NyxLauncher.slnx`：解决方案。
- `src/NyxLauncher/App.xaml.cs`：应用生命周期与主窗口创建。
- `src/NyxLauncher/MainWindow.xaml`：标题栏与页面容器。
- `src/NyxLauncher/MainPage.xaml`：侧栏、账户菜单与页面切换容器。
- `src/NyxLauncher/Views/`：首页、版本管理与下载界面。
- `src/NyxLauncher/Assets/`：图标和本地 SVG 原型插图。
- `src/NyxLauncher/Views/DownloadCatalog.cs`、`DownloadEntry.cs`：其他分类演示数据与列表显示模型。
- `src/NyxLauncher/Services/`：双源清单获取、下载设置存储及独立愚人节 ID 集合。
- `tests/NyxLauncher.Checks/`：无额外测试依赖的业务断言；可用 `--live` 验证真实清单端点。
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

本次版本清单接入（2026-10-28）：

- Debug 编译通过，0 警告、0 错误；未新增 NuGet 依赖。
- `.\.dotnet\dotnet.exe run --project tests/NyxLauncher.Checks -- --live` 通过 23 项检查：解析、排序去重、独立愚人节分类、普通四月一日版本、三种源顺序、慢源回退及取消、双源错误、超时、用户取消、设置回读/非法值/损坏/写入失败。
- 官方与 BMCLAPI 实际各返回 921 个版本、9 个愚人节版本；数量会随上游发布变化。
- 启动已构建的 Debug 程序后，进程存活并创建 Nyx Launcher 主窗口；结构检查覆盖新增设置 Tab、两组策略和滑块范围。
- 本轮 UI 自动化入口因运行环境初始化错误不可用，旋转、红色交叉和过渡动画尚未完成真实窗口视觉验收。下列原型验收记录属于此前版本，不作为本轮动画验收证据。

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
