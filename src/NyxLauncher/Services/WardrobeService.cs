using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NyxLauncher.Services;

public sealed record WardrobeTexture(string Id, string Name, Uri Url, bool Active, bool Slim = false);
public sealed record WardrobeProfile(WardrobeTexture? Skin, IReadOnlyList<WardrobeTexture> Capes);

/// <summary>通过官方 Minecraft API 读取正版档案和应用纹理；认证请求与无凭据纹理下载严格分离。</summary>
public sealed class WardrobeService : IDisposable
{
    private readonly HttpClient _http;

    /// <summary>创建禁止重定向的独立客户端，避免 Bearer 被带到其他主机；测试可注入协议处理器。</summary>
    public WardrobeService(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new NetworkDiagnosticHandler(new HttpClientHandler { AllowAutoRedirect = false }))
        { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>读取认证账户完整档案并核对 UUID；离线及外置账户在发送请求前拒绝。</summary>
    public async Task<WardrobeProfile> GetProfileAsync(GameAccount account, string accessToken, CancellationToken token)
    {
        RequireMicrosoft(account);
        using var response = await SendAsync(HttpMethod.Get, "", accessToken, null, token);
        await response.Content.LoadIntoBufferAsync(256 * 1024, token);
        return ParseProfile(await response.Content.ReadAsByteArrayAsync(token), account.Uuid);
    }

    /// <summary>解析账户皮肤与拥有的披风，只接受与请求身份匹配的档案及官方纹理 URL。</summary>
    internal static WardrobeProfile ParseProfile(byte[] json, string uuid)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("id").GetString(), uuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("衣柜档案与所选账户不一致。");
        var skins = root.GetProperty("skins").EnumerateArray().Select(s => ParseTexture(s, false)).ToArray();
        var capes = root.GetProperty("capes").EnumerateArray().Select(s => ParseTexture(s, true)).ToArray();
        return new(skins.FirstOrDefault(s => s.Active), capes);
    }

    /// <summary>读取单个纹理的名称、模型和激活状态；服务端原始数据不直接参与路径或请求头。</summary>
    private static WardrobeTexture ParseTexture(JsonElement element, bool cape) => new(
        element.GetProperty("id").GetString() ?? throw new InvalidDataException("缺少纹理标识。"),
        cape && element.TryGetProperty("alias", out var alias) ? alias.GetString() ?? "披风" : "当前皮肤",
        ValidateTextureUrl(element.GetProperty("url").GetString()!),
        element.GetProperty("state").GetString() == "ACTIVE",
        !cape && element.TryGetProperty("variant", out var variant) && variant.GetString() == "SLIM");

    /// <summary>仅允许官方纹理路径并升级 HTTPS，拒绝端口、凭据、查询及重定向入口。</summary>
    internal static Uri ValidateTextureUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Host != "textures.minecraft.net" ||
            url.Scheme is not ("http" or "https") || !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Query.Length != 0 ||
            url.Fragment.Length != 0 || !Regex.IsMatch(url.AbsolutePath, @"\A/texture/[0-9a-fA-F]{32,64}\z"))
            throw new InvalidDataException("不是受支持的官方纹理地址。");
        return new UriBuilder(url) { Scheme = "https", Port = -1 }.Uri;
    }

    /// <summary>下载公开纹理时不携带访问令牌，限制响应大小并验证标准 PNG；失败由界面呈现。</summary>
    public async Task<byte[]> DownloadAsync(WardrobeTexture texture, bool cape, CancellationToken token)
    {
        using var response = await _http.GetAsync(ValidateTextureUrl(texture.Url.AbsoluteUri), HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1024 * 1024, token);
        byte[] png = await response.Content.ReadAsByteArrayAsync(token);
        WardrobeStore.ValidatePng(png, texture.Slim, cape);
        return png;
    }

    /// <summary>把验证后的皮肤以 multipart 上传到官方接口；不自动重试写请求，避免重复修改账户。</summary>
    public async Task ApplySkinAsync(GameAccount account, string accessToken, byte[] png, bool slim, CancellationToken token)
    {
        RequireMicrosoft(account); WardrobeStore.ValidatePng(png, slim);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(slim ? "slim" : "classic"), "variant");
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "skin.png");
        using var response = await SendAsync(HttpMethod.Post, "/skins", accessToken, form, token);
    }

    /// <summary>仅允许应用最新档案中拥有的披风；空标识表示隐藏披风，服务器仍负责最终权限验证。</summary>
    public async Task ApplyCapeAsync(GameAccount account, string accessToken, WardrobeProfile profile, string? capeId, CancellationToken token)
    {
        RequireMicrosoft(account);
        if (capeId is not null && !profile.Capes.Any(c => c.Id == capeId)) throw new InvalidOperationException("该账户没有这件披风。");
        using var content = capeId is null ? null : JsonContent.Create(new { capeId });
        using var response = await SendAsync(capeId is null ? HttpMethod.Delete : HttpMethod.Put, "/capes/active", accessToken, content, token);
    }

    /// <summary>在服务入口强制正版身份约束，防止界面状态绕过账户类型检查。</summary>
    private static void RequireMicrosoft(GameAccount account)
    {
        if (!account.IsMicrosoft) throw new InvalidOperationException("衣柜仅支持微软账户。");
        account.Validate();
    }

    /// <summary>只向固定官方路径附加 Bearer，失败显示状态码而非可能包含敏感信息的服务器正文。</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string suffix, string accessToken, HttpContent? content, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, "https://api.minecraftservices.com/minecraft/profile" + suffix) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.IsSuccessStatusCode) return response;
        int code = (int)response.StatusCode;
        response.Dispose();
        throw new InvalidOperationException(code is 401 or 403 ? "微软授权已过期或无权修改皮肤，请重新登录后重试。" : $"衣柜请求失败（HTTP {code}），请稍后重试。");
    }

    /// <summary>释放连接池；调用方须先取消正在进行的操作。</summary>
    public void Dispose() => _http.Dispose();
}
