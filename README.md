# Nyx Launcher

使用 C#、.NET 10 和 WinUI 3 重新构建的 Windows 启动器。

当前已接入真实 Java 游戏版本清单，包含首页、版本管理、下载，以及从左下角账户弹层进入的账户和设置页。使用原生 WinUI 3 控件，不包含 WebView。

侧栏仅放导航与底部头像、用户名，不显示应用图标或应用名。界面使用浅色主题，正文 15、导航 16；各页面不再显示页面标题，减少副标题和描述性文本。账户菜单从上到下为“账户”、分隔线、“设置”。

下载页顶部为“游戏、模组、整合包、资源包、数据包、光影”六个分类 Tab，下方是搜索框与类型筛选（全部类型、正式版、快照版、远古版、愚人节版）。类型默认筛选正式版，切换分类时也重置为正式版。滚动条位于卡片外侧的右边留白，卡片与搜索栏保持对齐。游戏分类从 Mojang / BMCLAPI 获取真实版本；其他分类保留明确标注的演示数据。列表左侧为图标，右侧为版本名称与发布时间，行尾显示类型。

首页、版本管理中的版本号，以及 Steve 账户和头像仍是布局示例，不代表真实安装或登录状态。真实游戏启动、文件下载、导入和账户切换尚未接入，首页与版本管理的相关操作按钮保持禁用。

## 游戏安装配置页

点击下载页“游戏”分类中的版本条目进入安装配置页。顶部可编辑版本名称，右侧“安装”按钮带安装图标、可点击，但不执行任何操作。下方依次显示 Forge、Fabric、NeoForge，每个加载器的 32×32 本地图标、名称、同一行灰色状态和右箭头合并在同一张卡片中；卡片最小高度为 56，间距为 10。目前不展开加载器版本选择，也不执行安装或保存名称到游戏目录。

进入时列表向左淡出，配置页从右侧淡入；返回时反向播放，时长 220 毫秒。转场期间防止重复触发；关闭系统动画后直接切换。返回保留搜索、筛选和滚动位置，离开下载模块时取消转场与探测。

三个加载器独立查询官方元数据，状态为“正在检测… / 可以添加 / 暂无可用版本 / 检测失败”。悬停失败状态可查看原因，页面不再提供重新检测按钮；返回列表后重新进入会再次探测。探测始终使用所选游戏的原始 ID，不受版本名称编辑影响：

- Forge：读取 [官方 Maven 清单](https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml)，精确匹配游戏版本前缀及后续构建号。
- Fabric：先读取 [Meta 游戏支持列表](https://meta.fabricmc.net/v2/versions/game)，再查询对应游戏的 loader 列表，避免将旧版不支持导致的 HTTP 400 当成网络失败。新版去混淆游戏（例如 26.3）的 `intermediary.version` 为通用映射 `0.0.0`，不是游戏 ID；解析接受这个占位值，仍由官方支持清单及具体游戏的 loader 请求限定支持范围。
- NeoForge：读取 [官方 Maven 清单](https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml)，按[官方编号规则](https://github.com/neoforged/documentation/blob/main/docs/gettingstarted/versioning.md)匹配 1.20.2 以后版本、26.x 及快照；1.20.1 使用 `net.neoforged:forge` 的历史清单。

加载器探测使用独立官方端点，暂不套用 Mojang/BMCLAPI 的游戏清单源设置。每个加载器总超时 15 秒、单响应上限 8 MiB，失败不伪装为“不支持”；返回或离开页面取消旧请求。保留系统代理与 HTTPS 校验，不写磁盘缓存。

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
- `src/NyxLauncher/Views/`：首页、版本管理、下载及安装配置界面。
- `src/NyxLauncher/Assets/`：图标和本地 SVG 原型插图。
- `src/NyxLauncher/Views/DownloadCatalog.cs`、`DownloadEntry.cs`：其他分类演示数据与列表显示模型。
- `src/NyxLauncher/Services/`：双源清单获取、加载器官方可用性探测、下载设置存储及独立愚人节 ID 集合。
- `tests/NyxLauncher.Checks/`：无额外测试依赖的业务断言；`--live` 验证真实游戏清单端点，`--live-loaders` 验证加载器官方端点，`--live-fabric` 仅实测 Fabric 26.3。
- `scripts/Test-Prototype.ps1`：导航、弹层顺序、页面标题移除与下载页结构的静态检查。

## 开发环境

- Windows x64。项目的 Windows API 最低版本设为 10.0.17763.0；其他系统版本尚未实机验证。
- .NET SDK 10.0.403（通过 `global.json` 固定，允许同功能带补丁升级）。
- Windows App SDK 2.6.4（包含 WinUI 3），Windows SDK BuildTools 10.0.28000.2705，通过 NuGet 还原。

本机已在 `.dotnet/` 中准备独立 SDK，该目录不会纳入 Git。其他开发机需自行安装匹配的 .NET SDK。

## 构建与运行

在项目根目录执行 PowerShell 命令：

```powershell
.\.dotnet\dotnet.exe build NyxLauncher.slnx -c Debug
.\.dotnet\dotnet.exe run --project src\NyxLauncher\NyxLauncher.csproj -c Debug
```

若系统已安装匹配的 SDK，可将 `.\.dotnet\dotnet.exe` 替换为 `dotnet`。

两种 SDK 均使用 NuGet 默认用户缓存，或终端自行设置的 `NUGET_PACKAGES`。首次运行会自动从官方源下载依赖，不需要复制开发者的 `.local` 或包缓存。已安装匹配 SDK 时，可直接执行：

```powershell
dotnet run --project .\src\NyxLauncher\NyxLauncher.csproj -p:Platform=x64
```

可用 `dotnet nuget locals global-packages --list` 查看当前缓存位置。`DOTNET_CLI_HOME` 或 `NUGET_PACKAGES` 不同时，可能使用不同缓存；在同一终端完成还原与启动即可避免切换环境。

若还原报 `NU1301` 或“每个套接字地址只允许使用一次”，并指向 `127.0.0.1:7897`，表示访问包源时使用的本机代理连接失败。先检查代理是否正常；确认本机能直连 NuGet 后，也可在当前 PowerShell 中仅让 NuGet 域名绕过该代理：

```powershell
$env:HTTP_PROXY = 'http://127.0.0.1:7897'
$env:HTTPS_PROXY = $env:HTTP_PROXY
$env:NO_PROXY = if ($env:NO_PROXY) { "$env:NO_PROXY,nuget.org,.nuget.org" } else { 'nuget.org,.nuget.org' }
dotnet restore .\src\NyxLauncher\NyxLauncher.csproj -p:Platform=x64 --disable-parallel
dotnet run --project .\src\NyxLauncher\NyxLauncher.csproj -p:Platform=x64 --no-restore
```

以上代理端口对应报错中的示例；变量只影响当前终端及其子进程，关闭终端即可丢弃，不修改 Windows 系统代理。直连不可用时应修复代理后重试，无需清空已有包缓存或关闭证书校验。代理变量会同时影响该终端启动的应用网络请求。

项目采用无 MSIX 包的桌面运行方式，并将 .NET 与 Windows App SDK 运行时复制到输出目录。首次构建需要联网还原依赖；运行不需要开发者模式或单独安装这些运行时。请保留完整输出目录，不能只复制 EXE。

当前未配置安装包、自动更新或代码签名。应用图标沿用微软官方空白模板，后续替换为项目品牌资源。

## 验证记录

安装配置页紧凑布局与 Fabric 26.3 修复（2026-10-28）：

- 官方 26.3 loader 响应使用 `intermediary.version = 0.0.0`；新增回归先复现旧逻辑误报，再修正通用映射识别，保留不支持游戏的清单校验。
- `--live-fabric` 通过 39 项加载器检查（含 1 项 26.3 官方实网检查）及原有 21 项检查；26.3 实测返回“可以添加”。
- Debug 构建通过，0 警告、0 错误；结构检查覆盖可点击但无操作的安装图标按钮、重新检测按钮移除、图标合并与 56 高紧凑卡片。差异空白检查通过；未重新进行实机布局验收。

本次安装配置页与双向转场（2026-10-28）：

- Debug 构建通过，0 警告、0 错误；没有新增 NuGet 依赖。启动构建产物后进程存活并创建 Nyx Launcher 主窗口。
- 最终离线检查通过：35 项加载器检查及原有 21 项检查，覆盖精确版本匹配、NeoForge 历史/26.x/快照编号、Fabric 支持列表、错误正文、大小上限、HTTP 错误、超时和取消。页面结构检查及 `git diff --check` 通过，三个 PNG 已复制到构建输出。
- 实网测试曾完整通过：三个加载器均对 1.21.1 返回可用，对 b1.7.3 返回无版本；NeoForge 1.20.1、26.1、26.1-snapshot-1 也返回可用。测试曾因本机代理套接字错误失败，单次进程直连后通过；最终复测又遇 Forge TLS EOF，未关闭证书校验或修改应用代理设置。实网状态仍依赖当前网络。
- 两个窗口自动化入口均因运行环境初始化失败而不可用，因此本轮未完成安装配置页排版、键盘交互及双向动画的实机验收；启动成功不代表这些交互已验证。未制作 Release 或安装包。

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
