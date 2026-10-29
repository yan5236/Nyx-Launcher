using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CmlLib.Core.Auth;

namespace NyxLauncher.Services;

/// <summary>将微软令牌交换为 Xbox/XSTS 与 Minecraft 会话；仅在所有权和真实档案均通过时返回身份。</summary>
internal static class MinecraftAuthentication
{
    /// <summary>顺序认证 Xbox、XSTS、Minecraft 并校验档案；全程传递取消，不将任意失败降级为离线登录。</summary>
    internal static async Task<MSession> AuthenticateAsync(HttpClient http, string microsoftToken,
        IProgress<string>? progress, CancellationToken token)
    {
        try
        {
            progress?.Report("正在验证 Xbox 账户…");
            var xbox = await SendAsync(http, "https://user.auth.xboxlive.com/user/authenticate", new
            {
                Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + microsoftToken },
                RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT"
            }, null, token);
            var xsts = await SendAsync(http, "https://xsts.auth.xboxlive.com/xsts/authorize", new
            {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { Required(xbox, "Token") } },
                RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT"
            }, null, token);
            var claims = xsts.GetProperty("DisplayClaims").GetProperty("xui")[0];
            string hash = Required(claims, "uhs");
            if (Required(xbox.GetProperty("DisplayClaims").GetProperty("xui")[0], "uhs") != hash)
                throw new InvalidOperationException("Xbox 账户标识不一致，请重新登录。");
            // Minecraft 的 XSTS 不一定含 xid；按 XboxAuthNet 的 XuiClaims 流程补取 Xbox Live 声明。
            if (!claims.TryGetProperty("xid", out _))
            {
                var live = await SendAsync(http, "https://xsts.auth.xboxlive.com/xsts/authorize", new
                {
                    Properties = new { SandboxId = "RETAIL", UserTokens = new[] { Required(xbox, "Token") } },
                    RelyingParty = "http://xboxlive.com", TokenType = "JWT"
                }, null, token);
                claims = live.GetProperty("DisplayClaims").GetProperty("xui")[0];
                if (Required(claims, "uhs") != hash) throw new InvalidOperationException("Xbox 账户标识不一致。");
            }
            string xuid = Required(claims, "xid");
            if (!ulong.TryParse(xuid, out _)) throw new InvalidOperationException("Xbox 返回了无效的玩家标识。");
            progress?.Report("正在验证 Minecraft 资格与档案…");
            var minecraft = await SendAsync(http, "https://api.minecraftservices.com/authentication/login_with_xbox",
                new { identityToken = "XBL3.0 x=" + hash + ";" + Required(xsts, "Token") }, null, token);
            string accessToken = Required(minecraft, "access_token");
            var ownership = await SendAsync(http, "https://api.minecraftservices.com/entitlements/mcstore", null, accessToken, token);
            if (!ownership.GetProperty("items").EnumerateArray().Any())
                throw new InvalidOperationException("此账户没有可用的 Minecraft Java 版资格，请确认已购买游戏或订阅有效。");
            var profile = await SendAsync(http, "https://api.minecraftservices.com/minecraft/profile", null, accessToken, token);
            string uuid = Required(profile, "id");
            if (!Guid.TryParseExact(uuid, "N", out var parsed) || parsed == Guid.Empty)
                throw new InvalidOperationException("Minecraft 返回了无效的玩家 UUID。");
            string name = Required(profile, "name");
            GameAccount.ValidateName(name);
            return new MSession
            {
                Username = name, UUID = parsed.ToString("N"), AccessToken = accessToken, UserType = "msa",
                Xuid = xuid
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException)
        { throw new InvalidOperationException("认证服务返回了无效数据，请稍后重试。"); }
    }

    /// <summary>读取必要的非空字符串，拒绝缺失字段；异常不包含服务端正文或任何认证凭据。</summary>
    private static string Required(JsonElement json, string name) =>
        json.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw new JsonException("缺少认证字段。");

    /// <summary>向固定官方端点发送认证请求，限制响应大小、超时和取消；将业务错误映射为不含令牌的中文提示。</summary>
    private static async Task<JsonElement> SendAsync(HttpClient http, string url, object? body, string? bearer, CancellationToken token)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: new JsonSerializerOptions());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await response.Content.LoadIntoBufferAsync(1024 * 1024, token);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            long error = 0;
            try
            {
                using var failure = JsonDocument.Parse(bytes);
                if (failure.RootElement.TryGetProperty("XErr", out var code)) code.TryGetInt64(out error);
            }
            catch (JsonException) { }
            string reason = error switch
            {
                2148916233 => "此微软账户尚未建立 Xbox 档案，请先在 xbox.com 完成设置。",
                2148916235 => "此账户所在地区暂不支持 Xbox Live。",
                2148916236 or 2148916237 => "Xbox 要求先完成年龄验证。",
                2148916238 => "此儿童账户需要家庭组织者完成 Xbox 家庭与隐私设置。",
                _ when url.EndsWith("login_with_xbox") && response.StatusCode == HttpStatusCode.Forbidden =>
                    "Minecraft 服务拒绝了此应用（403）。请核实客户端 ID 的 API 白名单资格；测试 ID 不能替代正式申请。",
                _ when url.EndsWith("/profile") && response.StatusCode == HttpStatusCode.NotFound =>
                    "此账户尚无 Minecraft Java 档案，请先在 minecraft.net 创建玩家名称。",
                _ when response.StatusCode == HttpStatusCode.Unauthorized => "认证已失效，请在账户页重新登录。",
                _ => $"认证服务 {request.RequestUri!.Host} 返回 HTTP {(int)response.StatusCode}，请稍后重试。"
            };
            throw new InvalidOperationException(reason);
        }
        using var json = JsonDocument.Parse(bytes);
        return json.RootElement.Clone();
    }
}
