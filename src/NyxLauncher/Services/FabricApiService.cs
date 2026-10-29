using System.Globalization;
using System.Net;
using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>Fabric API 模组的真实发布与主 JAR；保留游戏版本、长度和哈希供安装再次校验。</summary>
public sealed record FabricApiVersion(string Id, string Version, string GameVersion, string Channel,
    DateTimeOffset Published, string FileName, Uri Url, string Sha1, long Size)
{
    public string Detail => Published.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " · " +
        (Channel switch { "release" => "正式版", "beta" => "测试版", _ => "预览版" });
    public string Icon => "ms-appx:///Assets/fabric.png";
    public string AccessibleName => "Fabric API " + Version + "，" + Detail;
}

/// <summary>读取 Modrinth 官方 Fabric API 项目；与 Fabric Loader 分离，不将其他加载器或游戏版本的文件列为可选。</summary>
public sealed class FabricApiService
{
    public const string ProjectId = "P7dR8mSH";
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    { AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;

    /// <summary>复用连接并为整次清单请求设置 15 秒超时；测试可注入客户端和时限。</summary>
    public FabricApiService(HttpClient? client = null, TimeSpan? timeout = null)
    { _client = client ?? SharedClient; _timeout = timeout ?? TimeSpan.FromSeconds(15); }

    /// <summary>按原始游戏 ID 和 fabric 双重筛选，读取最多 8 MiB；空数组代表无版本，网络和格式错误保留原因，主动取消向上传播。</summary>
    public async Task<IReadOnlyList<FabricApiVersion>> FetchAsync(string game, CancellationToken token)
    {
        GameInstallationService.ValidateName(game);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_timeout);
        try
        {
            string url = $"https://api.modrinth.com/v2/project/{ProjectId}/version?loaders=%5B%22fabric%22%5D&game_versions=" +
                Uri.EscapeDataString(JsonSerializer.Serialize(new[] { game })) + "&include_changelog=false";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("NyxLauncher/0.1");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            byte[] buffer = new byte[16384];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + read > 8 * 1024 * 1024) throw new JsonException("Fabric API 清单超过 8 MiB。");
                output.Write(buffer, 0, read);
            }
            return Parse(game, output.ToArray());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new HttpRequestException("Fabric API：请求超时。"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException)
        { throw new HttpRequestException("Fabric API：" + ex.Message, ex); }
    }

    /// <summary>再次核对项目、游戏与加载器，选择主文件（未标主文件时取第一项），按发布时间排序去重；不接受源码包、缺失哈希或不安全文件名。</summary>
    internal static IReadOnlyList<FabricApiVersion> Parse(string game, byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Fabric API 响应不是版本列表。");
        var result = new List<FabricApiVersion>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("project_id").GetString() != ProjectId) throw new JsonException("Fabric API 项目不匹配。");
            if (!entry.GetProperty("game_versions").EnumerateArray().Any(x => x.GetString() == game) ||
                !entry.GetProperty("loaders").EnumerateArray().Any(x => x.GetString() == "fabric")) continue;
            var files = entry.GetProperty("files").EnumerateArray().ToArray();
            if (files.Length == 0) throw new JsonException("Fabric API 版本没有文件。");
            var primaries = files.Where(x => x.GetProperty("primary").GetBoolean()).ToArray();
            if (primaries.Length > 1) throw new JsonException("Fabric API 版本有多个主文件。");
            var file = primaries.FirstOrDefault();
            if (file.ValueKind == JsonValueKind.Undefined) file = files[0];
            if (file.TryGetProperty("file_type", out var type) && type.ValueKind != JsonValueKind.Null &&
                type.GetString() is not ("" or "unknown")) throw new JsonException("Fabric API 主文件不是运行用 JAR。");
            var version = new FabricApiVersion(entry.GetProperty("id").GetString()!, entry.GetProperty("version_number").GetString()!,
                game, entry.GetProperty("version_type").GetString()!, entry.GetProperty("date_published").GetDateTimeOffset(),
                file.GetProperty("filename").GetString()!, new Uri(file.GetProperty("url").GetString()!),
                file.GetProperty("hashes").GetProperty("sha1").GetString()!, file.GetProperty("size").GetInt64());
            Validate(version);
            result.Add(version);
        }
        return result.OrderByDescending(x => x.Published).DistinctBy(x => x.Id).ToArray();
    }

    /// <summary>在远程解析和安装入口校验相同的下载契约，限制官方项目 CDN、单级 JAR 名和 SHA-1，失败不写文件。</summary>
    internal static void Validate(FabricApiVersion version)
    {
        GameInstallationService.ValidateName(version.FileName);
        if (string.IsNullOrWhiteSpace(version.Id) || string.IsNullOrWhiteSpace(version.Version) ||
            version.Channel is not ("release" or "beta" or "alpha") ||
            !version.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            !version.FileName.StartsWith("fabric-api-", StringComparison.OrdinalIgnoreCase) ||
            version.Size <= 0 || version.Sha1 is null || version.Sha1.Length != 40 || !version.Sha1.All(Uri.IsHexDigit) ||
            !version.Url.IsAbsoluteUri || version.Url.Scheme != "https" || version.Url.Host != "cdn.modrinth.com" ||
            version.Url.UserInfo.Length > 0 || !version.Url.AbsolutePath.StartsWith($"/data/{ProjectId}/versions/", StringComparison.Ordinal))
            throw new JsonException("Fabric API 文件元数据无效。");
    }

    /// <summary>安装前再次要求精确游戏版本和 Fabric Loader；不自动切换加载器，也不将 API 当成独立加载器。</summary>
    internal static void ValidateSelection(string game, LoaderVersion? loader, FabricApiVersion? api)
    {
        if (api is null) return;
        Validate(api);
        if (loader?.Loader != LoaderKind.Fabric || api.GameVersion != game)
            throw new ArgumentException("Fabric API 必须与所选游戏版本及 Fabric 加载器一起安装。");
    }

    /// <summary>使用统一下载预算向实际 mods 目录原子发布并校验 SHA-1；复用完全一致文件，拒绝覆盖损坏文件或叠加另一版 API。</summary>
    internal static async Task InstallAsync(FabricApiVersion api, string mods, DownloadTransport transport, CancellationToken token)
    {
        Validate(api);
        string destination = Path.Combine(mods, api.FileName);
        GameInstallationService.EnsureNoLinks(destination);
        if (Directory.Exists(mods) && Directory.EnumerateFiles(mods).Any(x =>
            Path.GetFileName(x).StartsWith("fabric-api-", StringComparison.OrdinalIgnoreCase) &&
            x.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(x).Equals(api.FileName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("mods 中已存在另一版本 Fabric API，请先移走旧版或使用版本隔离。");
        if (await DownloadTransport.ValidAsync(destination, api.Sha1, api.Size, token)) return;
        await transport.DownloadAsync(api.Url, destination, api.Sha1, api.Size, false, false, null, token);
    }
}
