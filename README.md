<div align="center">

# Nyx Launcher

**在 Windows 上安装、管理与启动 Minecraft Java 版。**

原生 WinUI 3 界面 · 多版本管理 · 模组下载

[开始使用](#开始使用) · [功能一览](#功能一览) · [开发指南](docs/development.md)

</div>

![Nyx Launcher 首页：游戏版本选择、账户状态与启动入口](assets/readme/home.png)

<sub>首页实机截图，界面随开发持续调整。</sub>

## 功能一览

- **安装与启动** — 下载原版游戏，选配 Forge、Fabric 或 NeoForge，自动补齐游戏资源与运行依赖。
- **管理版本** — 切换游戏目录，搜索本地版本，更改名称与图标，按版本隔离存档和模组。
- **选择账户** — 支持微软账户、离线账户，以及 LittleSkin 等兼容 Yggdrasil 的外置账户。
- **发现模组** — 搜索和筛选 Modrinth 资源，将兼容的模组安装到指定游戏版本。
- **管理 Java 与下载** — 扫描、导入和安装 Java，切换官方或镜像源，暂停、继续和取消下载任务。

> 当前仍在开发中。整合包、资源包、数据包与光影暂支持浏览和筛选；CurseForge 尚未接入。下载任务暂不支持跨重启恢复。

## 开始使用

当前提供源码构建方式，需要 **Windows x64** 与 **.NET SDK 10.0.403**。

首次构建前，复制微软登录配置示例，并按[配置说明](docs/microsoft-login.md#客户端配置)填写自己的应用 ClientId；已有配置时跳过复制。

```powershell
Copy-Item config/microsoft-auth.example.json config/microsoft-auth.json
```

在仓库根目录构建并运行：

```powershell
dotnet build NyxLauncher.slnx -c Debug -p:Platform=x64
dotnet run --project src/NyxLauncher/NyxLauncher.csproj -c Debug -p:Platform=x64
```

进入启动器后，添加账户，在「下载 → 游戏」安装一个版本，再回到首页选择版本并启动。

## 文档

- [开发与构建](docs/development.md)
- [微软登录配置](docs/microsoft-login.md) · [外置账户](docs/external-login.md)
- [Java 管理](docs/java-management.md) · [模组下载](docs/mod-downloads.md) · [Fabric API](docs/fabric-api.md)
- [界面与功能说明](docs/interface-and-behavior.md) · [历史验证记录](docs/validation-history.md)
