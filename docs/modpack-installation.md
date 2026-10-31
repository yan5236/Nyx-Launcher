# 整合包安装

版本管理的“导入整合包”、向主窗口拖入单个 ZIP/MRPACK，以及下载页整合包详情，共用名称确认弹窗。默认名称来自清单（在线使用项目名），实时检查 Windows 文件名、已有版本目录和未完成任务；提交时再次检查。下载页整合包恢复加载器筛选，复用游戏版本、加载器、发布文件三级选择。

每次安装创建一个标准游戏任务，支持进度、暂停/继续、失败重试和取消。精确安装清单指定的 Minecraft、Fabric、Forge 或 NeoForge，再下载文件并解压配置，全部完成后原子发布版本。失败不会在版本列表留下半成品。整合包使用实例自己的 mods、config、存档等目录，不受全局关闭隔离影响，不修改全局偏好。

支持范围：

- Modrinth formatVersion 1：SHA-1/长度校验、下载地址回退、跳过 client unsupported 文件、普通 overrides 及优先级更高的 client-overrides。客户端 optional 文件默认安装。
- MCBBS manifestVersion 1：addons 精确组件、overrides 内嵌文件及 SHA-1 校验、fileApi 补齐缺失 addon 文件；curse 文件须提供 fileName 和可用 url。不携带下载地址的 CurseForge 引用明确失败，不猜测下载地址。
- MultiMC formatVersion 1：根目录或单个实例外层目录中的 mmc-pack.json、instance.cfg 名称，以及 minecraft/ 或 .minecraft/ 内容。LWJGL 由游戏安装链路补齐。自定义 patches、未知组件和多加载器组合明确拒绝。

不执行整合包提供的脚本、认证配置或自定义启动参数。暂不支持 Quilt、LiteLoader、OptiFine 和 MCBBS 自定义 libraries。ZIP 上限为 100000 项、解压总量 20 GiB，单份 JSON 清单 8 MiB。拒绝路径回退、绝对路径、Windows 保留名称、符号链接、重复 ZIP 路径和启动器管理文件覆盖。

检查：`dotnet run --project tests/NyxLauncher.Checks`。显式实网验收：`dotnet run --project tests/NyxLauncher.Checks -- --live-modpacks <独立验收目录>`；会下载 Fabric API 并安装三种格式的 Minecraft 1.21.1 + Fabric 0.16.10 实例，保留结果供检查。

格式依据：[Modrinth 规范](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack)、[MCBBS 清单字段](https://github.com/HMCL-dev/HMCL/blob/main/HMCLCore/src/main/java/org/jackhuang/hmcl/modpack/mcbbs/McbbsModpackManifest.java)、[MultiMC 导出结构](https://github.com/MultiMC/wiki/blob/master/Export-Instance.md)。

## 本轮验收（2026-10-31）

- Debug x64 在 `.local/modpacks-final` 构建通过，0 警告、0 错误；XAML/结构检查通过，全部离线业务检查通过。测试项目 NuGet 漏洞数据查询产生 NU1900 网络警告，不影响构建及断言。
- `--live-modpacks .local/download-install-qa` 通过三种最小格式包的真实 Minecraft 1.21.1 + Fabric 0.16.10 安装。Modrinth/MCBBS 下载真实 Fabric API，MultiMC 导入同一真实 JAR；验证哈希、配置、版本清单和独立目录。结果为 `pack-qa-modrinth-164346`、`pack-qa-mcbbs-164535`、`pack-qa-multimc-164805`。
- 首轮 Windows 发布目录曾发生临时占用；加入最多 3 秒且可取消的原子发布重试后，上述三格式完整回归通过。目标存在仍立即拒绝覆盖。
- 实际鼠标/键盘验收：版本管理选择 MRPACK、清单名称预填、现有名称禁止安装、任务创建与真实进度、暂停/取消；资源管理器拖入后弹出相同名称确认；在线 Fabulously Optimized 的版本→Fabric→MRPACK 三级详情及项目名预填、在线任务创建。两项 UI 测试下载已取消；未完成该大型在线包的全量安装。
- 原桌面工具截图超时后，使用沙箱外桌面截图及输入完成操作验证。未启动安装后的游戏，未验证 Release 或其他 Windows/DPI；Forge/NeoForge 的整合包组合未在本轮实网运行。
