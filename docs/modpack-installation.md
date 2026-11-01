# 整合包安装

版本管理的“导入整合包”、向主窗口拖入单个 ZIP/MRPACK，以及下载页整合包详情，共用名称确认弹窗。默认名称来自清单（在线使用项目名），实时检查 Windows 文件名、已有版本目录和未完成任务；提交时再次检查。下载页整合包恢复加载器筛选，复用游戏版本、加载器、发布文件三级选择。

每次安装创建一个标准游戏任务，支持进度、暂停/继续、失败重试和取消。精确安装清单指定的 Minecraft、Fabric、Forge 或 NeoForge，再下载文件并解压配置，全部完成后原子发布版本。失败不会在版本列表留下半成品。整合包使用实例自己的 mods、config、存档等目录，不受全局关闭隔离影响，不修改全局偏好。

支持范围：

- Modrinth formatVersion 1：SHA-1/长度校验、下载地址回退、跳过 client unsupported 文件、普通 overrides 及优先级更高的 client-overrides。客户端 optional 文件默认安装。
- MCBBS manifestVersion 1：addons 精确组件、overrides 内嵌文件及 SHA-1 校验、fileApi 补齐缺失 addon 文件；curse 文件须提供 fileName 和可用 url。不携带下载地址的 CurseForge 引用明确失败，不猜测下载地址。
- MultiMC formatVersion 1：根目录或单个实例外层目录中的 mmc-pack.json、instance.cfg 名称，以及 minecraft/ 或 .minecraft/ 内容。LWJGL 由游戏安装链路补齐。自定义 patches、未知组件和多加载器组合明确拒绝。

不执行整合包提供的脚本、认证配置或自定义启动参数。暂不支持 Quilt、LiteLoader、OptiFine 和 MCBBS 自定义 libraries。ZIP 上限为 100000 项、解压总量 20 GiB，单份 JSON 清单 8 MiB。拒绝路径回退、绝对路径、Windows 保留名称、符号链接、重复 ZIP 路径和启动器管理文件覆盖。

格式依据：[Modrinth 规范](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack)、[MCBBS 清单字段](https://github.com/HMCL-dev/HMCL/blob/main/HMCLCore/src/main/java/org/jackhuang/hmcl/modpack/mcbbs/McbbsModpackManifest.java)、[MultiMC 导出结构](https://github.com/MultiMC/wiki/blob/master/Export-Instance.md)。

下载阶段按已接收字节、文件完成数显示进度；未知大小不作为真实总字节数显示。名称弹窗在名称无效或重名时禁用安装。

## 维护入口

[ModpackManifest](../src/NyxLauncher/Services/ModpackManifest.cs)、[ModpackInstallationService](../src/NyxLauncher/Services/ModpackInstallationService.cs)、[VersionIsolation](../src/NyxLauncher/Services/VersionIsolation.cs)。验证入口统一见[验证指南](testing.md)。
