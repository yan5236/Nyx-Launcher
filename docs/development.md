# 开发指南

## 环境与构建

目标平台为 Windows x64，界面使用 WinUI 3，不依赖 WebView。安装 [global.json](../global.json) 要求的 .NET SDK；Windows 目标版本、运行时和依赖版本以[应用项目文件](../src/NyxLauncher/NyxLauncher.csproj)为准，包源以 [NuGet.Config](../NuGet.Config) 为准。

首次构建前，复制[微软配置示例](../config/microsoft-auth.example.json)为同目录的 `microsoft-auth.json`，填写自己的公共客户端配置，已有文件不要覆盖。字段与回调要求见[微软登录](microsoft-login.md#客户端配置)。实际配置被 Git 忽略，但会嵌入构建产物；不放入密码或客户端 secret。

在仓库根目录执行：

```powershell
dotnet build NyxLauncher.slnx -c Debug -p:Platform=x64
dotnet run --project src/NyxLauncher/NyxLauncher.csproj -c Debug -p:Platform=x64
```

首次构建需要联网还原依赖，使用 NuGet 默认用户缓存或显式配置的缓存，不需要复制他人的 SDK 或本地验收目录。还原失败时检查包源、网络与当前代理；不在共享文档中固化某台机器的网络绕过方案，不关闭证书校验。

普通 Debug/目录构建需要保留完整输出目录。正式发布使用无 MSIX、自包含的单 EXE；依赖会在启动时解压至 .NET 缓存，默认游戏与 Java 目录始终以实际 EXE 所在位置为根，不使用解压目录。发布版不要求另装 .NET 或 Windows App SDK，当前只发布 Windows x64，未配置代码签名。

## 打包与 Tag 发布

发布配置 [microsoft-auth.release.json](../config/microsoft-auth.release.json)只包含项目自己的微软公共客户端 ID 和 localhost 回调，随程序集嵌入，GitHub Actions 无需额外认证密钥。客户端 ID 是公开应用标识，不是 client secret；禁止加入密码、令牌或 client secret。本地开发默认仍使用被 Git 忽略的 `microsoft-auth.json`，发布脚本显式选择公共发布配置。

```powershell
pwsh -File scripts/Publish-Release.ps1 -Version v1.0.0
```

脚本按版本建立独立中间目录，拒绝覆盖非空发布目录，使用 [SingleFile 发布配置](../src/NyxLauncher/Properties/PublishProfiles/SingleFile.pubxml)打包并确认运行产物只有一个 EXE，不分发原生调试符号，输出 `artifacts/1.0.0/dist/NyxLauncher.exe` 和 `SHA256SUMS`。请保留 `NyxLauncher.exe` 文件名，WinUI 资源索引依赖这个名称。版本必须使用三段 SemVer，各段不超过 65534；支持 `v1.0.0-rc.1` 这类预发布。再次构建同版本时使用新的 `-OutputDirectory`。

[发布工作流](../.github/workflows/release.yml)在推送 `v*` Tag 时运行 Windows 业务检查、单文件发布和界面结构检查，保存 Actions artifact，再创建带自动生成更新说明的 GitHub Release 并上传 EXE 和校验清单；带预发布后缀的 Tag 标记为 prerelease。维护者可在发布页补充面向用户的更新说明，启动器会显示 Release 正文。

```powershell
git tag v1.0.0
git push origin v1.0.0
```

先将工作流和实现提交、推送到项目仓库，再向该提交打 Tag。工作流使用仓库自带 `GITHUB_TOKEN` 的 `contents: write` 权限，不需要另建 PAT。示例命令不会由启动器执行。

## 协议与自身更新

协议位于仓库根目录的 [USER_AGREEMENT.md](../USER_AGREEMENT.md) 与 [PRIVACY.md](../PRIVACY.md)，随产物打包，界面链接直接打开 GitHub 对应文件。行为变化需要重新确认时，同时更新两份文档日期及 [AgreementStore.Revision](../src/NyxLauncher/Services/AgreementStore.cs)；首次或旧修订先弹窗，主动勾选并同意且写盘成功后才创建主页面。不同意或关闭时退出。

自身更新只由“设置 → 关于 → 检查更新”触发，查询 GitHub latest 正式发布，展示版本和更新说明，确认后用共享下载任务下载安装。预发布不会成为正式版自动候选。仅单文件发布版允许直接替换，目录/开发版引导手动获取发布产物。没有后台自动检查、自动安装或强制重启。

下载包必须是本仓库发布的固定 `NyxLauncher.exe`，使用 GitHub asset 的 `digest` 校验 SHA-256 和大小，缺失校验信息拒绝安装。跨进程锁防止并发替换；旧 EXE 改名为 `.previous` 后再移动新文件，失败恢复旧路径，备份冲突不覆盖。当前窗口继续运行，新版本下次启动生效，旧进程释放文件后才可清理备份。暂存、锁和备份位于 EXE 旁，因此该目录需要写权限；不支持跨重启恢复下载任务。

单文件属性依据 [Microsoft 的 WinUI 3 发布说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app#single-file-exe)；更新元数据依据 [GitHub Releases API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)。

## 职责与维护入口

| 范围 | 主要入口 | 职责 |
| --- | --- | --- |
| 应用与导航 | [App](../src/NyxLauncher/App.xaml.cs)、[MainPage](../src/NyxLauncher/MainPage.xaml.cs)、[Views](../src/NyxLauncher/Views) | 生命周期、页面组合与交互 |
| 账户 | [AccountStore](../src/NyxLauncher/Services/AccountStore.cs)、[MicrosoftLoginService](../src/NyxLauncher/Services/MicrosoftLoginService.cs)、[ExternalLoginService](../src/NyxLauncher/Services/ExternalLoginService.cs) | 身份、认证、加密缓存与持久化 |
| 游戏与启动 | [GameInstallationService](../src/NyxLauncher/Services/GameInstallationService.cs)、[GameLaunchService](../src/NyxLauncher/Services/GameLaunchService.cs)、[GameLaunchSession](../src/NyxLauncher/Services/GameLaunchSession.cs) | 安装、启动准备和单次进程生命周期 |
| 本地版本 | [InstalledVersionService](../src/NyxLauncher/Services/InstalledVersionService.cs)、[VersionManagementService](../src/NyxLauncher/Services/VersionManagementService.cs)、[VersionIsolation](../src/NyxLauncher/Services/VersionIsolation.cs) | 扫描、改名、删除边界及实际运行目录 |
| 下载 | [DownloadTaskManager](../src/NyxLauncher/Services/DownloadTaskManager.cs)、[DownloadTransport](../src/NyxLauncher/Services/DownloadTransport.cs)、[ResourceMirror](../src/NyxLauncher/Services/ResourceMirror.cs) | 任务状态、预算、校验发布与镜像映射 |
| 资源 | [ModrinthCatalogService](../src/NyxLauncher/Services/ModrinthCatalogService.cs)、[ResourceInstallationService](../src/NyxLauncher/Services/ResourceInstallationService.cs)、[LocalResourceService](../src/NyxLauncher/Services/LocalResourceService.cs)、[ModpackInstallationService](../src/NyxLauncher/Services/ModpackInstallationService.cs) | 在线发现、目标校验、本地复制与整合包安装 |
| Java 与启动偏好 | [JavaRuntimeStore](../src/NyxLauncher/Services/JavaRuntimeStore.cs)、[JavaInstallationService](../src/NyxLauncher/Services/JavaInstallationService.cs)、[MemorySettings](../src/NyxLauncher/Services/MemorySettings.cs)、[AdvancedLaunchSettings](../src/NyxLauncher/Services/AdvancedLaunchSettings.cs) | Java 登记、安装、选择及全局/版本设置 |
| 检查 | [业务检查](../tests/NyxLauncher.Checks)、[界面结构检查](../scripts/Test-Prototype.ps1) | 可重复运行的验证，选择见[验证指南](testing.md) |

沿用已有服务边界和共享目录解析。更改身份、隔离、任务生命周期或文件发布时，检查相关调用方与测试，避免不同入口出现不同规则。

## 数据与安全边界

- 用户配置位于 `%LOCALAPPDATA%/NyxLauncher`，字段和迁移由对应 Store 维护。配置损坏或写入失败不得伪装成功或覆盖原数据。
- EXE 所在目录承载默认 `.minecraft` 和应用安装的 `Java`，需要写权限。共享资源与版本独立数据的关系见[界面与功能](interface-and-behavior.md)。
- 下载、导入、解压、改名和删除保留目标归属、链接、路径穿越、完整性与冲突校验；失败不覆盖已有用户文件。
- 认证不使用下载镜像，保留 HTTPS 校验；凭据不进入文档、截图或提交。日志导出有脱敏，但第三方模组输出仍可能包含个人信息。

共享文档与本机记录的分界见[文档维护约定](README.md#共同维护约定)。
