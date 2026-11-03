# 整合包导入与导出

## 导出

入口为“版本管理 → 版本详情 → 导出”。填写整合包名、作者和版本，点击导出路径区域选择文件夹；文件名按整合包名生成，非法文件名字符替换为下划线，扩展名由格式决定。选择 Modrinth（`.mrpack`）、MCBBS 或 MultiMC（`.zip`）。文件树读取该版本实际使用的隔离或共享游戏目录；点击文件夹标题或右侧箭头展开/收起子项，独立勾选框支持整体勾选与半选；点击文件卡片选择该文件，展开后的子卡片紧贴排列；列表随内容展开并跟随页面统一滚动。默认选择模组、模组配置和默认配置，存档、截图、服务器列表等需自行勾选。常见游戏设置、投影、资源包、光影包、脚本与地图目录提供用途标注。

- Modrinth 默认按 SHA-1 查询所选模组、资源包及光影目录中的文件，精确匹配后验证 SHA-512 和长度并写入官方下载引用，未收录资源内置到 overrides。普通配置等文件始终按勾选内置。网络查询失败会报错，不会当成未收录而内置。
- 勾选“打包资源文件”后，所有所选资源直接内置，无需联网，并显示模组协议警告。
- “只打包 Modrinth 资源”仅适用于 Modrinth 格式：未收录资源跳过，成功通知报告跳过数量；勾选“打包资源文件”时清除并禁用此选项。此筛选不保证上传审核通过，也不检查模组间依赖是否完整。
- MCBBS 与 MultiMC 按原生格式固定内置所选资源，并展示同一协议警告。MCBBS 写入 addons、文件 SHA-1 和 overrides；MultiMC 写入组件清单、实例名和 `.minecraft` 内容。

游戏运行或安装中不允许开始导出。导出支持取消；离开导出页会取消未完成工作并释放文件树。输出同名文件不覆盖，失败或取消清理暂存文件，完成后显示可点击定位文件的 Toast。导出不包含启动器内部状态、已知账户文件、运行库、游戏客户端 JAR、当前版本清单或链接；用户自选配置、日志和存档仍可能包含个人数据。清单扫描最多 100000 项、64 层，导出内容最多 20 GiB。

## 导入

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
