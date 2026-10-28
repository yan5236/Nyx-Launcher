using System.Globalization;
using System.Net;
using System.Text.Json;
using NyxLauncher.Views;

namespace NyxLauncher.Services;

/// <summary>只获取 Java 版本索引；不下载游戏文件、不安装、不启动。</summary>
public sealed class VersionCatalogService
{
    public const string OfficialUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string MirrorUrl = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client;
    private readonly TimeSpan _fallbackDelay;
    private readonly TimeSpan _timeout;

    /// <summary>复用 HTTP 连接；可注入客户端和时限进行确定性测试。</summary>
    public VersionCatalogService(HttpClient? client = null, TimeSpan? fallbackDelay = null, TimeSpan? timeout = null)
    {
        _client = client ?? SharedClient;
        _fallbackDelay = fallbackDelay ?? TimeSpan.FromSeconds(3);
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>按优先策略获取首个有效清单；混合策略在官方缓慢时并行尝试镜像，成功后取消另一请求。</summary>
    public Task<IReadOnlyList<DownloadEntry>> FetchAsync(SourceStrategy strategy, CancellationToken cancellationToken) =>
        SourceFallback.FetchAsync(strategy, (mirror, token) => FetchSourceAsync(mirror ? MirrorUrl : OfficialUrl, token),
            _fallbackDelay, cancellationToken);

    /// <summary>以独立超时读取最多 8 MiB 的清单，保留 TLS 校验；网络、格式和超时错误附带来源。</summary>
    private async Task<IReadOnlyList<DownloadEntry>> FetchSourceAsync(string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_timeout);
        string source = url == OfficialUrl ? "官方源" : "BMCLAPI 镜像源";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("NyxLauncher/0.1");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > 8 * 1024 * 1024) throw new JsonException("清单超过大小限制。");
                buffer.Write(chunk, 0, read);
            }
            return Parse(buffer.ToArray());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new HttpRequestException($"{source}：请求超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            throw new HttpRequestException($"{source}：{ex.Message}", ex);
        }
    }

    /// <summary>校验远程数据，按发布时间降序去重；愚人节类型只由独立 ID 清单决定。</summary>
    public static IReadOnlyList<DownloadEntry> Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
            throw new JsonException("响应缺少版本列表。");
        var entries = new List<(DownloadEntry Entry, DateTimeOffset Time)>();
        foreach (var version in versions.EnumerateArray())
        {
            if (version.ValueKind != JsonValueKind.Object ||
                !version.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()) ||
                !version.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                !version.TryGetProperty("releaseTime", out var time) || time.ValueKind != JsonValueKind.String ||
                !time.TryGetDateTimeOffset(out var releaseTime))
                throw new JsonException("版本条目缺少有效的名称、类型或发布时间。");
            string name = id.GetString()!;
            string kind = AprilFoolsVersions.Ids.Contains(name) ? "愚人节版" : type.GetString() switch
            {
                "release" => "正式版", "snapshot" => "快照版",
                "old_alpha" or "old_beta" => "远古版", _ => "其他"
            };
            entries.Add((new DownloadEntry(name, releaseTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), kind, "\uE7FC"), releaseTime));
        }
        if (entries.Count == 0) throw new JsonException("服务器返回了空版本列表。");
        return entries.OrderByDescending(item => item.Time).Select(item => item.Entry)
            .DistinctBy(entry => entry.Title, StringComparer.Ordinal).ToArray();
    }
}
