using System.Collections.Concurrent;
using System.Net;
using SkiaSharp;

namespace NyxLauncher.Services;

/// <summary>有界下载并把平台 PNG、JPEG、GIF、WebP 图标转成保留透明通道的 PNG，不依赖系统可选解码器。</summary>
public sealed class ResourceIconService
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly SemaphoreSlim Slots = new(4);
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new();
    private static readonly ConcurrentQueue<string> CacheOrder = new();
    private readonly HttpClient _client;

    /// <summary>复用图标连接；测试可以替换 HTTP 传输，缓存只保留成功解码的最近 128 个小图标。</summary>
    public ResourceIconService(HttpClient? client = null) => _client = client ?? SharedClient;

    public Func<SourceStrategy> Source { get; set; } = () => SourceStrategy.OfficialWithFallback;

    /// <summary>验证官方 CDN 后按文件源设置加载与回退；限制四路图标任务，缓存仅保存成功解码结果，取消传播。</summary>
    public async Task<byte[]> FetchAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "cdn.modrinth.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            throw new ArgumentException("图标地址无效。", nameof(url));
        token.ThrowIfCancellationRequested();
        if (Cache.TryGetValue(url, out var cached)) return cached;
        await Slots.WaitAsync(token);
        try
        {
            if (Cache.TryGetValue(url, out cached)) return cached;
            byte[] png = await SourceFallback.FetchAsync(Source(),
                (mirror, ct) => FetchSourceAsync(mirror ? ResourceMirror.Map(uri)! : uri, ct), TimeSpan.FromSeconds(3), token);
            token.ThrowIfCancellationRequested();
            if (Cache.TryAdd(url, png)) CacheOrder.Enqueue(url);
            while (Cache.Count > 128 && CacheOrder.TryDequeue(out string? oldest)) Cache.TryRemove(oldest, out _);
            return png;
        }
        finally { Slots.Release(); }
    }

    /// <summary>单源限时十五秒读取最多 4 MiB 图像；跟随最多五次 HTTPS 重定向，每一跳均发送登记 UA，失败交给双源回退。</summary>
    private async Task<byte[]> FetchSourceAsync(Uri uri, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        for (int redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(ResourceMirror.UserAgent);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                if (redirects >= 5 || location is null) throw new HttpRequestException("图标重定向无效。");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
                    throw new HttpRequestException("图标重定向地址不安全。");
                continue;
            }
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            byte[] buffer = new byte[16384];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("图标超过大小限制。");
                output.Write(buffer, 0, read);
            }
            return await Task.Run(() => Decode(output.ToArray()), timeout.Token);
        }
    }

    /// <summary>先校验解码尺寸，再按最长 128 像素缩小并输出 PNG；保留透明度，拒绝坏数据及超过 4096 像素的图像。</summary>
    internal static byte[] Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("无法识别图标格式。");
        if (codec.Info.Width is <= 0 or > 4096 || codec.Info.Height is <= 0 or > 4096)
            throw new InvalidDataException("图标尺寸无效。");
        using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("无法解码图标。");
        double scale = Math.Min(1, 128d / Math.Max(bitmap.Width, bitmap.Height));
        using var thumbnail = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)),
            Math.Max(1, (int)(bitmap.Height * scale))), new SKSamplingOptions(SKFilterMode.Linear))
            ?? throw new InvalidDataException("无法缩放图标。");
        using var encoded = thumbnail.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
