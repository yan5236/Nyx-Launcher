using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NyxLauncher.Services;

/// <summary>按真实 UUID 查询 Mojang 公开皮肤档案，只从官方纹理域下载，不读取或发送登录令牌。</summary>
internal static class MinecraftSkinService
{
    /// <summary>获取完整 PNG 皮肤；无皮肤返回 null，网络或格式错误向调用方传播，由显示层保留缓存。</summary>
    internal static async Task<byte[]?> FetchAsync(HttpClient http, string uuid, CancellationToken token)
    {
        ValidateUuid(uuid);
        using var profileResponse = await http.GetAsync("https://sessionserver.mojang.com/session/minecraft/profile/" + uuid,
            HttpCompletionOption.ResponseHeadersRead, token);
        if (profileResponse.StatusCode == HttpStatusCode.NoContent) return null;
        profileResponse.EnsureSuccessStatusCode();
        await profileResponse.Content.LoadIntoBufferAsync(128 * 1024, token);
        var url = ParseSkinUrl(await profileResponse.Content.ReadAsByteArrayAsync(token), uuid);
        if (url is null) return null;
        using var skinResponse = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        skinResponse.EnsureSuccessStatusCode();
        await skinResponse.Content.LoadIntoBufferAsync(1024 * 1024, token);
        byte[] skin = await skinResponse.Content.ReadAsByteArrayAsync(token);
        if (skin.Length < 24 || !skin.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("皮肤不是 PNG 图片。");
        return skin;
    }

    /// <summary>校验档案属于请求的 UUID，解码 textures 属性并限制官方纹理 URL；历史 HTTP 地址升级为 HTTPS。</summary>
    internal static Uri? ParseSkinUrl(byte[] data, string uuid)
    {
        ValidateUuid(uuid);
        using var profile = JsonDocument.Parse(data);
        if (!string.Equals(profile.RootElement.GetProperty("id").GetString(), uuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("皮肤档案与账户不一致。");
        if (!profile.RootElement.TryGetProperty("properties", out var properties)) return null;
        foreach (var property in properties.EnumerateArray())
        {
            if (property.GetProperty("name").GetString() != "textures") continue;
            using var textures = JsonDocument.Parse(Convert.FromBase64String(property.GetProperty("value").GetString()!));
            if (!textures.RootElement.GetProperty("textures").TryGetProperty("SKIN", out var skin)) return null;
            if (!Uri.TryCreate(skin.GetProperty("url").GetString(), UriKind.Absolute, out var url) ||
                url.Scheme is not ("http" or "https") || url.Host != "textures.minecraft.net" || !url.IsDefaultPort ||
                url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
                !Regex.IsMatch(url.AbsolutePath, @"\A/texture/[0-9a-fA-F]{32,64}\z"))
                throw new InvalidDataException("皮肤地址不是受支持的官方纹理地址。");
            return new UriBuilder(url) { Scheme = "https", Port = -1 }.Uri;
        }
        return null;
    }

    /// <summary>只接受无分隔符的游戏 UUID，确保远端请求路径和本地缓存名称不能越界。</summary>
    internal static void ValidateUuid(string uuid)
    {
        if (!Regex.IsMatch(uuid, @"\A[0-9a-f]{32}\z")) throw new ArgumentException("皮肤账户 UUID 无效。");
    }
}
