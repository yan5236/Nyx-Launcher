using System.Net;
using NyxLauncher.Services;
using SkiaSharp;

internal static class ResourceIconChecks
{
    private static int _checks;

    /// <summary>验证透明 WebP 解码、尺寸上限、损坏图片、有限重试、缓存及取消，不依赖系统图像扩展。</summary>
    public static async Task RunAsync()
    {
        using var source = new SKBitmap(256, 128, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        source.Erase(new SKColor(20, 80, 160, 128));
        source.SetPixel(0, 0, SKColors.Transparent);
        using var encoded = source.Encode(SKEncodedImageFormat.Webp, 100);
        byte[] webp = encoded.ToArray();
        byte[] png = ResourceIconService.Decode(webp);
        using var decoded = SKBitmap.Decode(png);
        Check(decoded.Width == 128 && decoded.Height == 64 && decoded.GetPixel(64, 32).Alpha is >= 126 and <= 130,
            "WebP 缩小并保留半透明通道");
        using var transparent = new SKBitmap(8, 8); transparent.Erase(SKColors.Transparent);
        using var transparentBytes = transparent.Encode(SKEncodedImageFormat.Webp, 100);
        using var transparentResult = SKBitmap.Decode(ResourceIconService.Decode(transparentBytes.ToArray()));
        Check(transparentResult.GetPixel(4, 4).Alpha == 0, "透明图标不填充背景");
        await Throws<InvalidDataException>(() => Task.FromResult(ResourceIconService.Decode([1, 2, 3])));
        using var wide = new SKBitmap(4097, 1); using var wideBytes = wide.Encode(SKEncodedImageFormat.Png, 100);
        await Throws<InvalidDataException>(() => Task.FromResult(ResourceIconService.Decode(wideBytes.ToArray())));
        int calls = 0;
        using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(webp) }))))
        {
            var service = new ResourceIconService(client);
            string url = "https://cdn.modrinth.com/data/test/" + Guid.NewGuid() + ".webp";
            byte[] first = await service.FetchAsync(url, default);
            byte[] second = await service.FetchAsync(url, default);
            Check(calls == 2 && first.SequenceEqual(second), "暂时失败重试一次，返回已解码缓存");
            await Throws<ArgumentException>(() => service.FetchAsync("http://localhost/private", default));
        }
        using (var client = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(); })))
        {
            using var cancel = new CancellationTokenSource(30);
            await Throws<OperationCanceledException>(() => new ResourceIconService(client).FetchAsync("https://cdn.modrinth.com/data/cancel/icon.webp", cancel.Token));
        }
        Console.WriteLine($"通过 {_checks} 项资源图标检查。");
    }

    /// <summary>断言解码或请求的具体结果，失败立即报告业务名称。</summary>
    private static void Check(bool value, string name) { if (!value) throw new Exception("失败：" + name); _checks++; }

    /// <summary>要求指定失败类型，意外成功不能计为通过。</summary>
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { _checks++; return; } throw new Exception("未抛出异常：" + typeof(T).Name); }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>替换 HTTP 传输，仍运行实际重试、缓存、解码和取消逻辑。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
