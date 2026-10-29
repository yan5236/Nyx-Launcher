# 账户与真实启动

## 账户

首次启动不生成账户；空状态在内容区居中显示账户图标和“目前还没有账户呢，点击右上角添加”。右上角绿色添加按钮打开原生 WinUI `ContentDialog`。类型卡片左侧有图标，包含微软登录、离线账户和外置登录；三种入口均可用，外置认证默认提供 LittleSkin，详见[外置登录](external-login.md)。微软 API 申请尚未完成，Debug 暂用用户指定的测试客户端，入口明确标注“测试应用 · 仅供开发验证”。配置与登录流程详见[微软登录](microsoft-login.md)。

进入离线输入和返回类型选择均使用 220 毫秒平移淡入，遵循系统动画开关；返回按钮位于标题左侧，底部从左到右为“取消”“确定”。返回保留输入，保存错误留在当前步骤。确认按钮、添加、安装、创建下载及首页启动共用全局绿色配色（默认 #28563B、悬停 #346C4B、按下 #20452F）；运行中的停止按钮保留红色语义。

离线账户名称接受 1–16 个英文字母、数字或下划线，拒绝空白、非法字符和忽略大小写后重复的名称。添加成功自动选中，之后点击整张账户卡片切换，键盘选择同样生效。卡片显示名称、账户类型和左侧选中竖线；头像没有额外底色、内边距或边框。侧栏与首页共用同一个 `AccountStore`。

账户保存到 `%LOCALAPPDATA%/NyxLauncher/accounts.json`，离线身份只保存名称；微软身份还包含真实游戏 UUID、MSAL 账户标识、客户端 ID 和 Windows 当前用户 DPAPI 加密的令牌缓存。当前选择使用账户类型与身份键，兼容读取旧版按名称选择的离线配置，允许同名微软与离线身份并存。同目录临时文件原子替换成功后才更新内存和界面；损坏配置保留原文件并禁止覆盖。已有游戏进程保持启动时的身份，切换账户用于下一次启动。

每张卡片右侧提供圆形重试登录与删除按钮。离线重试重新校验名称、创建稳定 UUID 会话并选中该账户；微软重试打开系统浏览器重新授权，要求游戏 UUID 与原账户相同，游戏改名按 UUID 更新。删除移除本地账户及其加密缓存，不删除游戏文件或存档，也不撤销微软侧授权或退出系统浏览器；删除当前账户后选中剩余首项，删完最后一个后清空选择，侧栏与首页同步更新。写入失败保留原列表和选择。

离线 UUID 使用 Java `UUID.nameUUIDFromBytes(UTF8("OfflinePlayer:" + name))` 的 MD5 / RFC 4122 v3 规则，与 [HMCL 的离线账户工厂](https://github.com/HMCL-dev/HMCL/blob/f1c9b77df5f267e9eb3d09ce8881b73ed4ae0c4f/HMCLCore/src/main/java/org/jackhuang/hmcl/auth/offline/OfflineAccountFactory.java)一致。显式传入 CmlLib 会话，避免该库离线会话的随机 UUID 导致同名账户身份漂移。名称大小写会影响离线 UUID，不自动改写；微软 UUID 使用服务器档案原值。

微软账户页与侧栏共用真实皮肤头像控件：按真实 UUID 查询 Mojang 公开档案，从官方 `textures.minecraft.net` 下载皮肤，裁出正脸并叠加帽子层，以最近邻生成 64×64 头像：正脸绘制在内缩 4 像素的 56×56 区域，帽子层覆盖完整画布，保留透明外角并输出预乘 BGRA，避免方形底角及半透明黑边。层比例参考 [HMCL 的头像绘制](https://github.com/HMCL-dev/HMCL/blob/main/HMCL/src/main/java/org/jackhuang/hmcl/game/TexturesLoader.java)。已有微软账户不需要重新登录；不读取或向皮肤请求附带令牌。每次启动查询一次，同一进程的侧栏和卡片共享结果；换皮肤后重启启动器更新。头像像素原子缓存到 `%LOCALAPPDATA%/NyxLauncher/avatars/<UUID>-head-v2.bgra`，启动优先显示缓存，网络或解码失败保留旧头像，无缓存时使用默认头像。切换账户或控件卸载后的迟到结果不会覆盖新身份。

离线账户及没有可用皮肤的回退头像按用户提供的 Steve / Alex 两张素材采用传统双皮肤规则：Java UUID 哈希的最低位为 0 时显示 Steve，为 1 时显示 Alex，参考 [PCL 的 McSkinSex](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb)。名称恰为 Steve 不代表头像一定是 Steve。现代 Minecraft / HMCL 已扩展默认皮肤集合，本启动器的双默认头像不承诺与所有新版游戏内部默认皮肤一致，也不注入皮肤模组。

`scripts/Generate-Avatars.ps1` 从完整皮肤的 `(8,8,8,8)` 裁出正脸，并叠加 `(40,8,8,8)` 的透明帽子层，以最近邻放大生成头像；坐标随皮肤分辨率缩放。裁剪位置参考 [HMCL 的头像绘制](https://github.com/HMCL-dev/HMCL/blob/ac378ad46aa07c5d683dd0d82fea2cd4f88e99bc/HMCL/src/main/java/org/jackhuang/hmcl/game/TexturesLoader.java)，原始皮肤保持完整。

## 启动与停止

首页顶部仅显示内置的湖畔截图，不叠加标题；图片使用用户提供的 1920×1017 PNG 压缩为约 253 KiB JPEG，源截图不变。下方为等宽三列：带图标、版本名和 Minecraft / 加载器版本的选择框，居中的两行启动按钮（主行显示图标和操作，副行显示所选版本名，运行时保留本次版本名），以及直接进入所选版本详情的版本管理按钮；不显示当前账户文本。首页随窗口伸展，不受其他页面的 1280 像素宽度上限限制，背景高度、控件高度及主要字号同步调整。没有账户或有效版本时不能启动；点击启动后锁定本次版本、目录、隔离设置与账户，按钮立即变为红色“停止”。后台依次执行：

1. 检查资源完整性：校验资源索引和资源对象，按上游长度、SHA-1 复用或补齐。
2. 检查依赖完整性：校验客户端、libraries、日志配置和 Java；使用现有下载器的源策略、并行限制与原子发布。
3. 登录账户：离线身份生成稳定 UUID 会话；外置身份验证或刷新 Yggdrasil 会话并加载经 SHA-256 校验的 authlib-injector；微软身份通过 MSAL 静默取得或刷新微软令牌，再完成 Xbox/XSTS、Minecraft 认证、游戏资格与档案校验，保存最新加密缓存后传入真实会话。需要交互授权时停止启动并提示前往账户页重新登录，任何认证失败都不会退回离线身份。
4. 等待游戏窗口出现：创建 Java 进程并持续排空标准输出和错误输出，检查对应进程的可见游戏窗口。
5. 运行：发现真实窗口后按钮变为“停止运行”，保留实际版本和启动身份；不再显示“游戏正在运行”等常驻提示。

背景图与启动区之间保留进度区。启动时阶段文字与进度条从上方滑入，阶段里程碑依次为 10%、45%、75%、94%，不代表下载字节比例；等待窗口时保留最后一段。真实窗口出现后填满，停留 300 毫秒并向下淡出；进出动画为 220 毫秒，遵循系统动画开关。取消后收起进度，快速再次启动不会被旧动画隐藏。

失败使用带叉号和“确定”的弹窗显示具体原因，并提供“用记事本打开日志”“打开日志文件夹”。每次启动在游戏根目录 `.nyx-launch/logs/launch-*.log` 保存独立的 UTF-8 阶段与标准输出/错误日志，游戏访问令牌在写入前脱敏；准备阶段失败同样有日志。日志写入失败不阻断游戏，弹窗说明保存错误，未创建文件时禁用日志入口。

原版、Fabric、Forge、NeoForge 共用 CmlLib 本地版本及继承解析，保留加载器 JVM 参数。Java 检查从继承链中取有效 Java 声明，避免加载器子清单未声明 Java 时误装旧版 Java 8。缺失且没有下载地址的处理器产物会明确报错，需要重新安装对应加载器，不会伪造文件。

Java 元数据保存在游戏目录 `.nyx-launch/metadata`；首次获取需要网络，之后完整缓存允许离线检查。请求保留系统代理与 HTTPS 校验；取消令牌覆盖底层库未传递令牌的元数据请求。资源损坏时仍需网络修复。

旧版本的 `resources` / `assets/virtual` 资源副本也按哈希检查，同长度损坏会从已验证资源对象原子修复。副本目标与下载目标使用相同的游戏根目录及链接检查，清单不能通过复制后处理写入外部目录。

窗口检测参考 [PCL 的 ModWatcher](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModWatcher.vb)：用 `EnumWindows`、窗口所属 PID、可见性和 GLFW/LWJGL/SDL/AWT 类名共同判断，排除辅助窗口及加载器早期窗口，不要求标题以 Minecraft 开头。[HMCL 的 LauncherHelper](https://github.com/HMCL-dev/HMCL/blob/ac378ad46aa07c5d683dd0d82fea2cd4f88e99bc/HMCL/src/main/java/org/jackhuang/hmcl/game/LauncherHelper.java)主要通过 LWJGL 日志推进启动；这里不把日志或进程存在等同于窗口已出现。窗口出现也不等于世界或主菜单已加载完成。

准备、登录和等待窗口期间均可取消；运行中先请求正常关闭窗口，三秒内未退出则终止本次进程树。停止可能中断尚未保存的游戏进度。不会终止其他 Java 进程。正常退出恢复启动按钮；异常退出弹窗显示退出码和末尾日志。关闭启动器同样停止其持有的游戏及下载工作并等待回收。

## 验证入口

```powershell
.\.dotnet\dotnet.exe run --project tests/NyxLauncher.Checks
.\.dotnet\dotnet.exe run --project tests/NyxLauncher.Checks -- --live-launch .local/download-install-qa
```

第二条只用于此前 `--live-install` 创建的独立验收目录，要求存在 `qa-vanilla`、`qa-Fabric`、`qa-Forge`、`qa-NeoForge`。它会备份并临时损坏两个小文件以检查修复、验证断网暖缓存检查，随后真实启动四个客户端、等待窗口并停止，最后验证各准备阶段取消。不要指向日常使用的游戏目录。

2026-10-29 UI 调整验收：x64 Debug 构建与 XAML 检查通过，269 项业务断言通过（其中账户与启动 48 项）。实机验证添加弹窗的图标、输入校验、返回保留输入、按钮顺序、动画、新增/重试/删除测试账户及居中空状态；原账户保留，空状态验收后原配置逐字节恢复。截图位于 `.local/screenshots/ui-polish-*.png`。该次 UI 验收未重跑游戏安装/启动全流程、其他 DPI 或 Release 构建；当时微软与外置认证尚未接入。此后微软登录实现及验证范围见 [微软登录](microsoft-login.md)。

同日悬停补验：前述静止颜色检查遗漏了 WinUI 状态资源回落到系统蓝色的问题。按钮资源现集中在 `Themes/AccentButtonResources.xaml`，由应用、页面和独立弹窗显式合并。真实鼠标移动/按住后采样确认、添加账户、启动、安装、下载按钮，三种状态均分别为 `#28563B` / `#346C4B` / `#20452F`；验收在按钮外松开鼠标，没有触发实际启动、安装或下载。

2026-10-29 微软皮肤头像修复：356 项业务断言通过，其中新增 34 项覆盖公开档案、官方 HTTPS 纹理限制、独立层比例、透明四角、预乘合成、旧版皮肤和尺寸限制。Debug 构建无警告，页面检查通过。实机验证已登录账户在侧栏及账户卡片显示相同的真实皮肤头像，切换离线账户后恢复默认头像，再切回微软账户恢复真实头像；最终保留原微软选择。截图位于 `.local/screenshots/microsoft-real-avatar-*.png`。用户另确认此前微软账户能够启动游戏；本次未重复启动游戏或构建 Release。

同日头像轮廓补验：修正之前正脸和帽子同尺寸铺满、强制不透明造成的方角。实机截图 .local/screenshots/microsoft-avatar-layered-corners.png 确认账户页和侧栏均恢复透明轮廓，四角缓存 alpha 均为 0。缓存名称包含合成版本，旧方形缓存不会继续显示。

2026-10-29 首页重构验收：367 项业务断言通过，其中 58 项账户与启动检查和 31 项版本管理检查覆盖阶段里程碑、准备失败日志、写入失败、独立日志、脱敏及 `clientVersion` 解析。x64 Debug 与现有 XAML 检查通过。复用独立目录完成原版、Fabric、Forge、NeoForge 的 27 项真实启动/窗口/停止/取消检查。临时进程内 WinUI 检查验证了普通窗口和最大化等宽缩放、两行启动按钮与版本切换同步、对应版本详情、94% 等待窗口、动画收起、运行状态、快速重启、失败弹窗及模态排队，共 17 项通过；截图与报告位于 `.local/home-ui-qa/`，临时验收入口已移除。未构建 Release，未覆盖其他 DPI；日志文件/文件夹入口验证了启用状态，未自动操作外部记事本和资源管理器窗口。
