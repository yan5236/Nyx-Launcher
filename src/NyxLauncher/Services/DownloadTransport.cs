using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace NyxLauncher.Services;

/// <summary>文件任务和游戏依赖共用的连接预算、限速、分段下载与完整性校验。</summary>
public sealed class DownloadTransport : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _connections;
    private readonly DownloadSettings _settings;
    private readonly object _rateLock = new();
    private long _nextByteTime;

    /// <summary>固定本批次设置；可注入 HTTP 客户端进行真实协议测试，默认保留系统代理和证书校验。</summary>
    public DownloadTransport(DownloadSettings settings, HttpClient? http = null)
    {
        _settings = settings;
        _connections = new(settings.MaxConnections);
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>只接受 HTTP(S) 地址，拒绝凭据、文件协议和空主机；自定义下载不改写用户指定的地址。</summary>
    public static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 || uri.UserInfo.Length > 0)
            throw new ArgumentException("请输入不包含用户名或密码的 HTTP/HTTPS 下载链接。");
        return uri;
    }

    /// <summary>仅为已知官方分发域名生成 BMCLAPI 地址；任意用户链接保持原样。</summary>
    public static Uri? Mirror(Uri uri) => uri.Host switch
    {
        "piston-meta.mojang.com" or "launchermeta.mojang.com" or "piston-data.mojang.com" or "launcher.mojang.com"
            => new("https://bmclapi2.bangbang93.com" + uri.PathAndQuery),
        "resources.download.minecraft.net" => new("https://bmclapi2.bangbang93.com/assets" + uri.PathAndQuery),
        "libraries.minecraft.net" => new("https://bmclapi2.bangbang93.com/maven" + uri.PathAndQuery),
        "maven.minecraftforge.net" or "maven.fabricmc.net" => new("https://bmclapi2.bangbang93.com/maven" + uri.PathAndQuery),
        "maven.neoforged.net" => new("https://bmclapi2.bangbang93.com/maven" + uri.PathAndQuery.Replace("/releases/", "/")),
        "meta.fabricmc.net" => new("https://bmclapi2.bangbang93.com/fabric-meta" + uri.PathAndQuery),
        _ => null
    };

    /// <summary>下载至独立临时文件并校验后原子发布；失败、暂停或取消删除本次临时文件，不破坏已有目标。游戏文件可在校验通过后替换损坏副本。</summary>
    public async Task DownloadAsync(Uri url, string destination, string? sha1, long size,
        bool gameFile, bool segmented, Action<long, long>? progress, CancellationToken token)
    {
        ValidateUrl(url.AbsoluteUri);
        if (gameFile && await ValidAsync(destination, sha1, size, token)) return;
        if (!gameFile && File.Exists(destination)) throw new IOException("目标文件已存在，请选择其他目录或重命名原文件。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Uri? mirror = gameFile ? Mirror(url) : null;
        var strategy = _settings.FileSource;
        var first = strategy == SourceStrategy.MirrorFirst && mirror is not null ? mirror : url;
        var second = first == url ? mirror : url;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Uri source = attempt == 1 && second is not null ? second : first;
                await TransferAsync(source, destination, sha1, size, gameFile, segmented, progress, token,
                    attempt == 0 && strategy == SourceStrategy.OfficialWithFallback && second is not null ? TimeSpan.FromSeconds(3) : null);
                return;
            }
            catch (Exception ex) when (attempt < 2 && !token.IsCancellationRequested &&
                ex is HttpRequestException or IOException or OperationCanceledException)
            { await Task.Delay(300 * (attempt + 1), token); }
        }
    }

    /// <summary>验证存在文件的实际长度与 SHA-1；允许哈希明确匹配的合法空文件，缺少哈希的缓存仍要求非空。</summary>
    public static async Task<bool> ValidAsync(string path, string? hash, long size, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        if ((size > 0 && stream.Length != size) || (stream.Length == 0 && string.IsNullOrEmpty(hash))) return false;
        return string.IsNullOrEmpty(hash) || Convert.ToHexString(await SHA1.HashDataAsync(stream, token))
            .Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>探测可验证的 Range 支持后并行写入互不重叠的片段；服务器忽略 Range 时丢弃片段并回到单连接。</summary>
    private async Task TransferAsync(Uri url, string destination, string? hash, long expected, bool overwrite,
        bool segmented, Action<long, long>? progress, CancellationToken token, TimeSpan? firstByteTimeout)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        long total = expected;
        string? etag = null;
        try
        {
            if (segmented && _settings.MaxConnections > 1)
            {
                await _connections.WaitAsync(token);
                try
                {
                    using var head = new HttpRequestMessage(HttpMethod.Head, url);
                    using var response = await SendAsync(head, token, firstByteTimeout);
                    if (response.IsSuccessStatusCode && response.Headers.AcceptRanges.Contains("bytes") &&
                        response.Headers.ETag is { IsWeak: false } tag)
                    { total = response.Content.Headers.ContentLength ?? expected; etag = tag.ToString(); }
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException) { /* HEAD 不可用仍尝试 GET。 */ }
                finally { _connections.Release(); }
            }
            int count = etag is not null && total >= 4 * 1024 * 1024 ? Math.Min(4, _settings.MaxConnections) : 1;
            long received = 0;
            using (var output = File.OpenHandle(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous))
            {
                if (count > 1) RandomAccess.SetLength(output, total);
                using var peers = CancellationTokenSource.CreateLinkedTokenSource(token);
                Exception? failure = null;
                var parts = Enumerable.Range(0, count).Select(async i =>
                {
                    long start = count == 1 ? 0 : total * i / count;
                    long end = count == 1 ? -1 : total * (i + 1) / count - 1;
                    await _connections.WaitAsync(peers.Token);
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.AcceptEncoding.ParseAdd("identity");
                        if (count > 1)
                        {
                            request.Headers.Range = new RangeHeaderValue(start, end);
                            request.Headers.IfMatch.ParseAdd(etag!);
                        }
                        using var response = await SendAsync(request, peers.Token, firstByteTimeout);
                        response.EnsureSuccessStatusCode();
                        var range = response.Content.Headers.ContentRange;
                        if (count > 1 && (response.StatusCode != HttpStatusCode.PartialContent ||
                            range?.From != start || range.To != end || range.Length != total))
                            throw new InvalidDataException("服务器未正确返回所请求的分段。");
                        if (count == 1 && response.StatusCode == HttpStatusCode.PartialContent)
                            throw new InvalidDataException("服务器意外返回不完整文件。");
                        long length = count > 1 ? end - start + 1 : response.Content.Headers.ContentLength ?? expected;
                        using var input = await response.Content.ReadAsStreamAsync(peers.Token);
                        byte[] buffer = new byte[64 * 1024];
                        long offset = start;
                        while (true)
                        {
                            using var idle = CancellationTokenSource.CreateLinkedTokenSource(peers.Token);
                            idle.CancelAfter(TimeSpan.FromSeconds(30));
                            int read = await input.ReadAsync(buffer, idle.Token);
                            if (read == 0) break;
                            if (length > 0 && offset - start + read > length) throw new IOException("响应长度超出声明范围。");
                            await ThrottleAsync(read, peers.Token);
                            await RandomAccess.WriteAsync(output, buffer.AsMemory(0, read), offset, peers.Token);
                            offset += read;
                            progress?.Invoke(Interlocked.Add(ref received, read), count > 1 ? total : length);
                        }
                        if (length > 0 && offset - start != length) throw new IOException("下载内容不完整。");
                    }
                    catch (Exception ex)
                    {
                        if (!peers.IsCancellationRequested) Interlocked.CompareExchange(ref failure, ex, null);
                        peers.Cancel(); throw;
                    }
                    finally { _connections.Release(); }
                }).ToArray();
                try { await Task.WhenAll(parts); }
                catch when (failure is not null && !token.IsCancellationRequested)
                { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); throw; }
            }
            bool emptyUserFile = !overwrite && expected == 0 && hash is null && new FileInfo(temporary).Length == 0;
            if (!emptyUserFile && !await ValidAsync(temporary, hash, expected, token)) throw new IOException("下载文件的长度或 SHA-1 校验失败。");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
        }
        catch (InvalidDataException) when (segmented && !token.IsCancellationRequested)
        { await TransferAsync(url, destination, hash, expected, overwrite, false, progress, token, firstByteTimeout); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>限制响应头等待时间；正文使用独立空闲超时，允许大文件持续下载。</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token, TimeSpan? timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
    }

    /// <summary>按全实例累计字节预约发送时间，使并行文件和分段共享 MiB/s 限额；取消可及时打断等待。</summary>
    private async Task ThrottleAsync(int bytes, CancellationToken token)
    {
        if (_settings.SpeedLimitMiB == 0) return;
        long wait;
        lock (_rateLock)
        {
            long now = Stopwatch.GetTimestamp();
            _nextByteTime = Math.Max(now, _nextByteTime) +
                (long)((double)bytes * Stopwatch.Frequency / (_settings.SpeedLimitMiB * 1024L * 1024));
            wait = _nextByteTime - now;
        }
        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), token);
    }

    /// <summary>任务管理器退出后释放连接池和 HTTP 资源；调用前应等待所有下载结束。</summary>
    public void Dispose() { _http.Dispose(); _connections.Dispose(); }
}
