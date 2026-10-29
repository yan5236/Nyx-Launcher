using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using NyxLauncher.Services;

internal static class ExternalLoginChecks
{
    private static int _checks;
    private const string Id = "1234567890abcdef1234567890abcdef";
    private const string OtherId = "abcdefabcdefabcdefabcdefabcdefab";
    private static readonly ExternalProfile Profile = new(Id, "SkinPlayer");

    /// <summary>用合成凭据验证服务器发现、角色绑定、刷新、加密存储和启动参数，不接触真实用户密码。</summary>
    public static async Task RunAsync()
    {
        Check(ExternalServerStore.Normalize("littleskin.cn/api/yggdrasil") == ExternalServerStore.LittleSkin.Url, "补全 HTTPS");
        Check(ExternalServerStore.Normalize("authlib-injector:yggdrasil-server:https%3A%2F%2Flittleskin.cn%2Fapi%2Fyggdrasil") == ExternalServerStore.LittleSkin.Url, "标准拖拽卡片");
        foreach (string url in new[] { "http://example.com", "file:///c:/test", "https://user:pass@example.com", "https://example.com/?password=x", "https://example.com/#test" })
            await RejectAsync(() => Task.FromResult(ExternalServerStore.Normalize(url)), "拒绝不安全认证地址");
        using (var service = Service(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/")
            {
                var response = Response("{}"); response.Headers.Add("X-Authlib-Injector-API-Location", "/api/yggdrasil"); return response;
            }
            return Response("""{"meta":{"serverName":"Test Skin"},"signaturePublickey":"test-key"}""");
        }))
        {
            var found = await service.DiscoverAsync("https://example.com", default);
            Check(found.Server.Url == "https://example.com/api/yggdrasil/" && found.Server.Name == "Test Skin", "相对 ALI 元数据发现");
        }
        using (var service = Service(_ => Response("<html>wrong</html>")))
            await RejectAsync(() => service.DiscoverAsync("https://example.com", default), "拒绝非认证页面");
        var server = ExternalServerStore.LittleSkin;
        ExternalTokens bound;
        using (var service = Service(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Check(body.RootElement.GetProperty("requestUser").GetBoolean(), "请求用户信息");
            if (request.RequestUri!.AbsolutePath.EndsWith("authenticate"))
            {
                Check(body.RootElement.GetProperty("username").GetString() == "skin-account", "允许非邮箱账户");
                Check(body.RootElement.GetProperty("agent").GetProperty("name").GetString() == "Minecraft", "Minecraft agent");
                return TokenResponse(null, [Profile, new(OtherId, "Second")]);
            }
            Check(body.RootElement.GetProperty("selectedProfile").GetProperty("id").GetString() == Id, "选角传递真实 UUID");
            return TokenResponse(Profile, []);
        }))
        {
            var pending = await service.LoginAsync(server, "skin-account", "test-password", default);
            Check(pending.AvailableProfiles.Length == 2, "返回多角色");
            await RejectAsync(() => service.SelectAsync(server, pending, new("ffffffffffffffffffffffffffffffff", "Wrong"), default), "拒绝未提供角色");
            bound = await service.SelectAsync(server, pending, Profile, default);
            Check(bound.SelectedProfile == Profile, "绑定选中角色");
            Check(await service.SelectAsync(server, bound, Profile, default) == bound, "已绑定会话无需再次刷新");
        }
        using (var service = Service(_ => TokenResponse(null, [])))
            await RejectAsync(() => service.LoginAsync(server, "skin-account", "test-password", default), "无角色错误");
        using (var service = Service(_ => new(HttpStatusCode.Forbidden) { Content = new StringContent("secret-password") }))
        {
            try { await service.LoginAsync(server, "skin-account", "test-password", default); throw new Exception("应拒绝"); }
            catch (InvalidOperationException ex) { Check(!ex.Message.Contains("secret-password"), "不回显服务器敏感响应"); }
        }
        var account = ExternalLoginService.CreateAccount(server, "skin-account", bound);
        Check(account.IsExternal && !account.IsMicrosoft && account.Uuid == Id, "外置身份独立于微软");
        Check(!account.ToString().Contains("synthetic-token"), "记录诊断不泄露令牌");
        await RejectAsync(() => Task.FromResult(GameLaunchService.CreateSession(account)), "禁止外置降级离线");
        int calls = 0;
        using (var service = Service(request =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("validate")) return new(HttpStatusCode.Forbidden);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Check(!body.RootElement.TryGetProperty("selectedProfile", out _), "刷新已绑定令牌不再绑定角色");
            return TokenResponse(Profile with { Name = "Renamed" }, []);
        }))
        {
            var refreshed = await service.RefreshAsync(account, default);
            Check(calls == 2 && refreshed.Account.Name == "Renamed" && refreshed.Account.Key == account.Key, "失效令牌刷新且允许角色改名");
            Check(refreshed.Session.UserType == "mojang" && refreshed.Session.AccessToken == "synthetic-token", "真实认证会话");
        }
        using (var service = Service(request => request.RequestUri!.AbsolutePath.EndsWith("validate") ? new(HttpStatusCode.Forbidden) : TokenResponse(new(OtherId, "Other"), [])))
            await RejectAsync(() => service.RefreshAsync(account, default), "刷新拒绝更换 UUID");
        using (var service = Service(_ => new(HttpStatusCode.NoContent)))
            Check((await service.RefreshAsync(account, default)).Account.Key == account.Key, "有效令牌无需刷新");
        string folder = Path.Combine(Path.GetTempPath(), "nyx-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var servers = new ExternalServerStore(Path.Combine(folder, "servers.json"));
            Check(servers.Servers.Count == 1, "默认 LittleSkin");
            servers.Add(server); Check(servers.Servers.Count == 1, "默认站点去重");
            servers.Add(new("Other", "https://example.com/api/"));
            Check(new ExternalServerStore(Path.Combine(folder, "servers.json")).Servers.Count == 2, "服务器列表重启恢复");
            var custom = servers.Servers.Single(s => s.Name == "Other");
            servers.Update(custom, custom with { Name = "Renamed Server" });
            Check(new ExternalServerStore(Path.Combine(folder, "servers.json")).Servers.Any(s => s.Name == "Renamed Server"), "修改服务器重启保留");
            await RejectAsync(() => { servers.Update(server, custom); return Task.CompletedTask; }, "修改地址冲突被拒绝");
            servers.Remove(server);
            Check(new ExternalServerStore(Path.Combine(folder, "servers.json")).Servers.All(s => s.Url != server.Url), "删除默认服务器重启不复活");
            var store = new AccountStore(Path.Combine(folder, "accounts.json"));
            store.Add(account.Name); store.SaveExternal(account);
            var other = account with { AuthServer = "https://example.com/api/" };
            store.SaveExternal(other);
            Check(store.Accounts.Count == 3, "不同站点与离线同名身份共存");
            string json = File.ReadAllText(Path.Combine(folder, "accounts.json"));
            Check(!json.Contains("synthetic-token") && !json.Contains("test-password"), "文件不含明文令牌或密码");
            Check(new AccountStore(Path.Combine(folder, "accounts.json")).Selected == other, "加密账户重启恢复");
            store.Remove(account);
            await RejectAsync(() => { store.SaveExternal(account, account); return Task.CompletedTask; }, "迟到刷新不得复活删除账户");
        }
        finally { Directory.Delete(folder, true); }
        using var process = new Process(); process.StartInfo.Arguments = "-cp libraries net.minecraft.Main --username SkinPlayer";
        AuthlibInjector.PrependArguments(process, @"C:\game path\authlib.jar", server.Url, Encoding.UTF8.GetBytes("{}"));
        Check(process.StartInfo.Arguments.StartsWith("\"-javaagent:C:\\game path\\authlib.jar="), "注入参数保护空格路径");
        Check(process.StartInfo.Arguments.Contains("prefetched=e30=") && process.StartInfo.Arguments.EndsWith("--username SkinPlayer"), "元数据预取且保留原参数");
        string texture = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"textures":{"SKIN":{"url":"https://cdn.example.com/textures/skin.png"}}}"""));
        byte[] skinProfile = JsonSerializer.SerializeToUtf8Bytes(new { id = Id, properties = new[] { new { name = "textures", value = texture } } });
        Check(MinecraftSkinService.ParseSkinUrl(skinProfile, Id, [".example.com"])!.Host == "cdn.example.com", "外置头像允许声明的子域");
        try { MinecraftSkinService.ParseSkinUrl(skinProfile, Id, ["other.example.com"]); throw new Exception("不应允许未声明域"); }
        catch (InvalidDataException) { _checks++; }
        try { MinecraftSkinService.ParseSkinUrl(skinProfile, Id); throw new Exception("微软不应允许外置域"); }
        catch (InvalidDataException) { _checks++; }
        Console.WriteLine($"通过 {_checks} 项外置登录检查。");
    }

    /// <summary>只读取已登录外置角色的公开 UUID 和服务器，获取皮肤 PNG，不解密或输出任何认证缓存。</summary>
    public static async Task AvatarLiveAsync()
    {
        var account = new AccountStore().Accounts.First(a => a.IsExternal);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var png = await MinecraftSkinService.FetchAsync(http, account.Uuid, timeout.Token, account.AuthServer);
        Check(png is { Length: > 24 }, "外置角色实网皮肤 PNG");
        Console.WriteLine("通过：已登录外置角色公开档案与 PNG 下载（未读取令牌）。");
    }

    /// <summary>真实只读验证 LittleSkin 地址发现与官方注入器下载，并构建四种现有安装的启动参数；不启动或使用真实账户。</summary>
    public static async Task LiveAsync(string folder)
    {
        using var service = new ExternalLoginService();
        var found = await service.DiscoverAsync("https://littleskin.cn", default);
        Check(found.Server.Url == ExternalServerStore.LittleSkin.Url, "LittleSkin 实网 ALI");
        var account = ExternalLoginService.CreateAccount(found.Server, "synthetic", new("synthetic-token", "client", [], Profile, null));
        foreach (string name in new[] { "qa-vanilla", "qa-Fabric", "qa-Forge", "qa-NeoForge" })
        {
            var version = InstalledVersionService.Scan(folder).Single(v => v.Name == name);
            using var process = await GameLaunchService.BuildAsync(folder, version, IsolationMode.All, account, authenticatedSession:
                ExternalLoginService.CreateSession(account, new("synthetic-token", "client", [], Profile, null)));
            Check(process.StartInfo.Arguments.Contains("-javaagent:") && process.StartInfo.Arguments.Contains("prefetched=") &&
                process.StartInfo.Arguments.Contains("synthetic-token") && process.StartInfo.Arguments.Contains(Id), name + "外置启动参数");
        }
        Console.WriteLine($"通过 {_checks} 项实网发现及启动参数检查（未启动游戏）。");
    }

    /// <summary>提供隔离 HTTP 替身，精确检查发送的协议数据。</summary>
    private static ExternalLoginService Service(Func<HttpRequestMessage, HttpResponseMessage> response) => new(new HttpClient(new Handler(response)));
    /// <summary>生成 JSON 响应。</summary>
    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    /// <summary>生成固定合成令牌，测试不接触真实认证数据。</summary>
    private static HttpResponseMessage TokenResponse(ExternalProfile? profile, ExternalProfile[] profiles) => Response(JsonSerializer.Serialize(
        new ExternalTokens("synthetic-token", "client", profiles, profile, null), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    /// <summary>检查具体业务结果并计数。</summary>
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); _checks++; }
    /// <summary>必须拒绝非法输入或身份变化，未抛异常则失败。</summary>
    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException) { _checks++; return; }
        throw new Exception(message);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        /// <summary>返回合成响应并遵守取消信号。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }
}
