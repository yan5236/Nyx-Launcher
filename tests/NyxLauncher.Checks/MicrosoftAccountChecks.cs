using System.Net;
using System.Text;
using System.Text.Json;
using NyxLauncher.Services;

internal static class MicrosoftAccountChecks
{
    internal const string ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    internal const string RedirectUri = "http://localhost:43871/nyxlauncher/oauth/callback";
    private const string Uuid = "1234567890abcdef1234567890abcdef";
    private static int _checks;

    /// <summary>覆盖微软认证协议、加密持久化和旧账户迁移；只用合成令牌与临时文件，不访问用户真实账户。</summary>
    public static async Task RunAsync()
    {
        VerifySettings();
        VerifyAccounts();
        await VerifyProtocolAsync();
        Check(GameLaunchSession.Redact("token=secret-token!", "secret-token") == "token=[已隐藏令牌]!", "游戏错误日志脱敏");
        Console.WriteLine($"通过 {_checks} 项微软账户检查。");
    }

    /// <summary>使用已有独立验收目录构造四类真实版本的微软启动参数；仅使用合成会话，不启动游戏、不输出令牌。</summary>
    public static async Task VerifyLaunchArgumentsAsync(string root)
    {
        var versions = InstalledVersionService.Scan(root);
        var account = new GameAccount("MicrosoftTest", Uuid, "test-home", MicrosoftAccountChecks.ClientId, "AAAA");
        foreach (string name in new[] { "qa-vanilla", "qa-Fabric", "qa-Forge", "qa-NeoForge" })
        {
            var version = versions.Single(x => x.Name == name && x.Error is null);
            var session = new CmlLib.Core.Auth.MSession
            { Username = account.Name, UUID = Uuid, UserType = "msa", AccessToken = "synthetic-minecraft-token", Xuid = "123456" };
            using var process = await GameLaunchService.BuildAsync(root, version, IsolationMode.All, account, authenticatedSession: session);
            string arguments = process.StartInfo.Arguments;
            Check(new[] { account.Name, Uuid, "synthetic-minecraft-token", "msa", "123456", MicrosoftAccountChecks.ClientId }
                .All(arguments.Contains), name + "微软会话完整进入启动参数");
            session.UUID = new string('b', 32);
            try
            {
                using var rejected = await GameLaunchService.BuildAsync(root, version, IsolationMode.All, account, authenticatedSession: session);
                throw new Exception("错误 UUID 意外生成进程。");
            }
            catch (InvalidOperationException) { _checks++; }
        }
        Console.WriteLine($"通过 {_checks} 项真实本地版本的微软启动参数检查，未启动游戏。");
    }

    /// <summary>验证示例资源可独立加载，损坏或空配置不回退；拒绝远端、查询或凭据等可能外泄授权码的回调。</summary>
    private static void VerifySettings()
    {
        var settings = MicrosoftLoginSettings.Load();
        settings.Validate();
        Check(settings.ClientId == ClientId && settings.RedirectUri == RedirectUri, "从内嵌示例 JSON 读取配置，不依赖开发者实际配置");
        using var empty = new MemoryStream(Encoding.UTF8.GetBytes("null"));
        Throws<IOException>(() => MicrosoftLoginSettings.Read(empty), "空配置不回退旧客户端");
        using var broken = new MemoryStream(Encoding.UTF8.GetBytes("{"));
        Throws<JsonException>(() => MicrosoftLoginSettings.Read(broken), "损坏配置不回退");
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("""{"ClientId":"not-an-id","RedirectUri":"http://localhost/callback"}"""));
        Throws<ArgumentException>(() => MicrosoftLoginSettings.Read(invalid), "读取时校验配置字段");
        foreach (string redirect in new[] { "https://example.com/auth", "http://localhost.evil:29116/auth", "http://user@localhost/auth",
            "http://localhost/auth?q=1", "http://localhost/auth#x" })
            Throws<ArgumentException>(() => (settings with { RedirectUri = redirect }).Validate(), "拒绝错误回调");
        Throws<ArgumentException>(() => (settings with { ClientId = "not-an-id" }).Validate(), "拒绝无效客户端 ID");
        var production = new MicrosoftLoginSettings("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "http://localhost:29117/auth");
        production.Validate();
        Check(production.RedirectUri == "http://localhost:29117/auth", "允许独立应用回调配置");
    }

    /// <summary>验证旧配置迁移、同名不同类型、UUID 改名更新、加密重启、删除与失败原子性。</summary>
    private static void VerifyAccounts()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-microsoft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "accounts.json");
            File.WriteAllText(path, """{"Accounts":[{"Name":"Steve"}],"Selected":"Steve"}""");
            var store = new AccountStore(path);
            var offline = store.Selected!;
            Check(offline is { Name: "Steve", IsMicrosoft: false }, "读取旧离线账户且身份不漂移");
            byte[] plain = Encoding.UTF8.GetBytes("synthetic-refresh-token");
            string encrypted = MicrosoftLoginService.Protect(plain);
            Check(plain.All(x => x == 0), "加密后清理序列化明文缓冲区");
            Check(Encoding.UTF8.GetString(MicrosoftLoginService.Unprotect(encrypted)) == "synthetic-refresh-token", "当前 Windows 用户可解密缓存");
            Throws<InvalidOperationException>(() => MicrosoftLoginService.Unprotect("AAAA"), "损坏缓存要求重新登录");
            var online = new GameAccount("Steve", Uuid, "test-home-account", MicrosoftAccountChecks.ClientId, encrypted);
            store.SaveMicrosoft(online);
            Check(store.Accounts.Count == 2 && store.Selected == online, "允许微软与离线同名且自动选中微软");
            string saved = File.ReadAllText(path);
            Check(!saved.Contains("synthetic-refresh-token") && !online.ToString().Contains(encrypted), "配置和诊断不含明文认证信息");
            Check(new AccountStore(path).Selected == online, "加密账户与选择重启恢复");
            Throws<InvalidOperationException>(() => GameLaunchService.CreateSession(online), "微软身份禁止生成离线会话");
            store.Select(offline);
            var renamed = online with { Name = "Renamed" };
            store.SaveMicrosoft(renamed, online, false);
            Check(store.Selected == offline && store.Accounts.Count == 2 && store.Accounts.Contains(renamed), "按 UUID 更新改名并保留另选身份");
            Throws<InvalidOperationException>(() => store.SaveMicrosoft(online, online), "拒绝过期并发刷新写回");
            Throws<InvalidOperationException>(() => store.SaveMicrosoft(renamed with { ProfileId = new string('b', 32) }, renamed), "重登禁止换成其他游戏 UUID");
            store.Select(renamed);
            store.Remove(renamed);
            Check(store.Selected == offline && !File.ReadAllText(path).Contains(encrypted), "删除微软账户同时删除本地密文");
            Throws<InvalidOperationException>(() => store.SaveMicrosoft(renamed, renamed), "取消后迟到刷新不能复活已删账户");
            store.SaveMicrosoft(online);
            File.Delete(path); Directory.CreateDirectory(path);
            Throws<UnauthorizedAccessException>(() => store.SaveMicrosoft(renamed, online), "刷新写入失败报错");
            Check(store.Selected == online && store.Accounts.Contains(online), "写入失败不更改当前身份");
            foreach (var invalid in new[] { online with { ProfileId = "bad" }, online with { MicrosoftAccountId = null },
                online with { ProtectedTokenCache = "bad!" }, offline with { ClientId = MicrosoftAccountChecks.ClientId } })
                Throws<ArgumentException>(invalid.Validate, "拒绝不完整认证身份");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>按真实请求顺序验证票据、RP、Bearer 和最终会话；覆盖白名单、儿童账户、无资格、无档案及取消。</summary>
    private static async Task VerifyProtocolAsync()
    {
        int count = 0;
        using (var http = new HttpClient(new Handler(async (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            count++;
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            string host = request.RequestUri!.Host;
            if (host == "user.auth.xboxlive.com")
            {
                using var json = JsonDocument.Parse(body);
                Check(json.RootElement.GetProperty("Properties").GetProperty("RpsTicket").GetString() == "d=ms-token", "Xbox 使用 MSAL d= 票据");
                Check(request.Headers.Authorization is null, "OAuth 令牌不放通用 Bearer 头");
                return Response("""{"Token":"xbox-token","DisplayClaims":{"xui":[{"uhs":"hash"}]}}""");
            }
            if (host == "xsts.auth.xboxlive.com")
            {
                using var json = JsonDocument.Parse(body);
                string party = json.RootElement.GetProperty("RelyingParty").GetString()!;
                Check(json.RootElement.GetProperty("Properties").GetProperty("UserTokens")[0].GetString() == "xbox-token", "XSTS 使用 Xbox 票据");
                return party == "rp://api.minecraftservices.com/"
                    ? Response("""{"Token":"xsts-token","DisplayClaims":{"xui":[{"uhs":"hash"}]}}""")
                    : Response("""{"Token":"live-token","DisplayClaims":{"xui":[{"uhs":"hash","xid":"123456"}]}}""");
            }
            if (request.RequestUri.AbsolutePath.EndsWith("login_with_xbox"))
            {
                Check(body.Contains("XBL3.0 x=hash;xsts-token") && !body.Contains("live-token"), "Minecraft 使用自身 RP 的 XSTS 票据");
                return Response("""{"access_token":"minecraft-token","expires_in":86400}""");
            }
            Check(request.Headers.Authorization?.ToString() == "Bearer minecraft-token", "档案和资格使用 Minecraft 令牌");
            return request.RequestUri.AbsolutePath.EndsWith("mcstore")
                ? Response("""{"items":[{"name":"game_minecraft"}]}""")
                : Response("{\"id\":\"" + Uuid + "\",\"name\":\"Player\"}");
        })))
        {
            var session = await MinecraftAuthentication.AuthenticateAsync(http, "ms-token", null, default);
            Check(count == 6 && session.Username == "Player" && session.UUID == Uuid && session.UserType == "msa" &&
                session.AccessToken == "minecraft-token" && session.Xuid == "123456", "完整认证会话与 XUID");
        }
        foreach (var scenario in new[] { "forbidden", "child", "ownership", "profile", "mismatch", "invalid-json", "oversize", "cancel" })
        {
            using var cancel = new CancellationTokenSource();
            using var http = new HttpClient(new Handler((request, token) =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (scenario == "cancel") { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
                if (scenario == "invalid-json") return Task.FromResult(Response("broken secret-token"));
                if (scenario == "oversize") return Task.FromResult(Response(new string('a', 1024 * 1024 + 1)));
                if (path == "/xsts/authorize" && scenario == "child")
                    return Task.FromResult(Response("""{"XErr":2148916238,"secret":"secret-token"}""", HttpStatusCode.Unauthorized));
                if (path.EndsWith("login_with_xbox")) return Task.FromResult(scenario == "forbidden"
                    ? Response("secret-token", HttpStatusCode.Forbidden) : Response("""{"access_token":"minecraft-token"}"""));
                if (path.EndsWith("mcstore")) return Task.FromResult(Response(scenario == "ownership" ? """{"items":[]}""" : """{"items":[{}]}"""));
                if (path.EndsWith("profile")) return Task.FromResult(Response("secret-token", HttpStatusCode.NotFound));
                string hash = scenario == "mismatch" && path == "/xsts/authorize" ? "different" : "hash";
                return Task.FromResult(Response("{\"Token\":\"token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"" + hash + "\",\"xid\":\"123\"}]}}"));
            }));
            try { await MinecraftAuthentication.AuthenticateAsync(http, "ms-token", null, cancel.Token); throw new Exception("错误场景意外通过：" + scenario); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OperationCanceledException)
            {
                Check(!ex.Message.Contains("secret-token"), "异常不泄露服务端正文：" + scenario);
                Check(scenario switch
                {
                    "forbidden" => ex.Message.Contains("白名单"), "child" => ex.Message.Contains("家庭"),
                    "ownership" => ex.Message.Contains("资格"), "profile" => ex.Message.Contains("档案"),
                    "mismatch" => ex.Message.Contains("不一致"), "cancel" => ex is OperationCanceledException,
                    "invalid-json" => ex.Message.Contains("无效数据"), _ => ex is HttpRequestException
                }, "错误分类准确：" + scenario);
            }
        }
    }

    /// <summary>构造有限的合成认证响应；测试数据不包含真实账户或令牌。</summary>
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>对具体业务结果计数，错误时立即终止并指出失败行为。</summary>
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); _checks++; }

    /// <summary>要求无效输入或保存失败抛出预期异常，防止错误被吞掉或误报成功。</summary>
    private static void Throws<T>(Action action, string name) where T : Exception
    { try { action(); } catch (T) { _checks++; return; } throw new Exception(name); }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>保留真实请求及取消令牌，交给各场景检查请求顺序和内容。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
