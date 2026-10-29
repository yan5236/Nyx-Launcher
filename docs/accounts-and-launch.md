# 账户与真实启动

## 账户

首次启动不生成账户；空状态在内容区居中显示账户图标和“目前还没有账户呢，点击右上角添加”。右上角绿色添加按钮打开原生 WinUI `ContentDialog`。类型卡片左侧有图标，包含微软登录、离线账户和外置登录，目前只有离线账户可用。微软 API 申请尚未完成，外置认证未接入；不会使用其他启动器的 OAuth 客户端凭据。

进入离线输入和返回类型选择均使用 220 毫秒平移淡入，遵循系统动画开关；返回按钮位于标题左侧，底部从左到右为“取消”“确定”。返回保留输入，保存错误留在当前步骤。确认按钮、添加、安装、创建下载及首页启动共用全局绿色配色（默认 #28563B、悬停 #346C4B、按下 #20452F）；运行中的停止按钮保留红色语义。

离线账户名称接受 1–16 个英文字母、数字或下划线，拒绝空白、非法字符和忽略大小写后重复的名称。添加成功自动选中，之后点击整张账户卡片切换，键盘选择同样生效。卡片显示名称、账户类型和左侧选中竖线；头像没有额外底色、内边距或边框。侧栏与首页共用同一个 `AccountStore`。

账户保存到 `%LOCALAPPDATA%/NyxLauncher/accounts.json`，只包含名称和当前选择。同目录临时文件原子替换成功后才更新内存和界面；损坏配置保留原文件并禁止覆盖。已有游戏进程保持启动时的身份，切换账户用于下一次启动。

每张卡片右侧提供圆形重试登录与删除按钮。当前离线重试重新校验名称、创建稳定 UUID 会话并选中该账户；不会发起网络认证。删除仅移除本地账户记录，不删除游戏文件或存档；删除当前账户后选中剩余首项，删完最后一个后清空选择，侧栏与首页同步更新。写入失败保留原列表和选择。

UUID 使用 Java `UUID.nameUUIDFromBytes(UTF8("OfflinePlayer:" + name))` 的 MD5 / RFC 4122 v3 规则，与 [HMCL 的离线账户工厂](https://github.com/HMCL-dev/HMCL/blob/f1c9b77df5f267e9eb3d09ce8881b73ed4ae0c4f/HMCLCore/src/main/java/org/jackhuang/hmcl/auth/offline/OfflineAccountFactory.java)一致。显式传入 CmlLib 会话，避免该库离线会话的随机 UUID 导致同名账户身份漂移。名称大小写会影响 UUID，不自动改写。

当前按用户提供的 Steve / Alex 两张素材采用传统双皮肤规则：Java UUID 哈希的最低位为 0 时显示 Steve，为 1 时显示 Alex，参考 [PCL 的 McSkinSex](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb)。名称恰为 Steve 不代表头像一定是 Steve。现代 Minecraft / HMCL 已扩展默认皮肤集合，本启动器的双头像不承诺与所有新版游戏内部默认皮肤一致，也不注入皮肤模组。

`scripts/Generate-Avatars.ps1` 从完整皮肤的 `(8,8,8,8)` 裁出正脸，并叠加 `(40,8,8,8)` 的透明帽子层，以最近邻放大生成头像；坐标随皮肤分辨率缩放。裁剪位置参考 [HMCL 的头像绘制](https://github.com/HMCL-dev/HMCL/blob/ac378ad46aa07c5d683dd0d82fea2cd4f88e99bc/HMCL/src/main/java/org/jackhuang/hmcl/game/TexturesLoader.java)，原始皮肤保持完整。

## 启动与停止

首页布局保持原状。没有账户或有效版本时不能启动；点击启动后锁定本次版本、目录、隔离设置与账户，按钮立即变为红色“停止”。后台依次执行：

1. 检查资源完整性：校验资源索引和资源对象，按上游长度、SHA-1 复用或补齐。
2. 检查依赖完整性：校验客户端、libraries、日志配置和 Java；使用现有下载器的源策略、并行限制与原子发布。
3. 登录账户：生成所选离线身份的会话，不发起或伪装微软认证。
4. 等待游戏窗口出现：创建 Java 进程并持续排空标准输出和错误输出，检查对应进程的可见游戏窗口。
5. 运行：发现真实窗口后按钮变为“停止运行”，保留实际版本和启动身份。

原版、Fabric、Forge、NeoForge 共用 CmlLib 本地版本及继承解析，保留加载器 JVM 参数。Java 检查从继承链中取有效 Java 声明，避免加载器子清单未声明 Java 时误装旧版 Java 8。缺失且没有下载地址的处理器产物会明确报错，需要重新安装对应加载器，不会伪造文件。

Java 元数据保存在游戏目录 `.nyx-launch/metadata`；首次获取需要网络，之后完整缓存允许离线检查。请求保留系统代理与 HTTPS 校验；取消令牌覆盖底层库未传递令牌的元数据请求。资源损坏时仍需网络修复。

旧版本的 `resources` / `assets/virtual` 资源副本也按哈希检查，同长度损坏会从已验证资源对象原子修复。副本目标与下载目标使用相同的游戏根目录及链接检查，清单不能通过复制后处理写入外部目录。

窗口检测参考 [PCL 的 ModWatcher](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModWatcher.vb)：用 `EnumWindows`、窗口所属 PID、可见性和 GLFW/LWJGL/SDL/AWT 类名共同判断，排除辅助窗口及加载器早期窗口，不要求标题以 Minecraft 开头。[HMCL 的 LauncherHelper](https://github.com/HMCL-dev/HMCL/blob/ac378ad46aa07c5d683dd0d82fea2cd4f88e99bc/HMCL/src/main/java/org/jackhuang/hmcl/game/LauncherHelper.java)主要通过 LWJGL 日志推进启动；这里不把日志或进程存在等同于窗口已出现。窗口出现也不等于世界或主菜单已加载完成。

准备、登录和等待窗口期间均可取消；运行中先请求正常关闭窗口，三秒内未退出则终止本次进程树。停止可能中断尚未保存的游戏进度。不会终止其他 Java 进程。正常退出恢复启动按钮；异常退出显示退出码和末尾日志。关闭启动器同样停止其持有的游戏及下载工作并等待回收。

## 验证入口

```powershell
.\.dotnet\dotnet.exe run --project tests/NyxLauncher.Checks
.\.dotnet\dotnet.exe run --project tests/NyxLauncher.Checks -- --live-launch .local/download-install-qa
```

第二条只用于此前 `--live-install` 创建的独立验收目录，要求存在 `qa-vanilla`、`qa-Fabric`、`qa-Forge`、`qa-NeoForge`。它会备份并临时损坏两个小文件以检查修复、验证断网暖缓存检查，随后真实启动四个客户端、等待窗口并停止，最后验证各准备阶段取消。不要指向日常使用的游戏目录。

2026-10-29 UI 调整验收：x64 Debug 构建与 XAML 检查通过，269 项业务断言通过（其中账户与启动 48 项）。实机验证添加弹窗的图标、输入校验、返回保留输入、按钮顺序、动画、新增/重试/删除测试账户及居中空状态；原账户保留，空状态验收后原配置逐字节恢复。截图位于 `.local/screenshots/ui-polish-*.png`。本次未重跑游戏安装/启动全流程、其他 DPI 或 Release 构建；微软与外置认证仍未接入。

同日悬停补验：前述静止颜色检查遗漏了 WinUI 状态资源回落到系统蓝色的问题。按钮资源现集中在 `Themes/AccentButtonResources.xaml`，由应用、页面和独立弹窗显式合并。真实鼠标移动/按住后采样确认、添加账户、启动、安装、下载按钮，三种状态均分别为 `#28563B` / `#346C4B` / `#20452F`；验收在按钮外松开鼠标，没有触发实际启动、安装或下载。
