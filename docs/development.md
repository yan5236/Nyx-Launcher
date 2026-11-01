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

项目采用无 MSIX 包的自包含桌面输出，分发时保留完整输出目录，不能只复制 EXE。构建成功不代表发布包、代码签名、自动更新或其他系统环境已经验证。

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
