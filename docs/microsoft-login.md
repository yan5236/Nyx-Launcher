# 微软登录

## 客户端配置

当前实现使用 Microsoft.Identity.Client（MSAL.NET）4.92.0 和系统浏览器，申请个人微软账户的 `XboxLive.signin` 权限。MSAL 负责授权码、随机 state、PKCE S256、微软令牌及刷新缓存，自动处理其必要的 OIDC / 离线访问 scope；不需要也不接收客户端 secret。

Debug 没有本机配置时使用用户明确提供的测试值：

```json
{
  "ClientId": "288ec5dd-6736-4d4b-9b96-30e083a8cad2",
  "RedirectUri": "http://localhost:29116/authentication-response"
}
```

正式接入时替换测试客户端配置。

正式应用获批后，在 `%LOCALAPPDATA%/NyxLauncher/microsoft-auth.json` 写入同结构配置，替换 `ClientId` 为自己的应用 ID，`RedirectUri` 填应用后台登记的桌面公共客户端回调。若沿用当前回调，必须精确保留路径、大小写及无尾斜杠形式。只接受 `http://localhost` 回环 URI；配置错误明确报错，不偷偷回退测试 ID。Release 禁止使用此测试 ID。

配置每次认证读取，切换客户端后须在账户卡片点击“重试登录”。客户端 ID 是公共应用标识，不是密码；不要在配置中填 client secret、密码、授权码或令牌。无需提前创建本机配置文件即可进行 Debug 测试。

## 登录与账户生命周期

1. 账户 → 添加 → 微软登录。测试配置的入口显示“测试应用 · 仅供开发验证”。
2. 先独占绑定 `127.0.0.1:29116`，再打开系统浏览器。弹窗居中显示进度和提示，底部提供“打开浏览器”“复制链接”“取消”。自动打开失败不会结束认证，可重复尝试打开或复制同一授权链接到本机浏览器继续验证；回调完成、取消或超时后链接入口失效。微软密码、验证码及同意授权由用户在微软页面完成；启动器不读取密码页面。
3. 回调只接受精确 Host、路径和当前 state，拒绝重复参数、同时提交 code 与 error、超大请求头；无效请求不会终止正确登录。收到回调的浏览器页面只提示返回启动器，不提前声称登录成功。
4. 微软访问令牌依次换取 Xbox 用户票据、Minecraft RP 的 XSTS、Minecraft 令牌，核验游戏资格和真实 Java 档案。XSTS 缺少 XUID 时从 Xbox Live RP 补取声明并校验用户哈希一致。
5. 全部成功才保存并选中账户。身份使用 Minecraft 返回的 UUID 和名称，不使用邮箱或 Xbox 昵称冒充游戏名称。返回 `msa` 会话、游戏访问令牌、XUID 和客户端 ID 给 CmlLib 启动参数。

同一时间只处理一次微软认证；等待浏览器最多五分钟。关闭弹窗、按取消或退出启动器都会取消在途请求并释放端口，迟到结果不写入账户。端口被占用时明确提示，不自动改变已登记回调。IPv4 回环监听避免系统 HTTP.sys URL 注册依赖，浏览器的 localhost 连接可回落至 IPv4。

真实皮肤头像独立于令牌认证：按 UUID 查询 Mojang 公开档案并从官方纹理域取图，已有账户无需重新授权。账户页与侧栏共用裁剪、帽子层叠加和本地缓存，见[账户头像](accounts-and-launch.md)。

游戏启动时仅静默获取或刷新微软令牌，再重新验证 Xbox 和 Minecraft；不会自动打开浏览器。刷新失败、缺少档案、无资格、白名单拒绝或网络异常都终止此次启动，不降级为离线账户。重新登录必须保持同一个游戏 UUID；登录其他身份需使用添加入口。游戏改名按 UUID 更新原卡片。后台刷新时切换到其他账户不会被刷新结果改回；已经删除或更新的账户不能被迟到结果覆盖或复活。

## 凭据与错误处理

每个微软账户的 MSAL 缓存使用 `ProtectedData` / DPAPI `CurrentUser` 加密，再与账户元数据一起原子写入 `accounts.json`。其中包括用于续期的认证材料，但不保存密码和明文令牌；Minecraft 游戏访问令牌仅在本次会话内使用。复制配置到其他 Windows 用户不能直接解密，需要重新登录。删除卡片同时删除本地密文，不撤销微软侧应用授权、不清理系统浏览器登录状态。

错误面板保留经过字符约束的 MSAL 错误码及 `AADSTS` 编号，避免原样展示可能含敏感数据的服务端响应。Xbox 的未建立档案、儿童账户、地区和年龄限制，Minecraft 的 403、无资格及缺失档案分别给出提示。游戏日志显示前删除本次游戏访问令牌，避免异常退出时把令牌带到界面。

程序不会禁用 TLS 验证，不使用下载镜像传输认证信息。只有微软授权回调使用本机 HTTP；认证服务请求使用固定官方 HTTPS 端点且不跟随 HTTP 重定向。

## 文档依据与回调兼容性

- [微软 OAuth 授权码与 PKCE 文档](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow)：公共客户端不使用 secret，授权码兑换需一致回调及 PKCE verifier。
- [MSAL 桌面交互与自定义 Web UI](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-interactive)：使用官方 `ICustomWebUi` 接口将系统浏览器回环结果交回 MSAL，认证和 PKCE 仍由库执行。
- [CmlLib 的 MSAL 指南](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/cmllib.core.auth.microsoft/authentication-with-msal/)与[客户端 ID 注册说明](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game.msal/clientid/)：个人账户、Xbox scope、公共客户端回调和 Minecraft API 白名单是不同步骤。
- [CmlLib 认证源码](https://github.com/CmlLib/CmlLib.Core.Auth.Microsoft/tree/2fd9c344ad36386eec4abbc2ddc911ca756cf66d/src)：核对 Xbox / XSTS、Minecraft 资格、档案及 `msa` 会话映射。

本机验证中，MSAL 默认 HTTP.sys 监听出现 `http_listener_error` / “句柄无效”，并为路径自动附加 `/`。因此使用只监听回环地址的 TCP 适配器，严格接受用户指定的无尾斜杠路径，不修改 Windows HTTP 配置。MSAL 4.92 的 API 注释已将 `ICustomWebUi` 标为未来弃用方向；当前固定版本仍支持，正式发布及升级 MSAL 前需要复核此扩展的支持状态。适配器仅打开微软系统浏览器页面，不提供可注入密码的自制登录 UI。

## 验证

```powershell
.\.dotnet\dotnet.exe run --project tests/NyxLauncher.Checks
.\.dotnet\dotnet.exe run --project tests/NyxLauncher.Checks -- --microsoft-callback
.\.dotnet\dotnet.exe run --project tests/NyxLauncher.Checks -- --microsoft-launch-arguments .local/download-install-qa
.\.dotnet\dotnet.exe build NyxLauncher.slnx -c Debug
.\scripts\Test-Prototype.ps1
```

`--microsoft-callback` 使用固定的公开发现文档、真实 MSAL 和真实 `localhost:29116` TCP 回调，不打开浏览器也不交换真实用户令牌。验证精确客户端/回调、PKCE、无效 state / Host / 路径 / 重复参数、拒绝授权、取消以及端口释放；同时覆盖未打开浏览器时保留原链接、手动继续验证及结束后链接清理。

2026-10-29 弹窗调整验证：410 项业务检查、3 个 MSAL 回调场景、2 个手动恢复/取消场景、Debug 构建（0 警告/错误）及页面结构检查通过。桌面工具及备用入口因沙箱初始化失败无法启动，本次未验证实际布局截图、按钮点击、系统浏览器故障恢复或真实账户授权。

`--microsoft-launch-arguments` 复用已有独立安装验收目录的四类 1.21.1 版本，以合成会话构造真实启动参数并校验身份不匹配保护，不创建游戏进程，也不输出完整命令行。2026-10-29 通过 8 项检查，确认名称、UUID、Minecraft 令牌、`msa`、XUID 和客户端 ID 进入参数。

2026-10-29：322 项业务断言通过（含新增 53 项微软账户检查），覆盖合成令牌交换、错误映射、账户迁移、DPAPI 往返、原子保存和日志脱敏。Debug 构建与页面检查通过。桌面自动化因工具沙箱初始化失败未完成；用户在当前 Debug 界面点击微软登录并完成系统浏览器授权。随后只读检查确认账户文件由 1 个离线账户变为 1 个离线账户和 1 个微软账户，微软账户已选中、客户端 ID 匹配、真实玩家 UUID 与加密缓存已保存，回调端口释放且窗口保持响应。账户仅在微软、Xbox/XSTS、Minecraft 资格与档案校验全部通过后保存，因此本次真实登录链路通过。未执行微软账户游戏启动、联机、跨重启静默续期或 Release 构建。
