using System.Net;
using System.Text;
using NyxLauncher.Services;
using SkiaSharp;

internal static class WardrobeChecks
{
    /// <summary>用合成纹理和协议响应检查衣柜存储、账户限制、请求格式及模型投影，不修改真实账户。</summary>
    public static async Task RunAsync()
    {
        string folder = Path.Combine(Path.GetTempPath(), "nyx-wardrobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] png = Png(64), legacy = Png(32);
            var store = new WardrobeStore(folder);
            var skin = store.Add("测试皮肤", false, png);
            Check(new WardrobeStore(folder).Read(skin).SequenceEqual(png), "皮肤副本与索引往返");
            store.Rename(skin, "新名称"); skin = store.Skins.Single();
            Check(skin.Name == "新名称", "中文名称持久化");
            Check(store.Add("重复导入", false, png) == skin && store.Skins.Count == 1, "重复导入复用原皮肤并保留重命名");
            using (var decoded = SKBitmap.Decode(png))
            using (var encoded = decoded.Encode(SKEncodedImageFormat.Png, 50))
                Check(store.Add("服务器再次同步", false, encoded.ToArray()) == skin, "重新编码的相同像素不重复收藏");
            var slimSkin = store.Add("纤细", true, png);
            Check(slimSkin.Id != skin.Id, "相同纹理不同模型分别保留");
            var other = store.Add("换肤后", false, legacy);
            var reloaded = new WardrobeStore(folder);
            Check(reloaded.Skins.Count == 3 && reloaded.Read(skin).SequenceEqual(png) && reloaded.Read(other).SequenceEqual(legacy), "换肤并重启后旧皮肤和新皮肤都保留且可导出");
            store.Remove(slimSkin); store.Remove(other);
            Reject(() => store.Read(skin with { Id = "../outside" }), "拒绝外部路径标识");
            Reject(() => WardrobeStore.ValidatePng(legacy, true), "纤细模型拒绝旧式皮肤");
            Reject(() => WardrobeStore.ValidatePng(png[..30]), "拒绝截断图片");
            Reject(() => WardrobeStore.ValidatePng(new byte[1024 * 1024 + 1]), "拒绝超大图片");
            store.Remove(skin);
            Check(!store.Skins.Any() && !File.Exists(Path.Combine(folder, skin.Id + ".png")), "删除副本和索引");
            File.WriteAllText(Path.Combine(folder, "skins.json"), "broken");
            var broken = new WardrobeStore(folder);
            Reject(() => broken.Add("新皮肤", false, png), "损坏索引禁止覆盖");
            Check(File.ReadAllText(Path.Combine(folder, "skins.json")) == "broken", "原始损坏文件保留");

            string uuid = new('a', 32), hash = new('b', 64);
            string json = $$"""{"id":"{{uuid}}","skins":[{"id":"skin","state":"ACTIVE","variant":"SLIM","url":"http://textures.minecraft.net/texture/{{hash}}"}],"capes":[{"id":"cape-one","state":"ACTIVE","alias":"测试披风","url":"https://textures.minecraft.net/texture/{{hash}}"}]}""";
            var profile = WardrobeService.ParseProfile(Encoding.UTF8.GetBytes(json), uuid);
            Check(profile.Skin is { Slim: true } && profile.Skin.Url.Scheme == "https" && profile.Capes.Single().Active, "解析模型、激活披风及 HTTPS 升级");
            Reject(() => WardrobeService.ParseProfile(Encoding.UTF8.GetBytes(json), new string('c', 32)), "档案身份隔离");
            foreach (string url in new[] { "https://evil.test/texture/" + hash, "https://textures.minecraft.net:444/texture/" + hash, "https://textures.minecraft.net/texture/" + hash + "?token=x" })
                Reject(() => WardrobeService.ValidateTextureUrl(url), "拒绝非官方纹理入口");
            var handler = new Handler(json, png);
            using var service = new WardrobeService(handler);
            var account = new GameAccount("Player", uuid, "test-home", Guid.NewGuid().ToString(), "AA==");
            await service.GetProfileAsync(account, "fixture-token", default);
            await service.DownloadAsync(profile.Skin!, false, default);
            await service.ApplySkinAsync(account, "fixture-token", png, true, default);
            await service.ApplyCapeAsync(account, "fixture-token", profile, "cape-one", default);
            await service.ApplyCapeAsync(account, "fixture-token", profile, null, default);
            Check(handler.Upload && handler.Cape && handler.Hide, "皮肤 multipart、披风 PUT 与隐藏 DELETE");
            int count = handler.Count;
            await RejectAsync(() => service.GetProfileAsync(new GameAccount("Offline"), "fixture-token", default), "离线账户禁止请求");
            await RejectAsync(() => service.ApplyCapeAsync(account, "fixture-token", profile, "unowned", default), "拒绝未拥有披风");
            Check(handler.Count == count, "非法身份与披风均未发送网络请求");
            handler.Fail = true;
            await RejectAsync(() => service.ApplySkinAsync(account, "fixture-token", png, false, default), "服务器失败不得当作成功");

            using var renderer = new SkinModelRenderer();
            renderer.SetTextures(png, false, null);
            using var frame = new SKBitmap(360, 440);
            renderer.Render(frame, 0, 0, 0);
            Check(frame.GetPixel(180, 90).Red > 200, "正面头部使用正确 UV");
            Check(frame.GetPixel(0, 0).Alpha == 0 && frame.Pixels.Count(p => p.Alpha > 0) > 10000, "模型可见且背景透明");
            var still = frame.Bytes;
            renderer.Render(frame, 1, .55f, -.12f);
            Check(!still.SequenceEqual(frame.Bytes), "步态与相机改变投影");
            renderer.SetTextures(png, true, legacy);
            renderer.Render(frame, 1, 3.4f);
            Check(frame.Pixels.Any(p => p.Alpha > 0), "纤细模型与披风背面可绘制");
            using var cape = new SKBitmap(64, 32);
            cape.Erase(SKColors.Transparent);
            for (int y = 1; y < 17; y++)
                for (int x = 1; x < 22; x++) cape.SetPixel(x, y, x < 6 ? SKColors.Lime : x < 11 ? SKColors.Yellow : SKColors.Magenta);
            using var capePng = cape.Encode(SKEncodedImageFormat.Png, 100);
            renderer.SetTextures(png, false, capePng.ToArray());
            renderer.Render(frame, 0, MathF.PI, 0);
            Check(frame.GetPixel(150, 220).Green > 200 && frame.GetPixel(150, 220).Red < 20, "披风外侧左半保留绿色，不误用内衬或镜像");
            Check(frame.GetPixel(210, 220).Green > 200 && frame.GetPixel(210, 220).Red > 200, "披风外侧右半保留黄色，不左右反转");
            renderer.Render(frame, 0, 0, 0);
            Check(frame.GetPixel(180, 220).Blue > 200 && frame.GetPixel(180, 220).Red < 20, "披风位于身体背后，不遮住前胸");
            Console.WriteLine("Wardrobe checks passed.");
        }
        finally { Directory.Delete(folder, true); }
    }

    /// <summary>生成标准大小合成 PNG，正脸涂红，其余底层为蓝色以验证 UV 与模型尺寸。</summary>
    private static byte[] Png(int height)
    {
        using var bitmap = new SKBitmap(64, height);
        using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent);
        using var blue = new SKPaint { Color = SKColors.Blue }; canvas.DrawRect(0, 16, 64, 16, blue);
        using var red = new SKPaint { Color = SKColors.Red }; canvas.DrawRect(0, 0, 32, 16, red);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100); return encoded.ToArray();
    }

    /// <summary>断言明确业务结果，失败使命令行检查返回非零。</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    /// <summary>验证同步非法输入被拒绝，不吞掉断言自身失败。</summary>
    private static void Reject(Action action, string message)
    { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException) { return; } throw new Exception(message); }

    /// <summary>验证异步权限及协议失败被明确传播。</summary>
    private static async Task RejectAsync(Func<Task> action, string message)
    { try { await action(); } catch (InvalidOperationException) { return; } throw new Exception(message); }

    private sealed class Handler(string profile, byte[] png) : HttpMessageHandler
    {
        public bool Upload, Cape, Hide, Fail;
        public int Count;
        /// <summary>记录并校验官方请求的认证与正文，纹理请求必须无凭据；全部响应来自内存夹具。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++;
            if (request.RequestUri!.Host == "textures.minecraft.net")
            {
                Check(request.Headers.Authorization is null, "公开纹理不能携带 Bearer");
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
            }
            Check(request.Headers.Authorization?.Parameter == "fixture-token", "官方 API 携带对应令牌");
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            if (request.Method == HttpMethod.Post) Upload = body.Contains("slim") && body.Contains("image/png") && body.Contains("skin.png");
            if (request.Method == HttpMethod.Put) Cape = body.Contains("cape-one");
            if (request.Method == HttpMethod.Delete) Hide = request.RequestUri.AbsolutePath.EndsWith("/capes/active");
            return new(Fail ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = new StringContent(profile) };
        }
    }
}
