using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CmlLib.Core.Auth;

namespace NyxLauncher.Services;

public sealed record ExternalProfile(string Id, string Name)
{
    /// <summary>角色选择器显示服务端返回的玩家名。</summary>
    public override string ToString() => Name;
}
public sealed record ExternalTokens(string AccessToken, string ClientToken, ExternalProfile[] AvailableProfiles,
    ExternalProfile? SelectedProfile, JsonElement? User)
{
    /// <summary>阻止诊断输出记录中的访问令牌。</summary>
    public override string ToString() => "外置认证会话";
}

/// <summary>实现 Yggdrasil 发现、登录、角色绑定及启动刷新；密码不写入任何存储。</summary>
public sealed class ExternalLoginService : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>认证禁止自动重定向，防止跨站 POST 泄露凭据；可注入客户端用于协议验证。</summary>
    public ExternalLoginService(HttpClient? http = null) => _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1024 * 1024 };

    /// <summary>遵循 HTTPS 重定向与 ALI 相对地址，限制循环次数并验证元数据后返回实际认证根地址。</summary>
    public async Task<(ExternalServer Server, byte[] Metadata)> DiscoverAsync(string input, CancellationToken token)
    {
        string url = ExternalServerStore.Normalize(input);
        for (int i = 0; i < 8; i++)
        {
            using var response = await _http.GetAsync(url, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            { url = ExternalServerStore.Normalize(new Uri(new Uri(url), location).AbsoluteUri); continue; }
            response.EnsureSuccessStatusCode();
            if (response.Headers.TryGetValues("X-Authlib-Injector-API-Location", out var values))
            {
                string next = ExternalServerStore.Normalize(new Uri(new Uri(url), values.First()).AbsoluteUri);
                if (next != url) { url = next; continue; }
            }
            byte[] data = await response.Content.ReadAsByteArrayAsync(token);
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("signaturePublickey", out var key) || string.IsNullOrWhiteSpace(key.GetString()))
                throw new InvalidOperationException("该地址未提供有效的 Yggdrasil 认证元数据。");
            string name = doc.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("serverName", out var n)
                ? n.GetString() ?? new Uri(url).Host : new Uri(url).Host;
            return (new ExternalServer(name, url), data);
        }
        throw new InvalidOperationException("认证服务器地址重定向次数过多。");
    }

    /// <summary>提交一次账户密码认证，返回可选角色及临时令牌；失败只显示固定错误，不回显响应内的敏感内容。</summary>
    public Task<ExternalTokens> LoginAsync(ExternalServer server, string username, string password, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) throw new ArgumentException("请输入账户及密码。");
        return SendAsync(server.Url, "authenticate", new { agent = new { name = "Minecraft", version = 1 }, username, password,
            clientToken = Guid.NewGuid().ToString("N"), requestUser = true }, token);
    }

    /// <summary>绑定用户选择的角色；已绑定令牌不再次提交 selectedProfile，校验响应防止悄悄更换身份。</summary>
    public async Task<ExternalTokens> SelectAsync(ExternalServer server, ExternalTokens tokens, ExternalProfile profile, CancellationToken token)
    {
        if (tokens.SelectedProfile is { } selected)
        {
            if (selected.Id != profile.Id) throw new InvalidOperationException("该会话已绑定其他角色，请重新登录。");
            return tokens;
        }
        if (!tokens.AvailableProfiles.Contains(profile)) throw new ArgumentException("角色不在认证结果中。");
        var result = await SendAsync(server.Url, "refresh", new { accessToken = tokens.AccessToken, clientToken = tokens.ClientToken,
            selectedProfile = profile, requestUser = true }, token);
        EnsureProfile(result, profile.Id);
        return result;
    }

    /// <summary>验证保存的凭证；仅令牌失效时刷新，网络失败不退回离线身份，角色 UUID 必须保持一致。</summary>
    public async Task<(GameAccount Account, MSession Session)> RefreshAsync(GameAccount account, CancellationToken token)
    {
        account.Validate();
        byte[] bytes = MicrosoftLoginService.Unprotect(account.ProtectedTokenCache!);
        ExternalTokens saved;
        try { saved = JsonSerializer.Deserialize<ExternalTokens>(bytes, Json) ?? throw new InvalidOperationException("登录缓存无效，请重新登录。"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        using var response = await _http.PostAsJsonAsync(account.AuthServer + "authserver/validate",
            new { accessToken = saved.AccessToken, clientToken = saved.ClientToken }, token);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            saved = await SendAsync(account.AuthServer!, "refresh", new { accessToken = saved.AccessToken, clientToken = saved.ClientToken, requestUser = true }, token);
        else if (!response.IsSuccessStatusCode) throw new InvalidOperationException("无法验证外置登录会话，请稍后重试或重新登录。");
        EnsureProfile(saved, account.Uuid);
        var updated = CreateAccount(new ExternalServer(account.AuthServerName!, account.AuthServer!), account.ExternalUsername!, saved);
        return (updated, CreateSession(updated, saved));
    }

    /// <summary>校验认证 JSON、令牌及角色；只接受服务端的有效 UUID 和玩家名，拒绝空账户。</summary>
    private async Task<ExternalTokens> SendAsync(string url, string action, object body, CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync(ExternalServerStore.Normalize(url) + "authserver/" + action, body, Json, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(action == "authenticate" ? "外置登录失败，请检查账户、密码或站点登录限制。" : "外置会话已失效，请在账户页重新登录。");
        var result = await response.Content.ReadFromJsonAsync<ExternalTokens>(Json, token) ?? throw new InvalidOperationException("认证返回为空。");
        if (string.IsNullOrWhiteSpace(result.AccessToken) || string.IsNullOrWhiteSpace(result.ClientToken)) throw new InvalidOperationException("认证未返回有效令牌。");
        result = result with { AvailableProfiles = result.AvailableProfiles ?? [] };
        foreach (var p in result.AvailableProfiles.Concat(result.SelectedProfile is { } s ? [s] : Array.Empty<ExternalProfile>()))
        {
            if (p is null || !Guid.TryParseExact(p.Id, "N", out _)) throw new InvalidOperationException("认证返回无效角色 UUID。");
            GameAccount.ValidateName(p.Name);
        }
        if (action == "authenticate" && result.SelectedProfile is null && result.AvailableProfiles.Length == 0)
            throw new InvalidOperationException("此账户还没有角色，请先在皮肤站创建角色。");
        return result;
    }

    /// <summary>拒绝缺失或被替换的角色，保证重登及刷新不会更改存档身份。</summary>
    private static void EnsureProfile(ExternalTokens tokens, string id)
    {
        if (!string.Equals(tokens.SelectedProfile?.Id, id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原角色已不可用，请重新登录并选择角色。");
    }

    /// <summary>只将令牌加密写入账户模型，服务器和登录标识参与账户唯一键，密码不参与持久化。</summary>
    public static GameAccount CreateAccount(ExternalServer server, string username, ExternalTokens tokens)
    {
        var p = tokens.SelectedProfile ?? throw new InvalidOperationException("请先选择角色。");
        var account = new GameAccount(p.Name, p.Id.ToLowerInvariant(), ProtectedTokenCache: MicrosoftLoginService.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens, Json)),
            AuthServer: server.Url, AuthServerName: server.Name, ExternalUsername: username);
        account.Validate();
        return account;
    }

    /// <summary>生成带真实角色 UUID 和访问令牌的 Yggdrasil 会话供 Java 启动使用。</summary>
    public static MSession CreateSession(GameAccount account, ExternalTokens tokens) => new()
    { Username = account.Name, UUID = account.Uuid, AccessToken = tokens.AccessToken, UserType = "mojang" };

    /// <summary>释放认证 HTTP 客户端和连接。</summary>
    public void Dispose() => _http.Dispose();
}
