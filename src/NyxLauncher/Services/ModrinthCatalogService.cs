using System.Net;
using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>从 Modrinth v2 获取五类资源与版本筛选项；仅负责发现资源，不修改游戏文件。</summary>
public sealed partial class ModrinthCatalogService
{
    public const int PageSize = 20;
    public static readonly string[] LoaderIds = ["fabric", "forge", "neoforge", "quilt", "liteloader", "rift"];
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    { AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;
    public Func<SourceStrategy> Source { get; set; } = () => SourceStrategy.OfficialWithFallback;
    private IReadOnlyList<string>? _releaseVersions;

    /// <summary>复用平台连接；允许测试注入客户端和请求时限，默认每次请求最多等待 20 秒。</summary>
    public ModrinthCatalogService(HttpClient? client = null, TimeSpan? timeout = null)
    { _client = client ?? SharedClient; _timeout = timeout ?? TimeSpan.FromSeconds(20); }

    /// <summary>把资源分类、版本、模组或整合包加载器和资源类型组合为 AND 筛选；正确转义关键词并分页，取消由调用方处理。</summary>
    public async Task<ResourceSearchPage> SearchAsync(string category, string query, string gameVersion,
        string loader, int offset, CancellationToken token, string resourceType = "")
    {
        if (category is not ("mod" or "modpack" or "resourcepack" or "datapack" or "shader"))
            throw new ArgumentException("未知资源分类。", nameof(category));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var facets = new List<string[]> { new[] { "project_type:" + category } };
        if (gameVersion.Length > 0) facets.Add(["versions:" + gameVersion]);
        if (resourceType.Length > 0) facets.Add(["categories:" + resourceType]);
        if ((category is "mod" or "modpack") && loader.Length > 0)
        {
            if (!LoaderIds.Contains(loader)) throw new ArgumentException("未知加载器。", nameof(loader));
            facets.Add(["categories:" + loader]);
        }
        string path = "search?query=" + Uri.EscapeDataString(query.Trim()) + "&facets=" +
            Uri.EscapeDataString(JsonSerializer.Serialize(facets)) + $"&offset={offset}&limit={PageSize}&index=relevance";
        using var document = await GetAsync(path, token);
        var page = ParseSearch(document.RootElement, category);
        return page with { Projects = page.Projects.Select(x => x with { ReleaseVersions = _releaseVersions }).ToArray() };
    }

    /// <summary>读取并按发布日期倒序返回正式版，排除快照、远古及愚人节版本；保存正式版清单供卡片合并系列，仍允许搜索手动输入的其他 ID。</summary>
    public async Task<IReadOnlyList<ResourceGameVersion>> FetchGameVersionsAsync(CancellationToken token)
    {
        using var document = await GetAsync("tag/game_version", token);
        try
        {
            var entries = document.RootElement.EnumerateArray().ToArray();
            if (entries.Length == 0) throw new JsonException();
            var versions = entries.Where(x => x.GetProperty("version_type").GetString() == "release" &&
                    !AprilFoolsVersions.Ids.Contains(x.GetProperty("version").GetString() ?? ""))
                .OrderByDescending(x => x.GetProperty("date").GetDateTimeOffset())
                .Select(x =>
                {
                    string id = x.GetProperty("version").GetString() ?? throw new JsonException();
                    return new ResourceGameVersion(id, id);
                }).DistinctBy(x => x.Id).ToArray();
            if (versions.Any(x => string.IsNullOrWhiteSpace(x.Id))) throw new JsonException();
            _releaseVersions = versions.Select(x => x.Id).ToArray();
            return versions;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new HttpRequestException("Minecraft 版本列表暂时不可用，请重试。", ex); }
    }

    /// <summary>校验响应结构并提取真实统计和更新日期；数据包允许平台返回兼具模组类型的项目，其他资源不泄漏加载器标签。</summary>
    private static ResourceSearchPage ParseSearch(JsonElement root, string category)
    {
        try
        {
            int total = root.GetProperty("total_hits").GetInt32();
            if (total < 0) throw new JsonException();
            var projects = root.GetProperty("hits").EnumerateArray().Select(hit =>
            {
                string id = hit.GetProperty("project_id").GetString() ?? "";
                string title = hit.GetProperty("title").GetString() ?? "";
                if (id.Length == 0 || !id.All(char.IsAsciiLetterOrDigit) || string.IsNullOrWhiteSpace(title))
                    throw new JsonException();
                string icon = hit.GetProperty("icon_url").GetString() ?? "";
                if (!Uri.TryCreate(icon, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                    uri.Host != "cdn.modrinth.com" || uri.UserInfo.Length > 0) icon = "";
                long downloads = hit.GetProperty("downloads").GetInt64();
                if (downloads < 0) throw new JsonException();
                var versions = hit.GetProperty("versions").EnumerateArray().Select(x => x.GetString() ?? "")
                    .Where(x => x.Length > 0).Distinct().ToArray();
                var loaders = (category is "mod" or "modpack") ? hit.GetProperty("categories").EnumerateArray()
                    .Select(x => x.GetString() ?? "").Where(LoaderIds.Contains).Distinct().ToArray() : [];
                return new ResourceProject(id, title, hit.GetProperty("description").GetString() ?? "", icon,
                    downloads, hit.GetProperty("date_modified").GetDateTimeOffset(), versions, loaders, category);
            }).ToArray();
            return new ResourceSearchPage(projects, total);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new HttpRequestException("资源列表暂时不可用，请重试。", ex); }
    }

    /// <summary>读取平台实际资源类型并提供中文标签；只返回五类下载资源，数据包复用平台的模组类型标签。</summary>
    public async Task<IReadOnlyList<ResourceCategory>> FetchCategoriesAsync(CancellationToken token)
    {
        using var document = await GetAsync("tag/category", token);
        try
        {
            var categories = document.RootElement.EnumerateArray().Where(x =>
                x.GetProperty("project_type").GetString() is "mod" or "modpack" or "resourcepack" or "datapack" or "shader")
                .Select(x => new ResourceCategory(x.GetProperty("name").GetString() ?? throw new JsonException(),
                    ResourceCategoryLabels.Translate(x.GetProperty("name").GetString()!), x.GetProperty("project_type").GetString()!))
                .DistinctBy(x => (x.Id, x.ProjectType)).ToArray();
            if (categories.Length == 0) throw new JsonException();
            return categories;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new HttpRequestException("资源类型暂时不可用，请重试。", ex); }
    }

    /// <summary>按版本列表源策略读取元数据；可选 JSON 请求体用于批量哈希查询，双源分别创建请求，取消剩余请求。</summary>
    private Task<JsonDocument> GetAsync(string path, CancellationToken token, string? body = null) => SourceFallback.FetchAsync(Source(),
        (mirror, ct) => GetSourceAsync(path, mirror, ct, body), TimeSpan.FromSeconds(3), token);

    /// <summary>单源独立超时读取最多 8 MiB JSON；可选请求体使用 POST，发送登记 UA，保留限流提示和取消语义。</summary>
    private async Task<JsonDocument> GetSourceAsync(string path, bool mirror, CancellationToken token, string? body = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_timeout);
        try
        {
            var url = new Uri("https://api.modrinth.com/v2/" + path);
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, mirror ? ResourceMirror.Map(url)! : url);
            if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            request.Headers.UserAgent.ParseAdd(ResourceMirror.UserAgent);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            byte[] buffer = new byte[16384];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + read > 8 * 1024 * 1024) throw new JsonException();
                output.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(output.ToArray());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new HttpRequestException("连接 Modrinth 超时，请重试。"); }
        catch (HttpRequestException ex)
        { throw new HttpRequestException(ex.StatusCode == HttpStatusCode.TooManyRequests
            ? "请求过于频繁，请稍后重试。" : "无法连接 Modrinth，请检查网络后重试。", ex); }
        catch (Exception ex) when (ex is IOException or JsonException)
        { throw new HttpRequestException("Modrinth 返回的内容暂时不可用，请重试。", ex); }
    }
}
