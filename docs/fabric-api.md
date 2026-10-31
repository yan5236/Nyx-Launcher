# Fabric API 下载

下载 → 游戏 → 某个版本的详情页中，先选择 Fabric Loader，再点击 Fabric API 行选择发布版本。API 使用与加载器相同的原生版本列表弹窗，显示版本号、发布日期和发布类型；点击条目立即选择，点击已选行的叉号取消。API 可选，不影响只安装 Fabric。

取消 Fabric 会同时清除 API；切换游戏或离开后重新打开详情页会清空选择。编辑安装名称不影响原始 Minecraft 版本的兼容查询。Forge、NeoForge 与 API 不能同时选择。API 与三个加载器卡片左侧对齐。没有发布时显示“暂无可用版本”；网络失败显示“检测失败，点击重试”，悬停可查看原因。点击失败行只重新请求该项，成功后直接打开版本列表；请求中禁用重复点击，离开或切换游戏取消并丢弃迟到结果。API 重试仍需先选择 Fabric，加载器重试遵循互斥选择规则。

## 数据与下载

- 使用 [Modrinth 项目版本接口](https://docs.modrinth.com/api/operations/getprojectversions/)，固定官方 Fabric API 项目 `P7dR8mSH`，同时传入精确 `game_versions` 与 `loaders=["fabric"]`。返回后再次验证项目、游戏与加载器，按发布时间降序去重。
- 只安装主 JAR；没有 primary 标记时按接口约定选第一个文件，拒绝源码等非运行文件、无效文件名、无效 SHA-1 或非官方项目 CDN 地址。兼容官方历史链接的 `//data/` 路径；历史元数据大小为 0 时按未知大小处理，仍要求并校验 SHA-1，负数大小拒绝。
- 清单请求总超时 15 秒、正文上限 8 MiB，使用系统代理和 HTTPS 校验；离开页面取消请求。Fabric API 当前直接使用 Modrinth 官方服务，没有虚构 BMCLAPI 映射。
- 安装任务捕获 API 版本、目标游戏目录及隔离设置。文件下载共用现有并行额度、连接数、限速、暂停/继续和取消机制，按长度及 SHA-1 验证后原子发布。

## 安装位置与保护

API 安装在 `VersionIsolation.Resolve` 决定的实际游戏目录的 `mods` 中。启用该版本隔离时先放入版本暂存目录，随完整版本发布；不隔离时放入共享根目录的 `mods`。完整 API 缺失或下载失败时不会把游戏任务标为完成。

完全相同且哈希有效的文件会复用；同名但内容不同的文件不会覆盖。`mods` 已有其他 `fabric-api-*.jar` 时会明确报错，要求用户先移走旧版或启用隔离，不自动删除模组。共享目录中已经成功下载的 API 与其他共享资源一样，在随后取消或版本发布失败时可能保留。不支持重命名过的 API 文件识别、自动升级/清理或跨隔离设置迁移；日后更改隔离设置时需要自行迁移模组。

兼容清单筛选的是 Minecraft 版本和 Fabric 加载器类型。用户可选择历史 Loader；API 可能要求较新的 Loader，仍应使用合适的 Loader 版本。本次没有引入通用模组依赖解析器。

## 验证

```powershell
.\.dotnet\dotnet.exe run --project tests/NyxLauncher.Checks --no-restore
.\.dotnet\dotnet.exe run --project tests/NyxLauncher.Checks --no-build -- --live-fabric-api .local/download-install-qa
```

实网入口仅用于独立验收目录，会安装 `qa-FabricApi-All` 和 `qa-FabricApi-Disabled`，前者实际启动并验证日志，后者检查共享目录。不要传入日常使用的游戏目录。

2026-10-29：

- Debug 构建通过，0 警告、0 错误；262 项业务检查通过，包含 33 项 API 检查与 2 项审查修复回归。检查覆盖精确筛选、排序、主文件、坏元数据、HTTP 错误、超时、取消、大小限制、文件完整性及冲突保护。
- 7 项实网检查通过：Minecraft 1.21.1、Fabric 0.20.1、Fabric API 0.116.17+1.21.1。文件 SHA-1 为 `810b2b0195371a012906241d8b85a32a1d6de53c`；隔离和共享目录长度/哈希正确，真实游戏窗口出现并持续运行，日志确认加载所选 API。
- 原生窗口验证选择前禁用、版本列表、点选关闭、改名保留选择、API 单独取消、取消 Loader 清空 API，以及 Minecraft 1.0 无可用版本。截图位于 `.local/screenshots/fabric-api-*.png`；Computer Use 两个入口初始化失败，使用现有 UI Automation 与窗口区域截图验收。
- 首次构建因上次验收窗口锁定输出文件失败，正常关闭后构建通过。未验证其他 Minecraft 版本的真实启动、其他 Windows/DPI、Release 或安装包。

同日 UI 调整复核：四张卡片实测左边缘及宽度一致；用仅作用于验收进程的回环代理制造失败，验证失败文案、再次失败后仍可重试、请求中禁用点击、恢复网络后自动打开 Fabric / API 列表，以及重试中返回并切换游戏后无迟到弹窗。代理只透传 TLS，未改系统代理或 HTTPS 校验，验收后已关闭。截图为 `.local/screenshots/ui-polish-alignment.png`、`ui-polish-detection-failed.png`、`ui-polish-api-retry.png`。
