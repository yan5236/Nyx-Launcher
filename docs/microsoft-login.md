# 微软登录

## 客户端配置

配置结构以 [config/microsoft-auth.example.json](../config/microsoft-auth.example.json) 为准。首次构建前复制为同目录的 `microsoft-auth.json`，将 ClientId 填为自己的公共客户端应用 ID，RedirectUri 填为该应用登记的本机回调；已有实际配置不要覆盖。示例 ID 只是占位值，实际配置被 Git 忽略，缺少文件时构建失败，不回退测试客户端。

使用面向个人微软账户的移动和桌面公共客户端，申请 Xbox 登录权限，不配置 client secret。回调只支持 HTTP localhost，登记时保留实际路径、大小写和尾斜杠形式；运行时校验 Host、端口和精确路径。授权应用配置和 Minecraft 服务访问资格是不同条件，不能仅凭 ClientId 格式有效认定登录可用。

Debug 默认将本地实际 JSON 嵌入程序集；正式发布脚本显式选择仓库内仅含公共客户端 ID 与回调的 [microsoft-auth.release.json](../config/microsoft-auth.release.json)，流程见[打包与 Tag 发布](development.md#打包与-tag-发布)。两者运行时均不依赖开发机路径或外置配置。修改后需要重新构建；旧用户数据目录中的认证配置不再读取。客户端 ID 是公开应用标识，Git 忽略和嵌入资源不构成保密。不要填入密码、授权码、令牌或 secret。切换客户端后已有账户需要重新登录。

## 认证流程

1. 添加微软账户时先绑定配置的 IPv4 回环端口，再打开系统浏览器。弹窗提供重新打开浏览器、复制链接和取消；打开失败可以使用同一次授权链接继续。
2. MSAL 管理授权码、随机 state、PKCE S256 与令牌缓存，启动器不再实现第二套 PKCE。密码、验证码和授权同意由用户在微软页面完成。
3. 回调校验 Host、路径和 state，拒绝重复参数、无效请求和超大请求头；无效请求不终止正确授权。取消、超时或退出释放监听，迟到结果不保存。
4. 微软令牌换取 Xbox / XSTS 和 Minecraft 会话，核验游戏资格与真实 Java 档案，全部成功才保存并选中账户。

同一时间仅处理一次微软认证，端口占用明确报错，不静默改用其他回调。回调页面只提示返回启动器，不提前宣称游戏认证成功。浏览器授权链接在完成、取消或超时后失效。

启动游戏只尝试静默获取或刷新，再验证 Xbox 与 Minecraft，不自动打开浏览器。认证、资格或网络错误均终止启动，不降级离线。重登保持原游戏 UUID，改名按 UUID 更新；其他身份通过添加入口处理。账户切换、删除和迟到刷新保护见[账户与启动](accounts-and-launch.md)。

## 凭据与诊断

MSAL 缓存使用 Windows 当前用户 DPAPI 加密后与账户元数据原子保存，不持久化密码或明文令牌。复制到另一 Windows 用户不能直接解密；删除账户只清除本地缓存，不撤销授权或清理浏览器会话。

错误提示保留受约束的认证错误码，不直接展示可能包含敏感信息的响应。认证请求使用官方 HTTPS 端点，不走下载镜像，不关闭证书验证，不跟随认证 POST 重定向；仅本机回调使用 HTTP。请求诊断与日志导出规则见[账户与启动](accounts-and-launch.md#故障与日志)。

## 维护与检查

- [MicrosoftLoginSettings](../src/NyxLauncher/Services/MicrosoftLoginSettings.cs)：嵌入配置和校验；[项目文件](../src/NyxLauncher/NyxLauncher.csproj)：配置打包及 MSAL 版本。
- [MicrosoftLoopbackBrowser](../src/NyxLauncher/Services/MicrosoftLoopbackBrowser.cs)：系统浏览器与 TCP 回调适配；升级 MSAL 时复核自定义回调接口兼容性。
- [MicrosoftLoginService](../src/NyxLauncher/Services/MicrosoftLoginService.cs)、[MinecraftAuthentication](../src/NyxLauncher/Services/MinecraftAuthentication.cs)：令牌、服务端资格及档案校验。
- [验证指南](testing.md)：离线检查、真实 MSAL 的合成回调场景和启动参数检查。合成回调通过不代表真实账户登录或续期已通过。
