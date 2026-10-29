using System.Net;
using System.Text;
using System.Text.Json;
using NyxLauncher.Services;

internal static class SkinAvatarChecks
{
    private const string Uuid = "1234567890abcdef1234567890abcdef";
    private static int _checks;

    /// <summary>验证官方皮肤请求、正脸内缩、透明外角、帽子预乘合成、旧版皮肤和最近邻像素边界。</summary>
    public static async Task RunAsync()
    {
        string url = "http://textures.minecraft.net/texture/" + new string('a', 64);
        Check(MinecraftSkinService.ParseSkinUrl(Profile(url), Uuid)?.Scheme == "https", "历史纹理地址升级 HTTPS");
        Check(MinecraftSkinService.ParseSkinUrl(Encoding.UTF8.GetBytes("{\"id\":\"" + Uuid + "\",\"properties\":[]}"), Uuid) is null, "没有皮肤时允许默认头像");
        foreach (string invalid in new[] { "http://localhost/texture/" + new string('a', 64), url.Replace("minecraft.net", "minecraft.net.evil"),
            url + "?token=x", url.Replace("http://", "http://user@"), url.Replace(".net/", ".net:9999/"), url + "/../x" })
            Throws<InvalidDataException>(() => MinecraftSkinService.ParseSkinUrl(Profile(invalid), Uuid), "拒绝非官方或带额外参数的纹理地址");
        Throws<InvalidDataException>(() => MinecraftSkinService.ParseSkinUrl(Profile(url), new string('b', 32)), "拒绝其他账户档案");
        Throws<ArgumentException>(() => MinecraftSkinService.ValidateUuid("../x"), "UUID 不允许路径越界");
        int requests = 0;
        using (var http = new HttpClient(new Handler(request =>
        {
            Check(request.Headers.Authorization is null, "公开皮肤请求不传认证令牌");
            requests++;
            if (requests == 1) return Response(Profile(url));
            Check(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "textures.minecraft.net", "下载仅访问官方 HTTPS 纹理");
            byte[] png = new byte[24]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
            return Response(png);
        })))
            Check((await MinecraftSkinService.FetchAsync(http, Uuid, default))?.Length == 24 && requests == 2, "档案与 PNG 下载顺序正确");
        using (var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent))))
            Check(await MinecraftSkinService.FetchAsync(http, Uuid, default) is null, "204 不按 JSON 解析");
        using (var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests))))
        {
            try { await MinecraftSkinService.FetchAsync(http, Uuid, default); throw new Exception("限流必须报告失败以保留旧缓存。"); }
            catch (HttpRequestException) { _checks++; }
        }
        foreach (int scale in new[] { 1, 2 })
        {
            int width = 64 * scale;
            byte[] skin = new byte[width * width * 4];
            for (int y = 8 * scale; y < 16 * scale; y++)
                for (int x = 8 * scale; x < 16 * scale; x++)
                {
                    int index = (y * width + x) * 4;
                    skin[index] = 20; skin[index + 1] = 40; skin[index + 2] = 60; skin[index + 3] = 255;
                }
            byte[] avatar = SkinAvatarPixels.Create(skin, width, width);
            Check(avatar.AsSpan((4 * 64 + 4) * 4, 4).SequenceEqual(new byte[] { 20, 40, 60, 255 }) &&
                avatar.AsSpan((59 * 64 + 59) * 4, 4).SequenceEqual(new byte[] { 20, 40, 60, 255 }), "正脸按独立比例内缩，比例 " + scale);
            Check(new[] { 0, 63, 63 * 64, 64 * 64 - 1 }.All(index => avatar.AsSpan(index * 4, 4).SequenceEqual(new byte[4])), "四角保持全透明，比例 " + scale);
            Check(avatar[(3 * 64 + 4) * 4 + 3] == 0 && avatar[(60 * 64 + 4) * 4 + 3] == 0, "脸部上下边界不外溢，比例 " + scale);
            for (int y = 8 * scale; y < 9 * scale; y++)
                for (int x = 40 * scale; x < 41 * scale; x++)
                {
                    int index = (y * width + x) * 4;
                    skin[index] = 220; skin[index + 1] = 140; skin[index + 2] = 60; skin[index + 3] = 128;
                }
            avatar = SkinAvatarPixels.Create(skin, width, width);
            Check(avatar.AsSpan((4 * 64 + 4) * 4, 4).SequenceEqual(new byte[] { 120, 90, 60, 255 }), "帽子在脸部之上按透明度合成，比例 " + scale);
            Check(avatar.AsSpan(0, 4).SequenceEqual(new byte[] { 110, 70, 30, 128 }), "超出脸部的帽子使用预乘颜色，比例 " + scale);
            Check(avatar.AsSpan((4 * 64 + 7) * 4, 4).SequenceEqual(avatar.AsSpan((4 * 64 + 4) * 4, 4)) && avatar[(4 * 64 + 8) * 4] == 20,
                "帽子与脸部分别最近邻采样，比例 " + scale);
        }
        byte[] legacy = Enumerable.Repeat((byte)255, 64 * 32 * 4).ToArray();
        legacy[(8 * 64 + 8) * 4] = 12;
        Check(SkinAvatarPixels.Create(legacy, 64, 32)[(4 * 64 + 4) * 4] == 12, "旧版全不透明帽子区域不遮住脸");
        foreach (var size in new[] { (32, 32), (64, 48), (2048, 2048), (65, 65) })
            Throws<InvalidDataException>(() => SkinAvatarPixels.ValidateSize(size.Item1, size.Item2), "拒绝异常和超大图片尺寸");
        Throws<InvalidDataException>(() => SkinAvatarPixels.Create([], 64, 64), "拒绝不完整像素数组");
        Console.WriteLine($"通过 {_checks} 项皮肤头像检查。");
    }

    /// <summary>生成只包含合成 UUID 与公开纹理地址的档案，用于验证 Base64 textures 属性解析。</summary>
    private static byte[] Profile(string url) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        id = Uuid,
        properties = new[] { new { name = "textures", value = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { textures = new { SKIN = new { url } } })) } }
    });

    /// <summary>创建合成响应，不访问真实账户或凭据。</summary>
    private static HttpResponseMessage Response(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    /// <summary>要求非法输入抛出预期异常，防止下载越界或错误像素被接受。</summary>
    private static void Throws<T>(Action action, string name) where T : Exception
    { try { action(); } catch (T) { _checks++; return; } throw new Exception(name); }

    /// <summary>断言具体业务结果并计数，失败立即标明行为。</summary>
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        /// <summary>提供可检查 URL 和请求头的响应，取消时不执行场景回调。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(send(request)); }
    }
}
