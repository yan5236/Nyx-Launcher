# 开发指南

## 项目结构

- `NyxLauncher.slnx`：解决方案。
- `src/NyxLauncher/App.xaml.cs`：应用生命周期与主窗口创建。
- `src/NyxLauncher/MainWindow.xaml`：标题栏与页面容器。
- `src/NyxLauncher/MainPage.xaml`：侧栏、账户菜单与页面切换容器。
- `src/NyxLauncher/Views/`：首页、版本管理、下载及安装配置界面。
- `src/NyxLauncher/Assets/`：图标和本地 SVG 原型插图。
- `src/NyxLauncher/Views/DownloadEntry.cs`：真实游戏版本列表显示模型。
- `src/NyxLauncher/Views/ResourceCatalogView.xaml`、`.xaml.cs`：五类资源的搜索、筛选、卡片与分页。
- `src/NyxLauncher/Services/ModrinthCatalogService.cs`、`ResourceProject.cs`：Modrinth 搜索与版本标签、资源结果模型。
- `src/NyxLauncher/Services/`：双源清单、下载传输及任务状态、游戏/加载器安装、隔离与启动、目录持久化、本地版本扫描和删除范围校验。
- `tests/NyxLauncher.Checks/`：无额外测试依赖的业务断言；`--live` 验证真实游戏清单端点，`--live-loaders` 验证加载器官方端点，`--live-fabric` 仅实测 Fabric 26.3，`--live-loader-lists` 分别限定官方/BMCLAPI 域名验证完整加载器列表。
- `--live-install <独立验收目录>`：真实安装 Minecraft 1.21.1 原版及三个加载器，验证扫描与隔离启动参数；会下载游戏资源。可选 `--direct` 仅令此测试进程直连，不修改应用或系统代理。
- `--live-launch <独立验收目录>`：检查已安装的四种验收客户端，验证损坏资源修复、断网暖缓存、实际窗口、账户参数和分阶段停止。只用于 `--live-install` 的独立目录。
- `scripts/Test-Prototype.ps1`：导航、弹层顺序、页面标题移除与下载页结构的静态检查。

## 开发环境

- Windows x64。项目的 Windows API 最低版本设为 10.0.17763.0；其他系统版本尚未实机验证。
- .NET SDK 10.0.403（通过 `global.json` 固定，允许同功能带补丁升级）。
- Windows App SDK 2.6.4（包含 WinUI 3），Windows SDK BuildTools 10.0.28000.2705，通过 NuGet 还原。

本机已在 `.dotnet/` 中准备独立 SDK，该目录不会纳入 Git。其他开发机需自行安装匹配的 .NET SDK。

## 构建与运行

在项目根目录执行 PowerShell 命令：

```powershell
# 首次构建先复制示例，将 ClientId 改为自己的微软应用 ID；实际配置不纳入 Git。
Copy-Item config/microsoft-auth.example.json config/microsoft-auth.json
.\.dotnet\dotnet.exe build NyxLauncher.slnx -c Debug
.\.dotnet\dotnet.exe run --project src\NyxLauncher\NyxLauncher.csproj -c Debug
```

若系统已安装匹配的 SDK，可将 `.\.dotnet\dotnet.exe` 替换为 `dotnet`。

微软配置的填写、回调注册及 PKCE 说明见[微软登录](microsoft-login.md)。实际 JSON 会嵌入 Debug、Release 和发布产物；复制配置只需首次执行，避免覆盖已有 ID。

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

