# 账户与启动

## 身份与持久化

首次使用账户列表为空，支持离线、[微软](microsoft-login.md)及[外置](external-login.md)账户。添加成功自动选中，侧栏、账户页与下一次启动同步。离线名称要求合法且不重复，UUID 按名称稳定生成；微软使用真实游戏 UUID，外置身份同时包含服务器与角色，三类身份不相互替代。

账户保存在 `%LOCALAPPDATA%/NyxLauncher/accounts.json`。认证缓存由当前 Windows 用户 DPAPI 加密，不保存密码；写入成功才更新内存，损坏配置保留原文件并禁止覆盖。重登须保持原身份，允许同 UUID 改名。删除只移除本地记录与缓存，不删除游戏文件、不撤销服务端授权，也不退出系统浏览器。

启动固定本次账户，运行中切换只影响下一次启动。刷新完成时重新检查记录，避免迟到结果恢复已删除身份或覆盖用户的新选择。

## 头像

账户页、侧栏和选角列表复用头像控件。微软头像读取 Mojang 公开档案和官方纹理；外置头像受服务器声明的 HTTPS 纹理域限制，请求不携带游戏访问令牌。正脸与帽子层保留透明度，失败使用已有缓存或默认 Steve/Alex。

头像缓存在用户数据目录的 `avatars` 下，外置缓存区分服务器和角色。同一进程共享查询，换肤后重启刷新。双默认头像不承诺与所有新版游戏内默认皮肤集合一致，不注入皮肤模组。

## 启动与停止

首页需要有效版本和所选账户。一次启动固定目录、隔离、Java、内存、高级设置与身份，依次检查资源、依赖、账户认证，再创建 Java 进程并等待对应的真实可见窗口。准备进度表示阶段，不是总下载字节比例。

原版、Fabric、Forge、NeoForge 共用继承解析并保留加载器参数。缺失资源按完整性信息补齐，Java 选择见[Java 管理](java-management.md)。微软只尝试静默获取/刷新，外置先验证或刷新；需要重新登录或认证失败时中止，不降级为离线账户。处理器产物缺失且无下载来源时要求修复安装，不伪造文件。

检测到窗口后进入运行状态，不代表主菜单或世界加载完成。准备、认证及等待窗口均可取消；运行中先请求关闭，超时后结束本次进程树，不结束其他 Java 进程。强制停止可能中断未保存进度。关闭启动器也会停止其持有的游戏并回收工作。

## 故障与日志

准备或认证失败显示启动失败；游戏异常退出显示崩溃诊断，主动停止不作为崩溃。诊断覆盖内存、Java、图形和常见模组问题，无法识别时说明未知。加载器错误保留模组名称、ID、版本约束与上下文，规则匹配只是可能原因。

弹窗可打开日志、打开目录或导出 ZIP。优先收集本轮实际游戏目录的日志和崩溃报告，再加入游戏进程输出及本次启动日志；进程创建前失败也能查看和导出启动诊断。旧报告不作为本轮证据，大文件仅保留头尾。导出先完成临时文件，取消不写入，失败不破坏已有目标。

日志显示和导出过滤已知令牌与常见认证参数，但第三方模组输出仍可能包含其他个人信息，原始游戏日志不因导出而改写。提交问题时提供经检查的导出日志、触发步骤和环境范围，不提交账户配置或完整认证命令行。

公共下载、MSAL 及 Xbox/Minecraft 请求在 `%LOCALAPPDATA%/NyxLauncher/.nyx-launch/logs` 留下安全网络诊断：请求关联 ID、目标主机、耗时、状态和异常类型/错误码。不记录 URL 路径、查询、请求头、正文或原始异常正文；成功记录有数量限制，失败仍记录。认证客户端与公共下载池分离，保留系统代理及 TLS 校验。

## 维护入口

- [AccountStore](../src/NyxLauncher/Services/AccountStore.cs)、[GameAccount](../src/NyxLauncher/Services/GameAccount.cs)：身份与存储。
- [GameLaunchSession](../src/NyxLauncher/Services/GameLaunchSession.cs)、[GameLaunchService](../src/NyxLauncher/Services/GameLaunchService.cs)：启动快照、阶段与进程。
- [GameCrashReport](../src/NyxLauncher/Services/GameCrashReport.cs)、[NetworkDiagnosticHandler](../src/NyxLauncher/Services/NetworkDiagnosticHandler.cs)：日志收集、导出及网络诊断。
- 检查选择见[验证指南](testing.md)，不以历史本机验收替代当前回归。
