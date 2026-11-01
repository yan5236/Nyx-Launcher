# 外置登录

支持 LittleSkin 及提供 Yggdrasil API 的 Blessing Skin 等皮肤站。依据 [authlib-injector 启动器规范](https://yushijinhun.github.io/authlib-injector/zh/启动器技术规范.html)、[Yggdrasil 服务端规范](https://yushijinhun.github.io/authlib-injector/zh/Yggdrasil-服务端技术规范.html)和 [LittleSkin 手册](https://manual.littleskin.cn/yggdrasil/)实现。皮肤站需要启用 Yggdrasil 服务，仅安装 Blessing Skin 主程序并不代表提供认证接口。

## 使用

账户 → 添加账户 → 外置登录，显示认证服务器目录。首次使用默认提供 LittleSkin（`https://littleskin.cn/api/yggdrasil/`），点击后直接输入账户和密码。点击底部“取消”右侧的“登录”获取角色，以头像和角色名列表选择，再点击“完成登录”。服务器已自动绑定单个角色时仍显示该角色供确认。

右上角深色“创建认证服务器”按钮允许输入链接及可选显示名称，点击底部“确定”后解析 HTTPS 重定向、ALI 地址指示和认证元数据。无协议地址补全 HTTPS。目录卡片支持编辑、删除；默认 LittleSkin 也可删除，重启不会自动恢复。编辑地址或删除目录项不改变已有账户的认证地址、令牌或游戏文件，需要在新地址重新添加账户。

可以将皮肤站的外置登录卡片拖入启动器内容区或添加账户弹窗。读取标准 `text/plain` 数据 `authlib-injector:yggdrasil-server:<URI 编码的 API 地址>`，验证后按规范地址去重添加并进入对应服务器登录。普通文本不触发网络请求。登录忙碌或账户重登时不接收新的导入。

外置流程使用与离线步骤相同的 220 毫秒平移淡入，前进与返回方向相反，尊重系统动画开关。返回选角上一步需重新认证，关闭弹窗取消在途请求，迟到结果不会保存。

## 身份与启动

服务器目录原子保存到 `%LOCALAPPDATA%/NyxLauncher/auth-servers.json`。账户沿用 `accounts.json`，外置身份键包含服务器地址、登录标识和角色 UUID，不与微软或离线身份混用。密码不持久化；访问令牌和客户端令牌使用当前 Windows 用户 DPAPI 加密。认证 POST 禁止自动重定向，只接受 HTTPS 服务器；HTTP-only 站点不支持。

启动前先验证令牌，403 时刷新；失败提示重新登录，不退回离线身份。重登锁定原服务器、登录标识与角色 UUID，允许同 UUID 角色改名。移除或替换账户后，迟到刷新不能复活旧记录。

启动时使用官方 authlib-injector 1.2.8（build 56），缓存到游戏目录 `.nyx-launch/authlib-injector-1.2.8.jar`，下载后和每次使用前验证固定 SHA-256。使用现有下载器的并行限制、代理、取消和原子写入。构造 Java 进程时在主类之前添加 `-javaagent` 及 API 元数据预取参数，保留加载器已有参数。游戏日志沿用访问令牌脱敏。

## 头像

账户卡片、侧栏和选角列表复用 `AccountAvatar`。外置角色从对应服务器的公开 `sessionserver/session/minecraft/profile/<UUID>` 读取纹理，不附带登录令牌。纹理仅允许认证元数据声明的 HTTPS 域和站点自身域，拒绝凭据、IP、本机地址及非默认端口；微软仍只允许官方纹理域。保留 PNG 大小、像素尺寸、正脸与帽子层合成检查。

外置头像缓存键包含服务器地址的 SHA-256 与角色 UUID，避免跨站同 UUID 污染；缓存存放在原有 avatars 目录。网络失败保留旧头像，无缓存时显示默认头像。当前进程同一角色复用查询，换肤后重启启动器更新。

## 维护入口

[ExternalLoginService](../src/NyxLauncher/Services/ExternalLoginService.cs)、[ExternalServerStore](../src/NyxLauncher/Services/ExternalServerStore.cs)、[AuthlibInjector](../src/NyxLauncher/Services/AuthlibInjector.cs)。验证入口统一见[验证指南](testing.md)。
