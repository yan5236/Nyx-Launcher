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

    /// <summary>验证官方 CDN 后最多尝试两次，限制 4 路请求、4 MiB 输入和 15 秒单次超时；取消传播，失败不缓存。</summary>
    public async Task<byte[]> FetchAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "cdn.modrinth.com" || uri.UserInfo.Length > 0) throw new ArgumentException("图标地址无效。", nameof(url));
        token.ThrowIfCancellationRequested();
        if (Cache.TryGetValue(url, out var cached)) return cached;
        await Slots.WaitAsync(token);
        try
        {
            if (Cache.TryGetValue(url, out cached)) return cached;
            for (int attempt = 0; ; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.UserAgent.ParseAdd("NyxLauncher/0.1");
                    using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
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
                    byte[] png = await Task.Run(() => Decode(output.ToArray()), token);
                    token.ThrowIfCancellationRequested();
                    if (Cache.TryAdd(url, png)) CacheOrder.Enqueue(url);
                    while (Cache.Count > 128 && CacheOrder.TryDequeue(out string? oldest)) Cache.TryRemove(oldest, out _);
                    return png;
                }
                catch (Exception ex) when (attempt == 0 && !token.IsCancellationRequested &&
                    ex is HttpRequestException or IOException or OperationCanceledException)
                { await Task.Delay(250, token); }
            }
        }
        finally { Slots.Release(); }
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
