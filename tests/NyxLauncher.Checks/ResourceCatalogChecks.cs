using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NyxLauncher.Services;

internal static class ResourceCatalogChecks
{
    private static int _checks;

    /// <summary>构造含多加载器、跨版本及真实格式图标的资源样本，不依赖线上排序或固定统计值。</summary>
    private static JsonObject Project() => new()
    {
        ["project_id"] = "P7dR8mSH", ["title"] = "Fabric API", ["description"] = "测试简介",
        ["icon_url"] = "https://cdn.modrinth.com/data/P7dR8mSH/icon.png", ["downloads"] = 52000,
        ["date_modified"] = "2026-10-29T08:00:00Z", ["versions"] = new JsonArray("1.19.4", "1.20.1", "1.21", "1.21.1"),
        ["categories"] = new JsonArray("library", "fabric", "forge", "neoforge", "quilt")
    };

    /// <summary>验证平台筛选、分页、五类解析、中文类型和图标，覆盖坏响应、限流、超时及主动取消。</summary>
    public static async Task RunAsync()
    {
        foreach (string category in new[] { "mod", "modpack", "resourcepack", "datapack", "shader" })
        {
            using var client = new HttpClient(new Handler((request, _) =>
            {
                var query = request.RequestUri!.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
                    .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
                using var facets = JsonDocument.Parse(query["facets"]);
                string[] filters = facets.RootElement.EnumerateArray().SelectMany(x => x.EnumerateArray()).Select(x => x.GetString()!).ToArray();
                Check(query["query"] == "中文 & a+b" && query["offset"] == "20" && query["limit"] == "20", "保留中文关键词及特殊字符与分页");
                Check(filters.Contains("project_type:" + category) && filters.Contains("versions:1.21.1") &&
                    filters.Contains("categories:fabric") == (category == "mod"), category + " 精确分类且仅模组添加加载器筛选");
                Check(request.Headers.UserAgent.Count > 0 && request.RequestUri.Host == "api.modrinth.com" &&
                    filters.Contains("categories:optimization"), "平台地址、客户端标识和资源类型筛选");
                return Task.FromResult(Response(new JsonObject { ["hits"] = new JsonArray(Project()), ["total_hits"] = 42 }.ToJsonString()));
            }));
            var page = await new ModrinthCatalogService(client).SearchAsync(category, "中文 & a+b", "1.21.1", "fabric", 20, default, "optimization");
            var project = page.Projects.Single();
            Check(page.Total == 42 && project.Title == "Fabric API" && project.Description == "测试简介" &&
                project.Downloads == 52000 && project.Updated.Day == 29 && project.IconUrl.StartsWith("https://cdn.modrinth.com/"), "真实字段及匹配总数");
            Check(project.HasLoaders == (category == "mod") && (category != "mod" || project.LoadersText == "Fabric / Forge / NeoForge / Quilt"),
                category + " 不混入资源标签或非模组加载器");
            Check(project.Category == category && (category != "mod" || project.LoaderIcons.Contains("ms-appx:///Assets/quilt.svg")), "分类占位及 Quilt 图标地址");
            Check(project.VersionsText == "1.19–1.21" && project.VersionsDetail.Contains("1.20.1") && project.DownloadText == "5.2万", "卡片合并系列并保留完整悬停版本");
        }
        using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response("{\"hits\":[],\"total_hits\":0}")))))
        {
            var service = new ModrinthCatalogService(client);
            Check((await service.SearchAsync("shader", "", "", "", 0, default)).Projects.Count == 0, "空结果不是错误");
            await Throws<ArgumentException>(() => service.SearchAsync("game", "", "", "", 0, default));
            await Throws<ArgumentException>(() => service.SearchAsync("mod", "", "", "invalid", 0, default));
            await Throws<ArgumentOutOfRangeException>(() => service.SearchAsync("mod", "", "", "", -1, default));
        }
        const string versions = """[{"version":"1.20.1","version_type":"release","date":"2023-06-12T00:00:00Z"},{"version":"1.21.1","version_type":"release","date":"2024-08-08T00:00:00Z"},{"version":"24w03a","version_type":"snapshot","date":"2024-01-17T00:00:00Z"},{"version":"b1.7.3","version_type":"beta","date":"2011-07-08T00:00:00Z"},{"version":"a1.2.6","version_type":"alpha","date":"2010-12-03T00:00:00Z"},{"version":"1.RV-Pre1","version_type":"release","date":"2016-04-01T00:00:00Z"},{"version":"26w14a","version_type":"release","date":"2026-04-01T00:00:00Z"}]""";
        using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(versions)))))
        {
            var list = await new ModrinthCatalogService(client).FetchGameVersionsAsync(default);
            Check(list.Select(x => x.Id).SequenceEqual(new[] { "1.21.1", "1.20.1" }) && list.All(x => x.Label == x.Id),
                "下拉仅保留倒序正式版，排除快照、远古及错误标注为正式版的愚人节版本");
        }
        using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response("""[{"version":"24w03a","version_type":"snapshot","date":"2024-01-17T00:00:00Z"}]""")))))
            Check((await new ModrinthCatalogService(client).FetchGameVersionsAsync(default)).Count == 0, "只有特殊版本时允许下拉仅显示全部");
        string[] releases = ["1.12", "1.13", "1.14", "1.15", "1.16", "1.17", "1.18", "1.19", "1.20", "1.21", "26.1", "26.2", "26.3"];
        foreach (var sample in new (string[] Versions, string Expected)[]
        {
            ([], "未标注版本"), (["24w03a", "b1.7.3", "26w14a"], "非正式版"),
            (["1.20.1", "1.20.4"], "1.20"), (["1.12", "1.16", "1.17"], "1.16–1.17、1.12"),
            (["1.19", "1.20.1", "1.21", "26.1", "26.2", "26.3"], "1.19+"),
            (["1.12", "26.1", "26.2", "26.3"], "26.1+、1.12"),
            (["1.12", "1.14", "1.16", "1.18", "26.2", "26.3"], "26.2+、1.18、1.16…"),
            (["1.9", "1.10", "1.11"], "1.9–1.11"), (["1.20-pre1", "1.21.1", "1.21.1"], "1.21")
        }) Check(ResourceVersionSummary.Format(sample.Versions, releases) == sample.Expected, "紧凑版本摘要：" + sample.Expected);
        using (var client = new HttpClient(new Handler((request, _) =>
        {
            Check(Uri.UnescapeDataString(request.RequestUri!.Query).Contains("versions:24w03a"), "手动快照 ID 原样传给平台");
            return Task.FromResult(Response("{\"hits\":[],\"total_hits\":0}"));
        }))) await new ModrinthCatalogService(client).SearchAsync("mod", "", "24w03a", "", 0, default);
        foreach (string body in new[] { "[]", "{}", "[{\"version\":\"1.21\"}]" })
        {
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(body))));
            await Throws<HttpRequestException>(() => new ModrinthCatalogService(client).FetchGameVersionsAsync(default));
        }
        foreach (string body in new[] { "<html>", "{}", "{\"hits\":[],\"total_hits\":-1}",
            "{\"hits\":[{}],\"total_hits\":1}", "{\"hits\":{},\"total_hits\":1}" })
        {
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(body))));
            await Throws<HttpRequestException>(() => new ModrinthCatalogService(client).SearchAsync("mod", "", "", "", 0, default));
        }
        var unsafeIcon = Project(); unsafeIcon["icon_url"] = "http://localhost/private";
        using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(
            new JsonObject { ["hits"] = new JsonArray(unsafeIcon), ["total_hits"] = 1 }.ToJsonString())))))
            Check((await new ModrinthCatalogService(client).SearchAsync("mod", "", "", "", 0, default)).Projects[0].IconUrl == "", "外部不安全图标回退");
        using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)))))
        {
            try { await new ModrinthCatalogService(client).SearchAsync("mod", "", "", "", 0, default); throw new Exception("限流未失败"); }
            catch (HttpRequestException ex) { Check(ex.Message == "请求过于频繁，请稍后重试。", "限流使用用户可理解的中文提示"); }
        }
        using (var client = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(); })))
        {
            var service = new ModrinthCatalogService(client, TimeSpan.FromMilliseconds(30));
            await Throws<HttpRequestException>(() => service.SearchAsync("mod", "", "", "", 0, default));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Throws<OperationCanceledException>(() => service.SearchAsync("mod", "", "", "", 0, cancel.Token));
        }
        const string categories = """[{"name":"optimization","project_type":"mod"},{"name":"magic","project_type":"modpack"},{"name":"32x","project_type":"resourcepack"},{"name":"reflections","project_type":"shader"},{"name":"economy","project_type":"minecraft_java_server"}]""";
        using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(categories)))))
        {
            var types = await new ModrinthCatalogService(client).FetchCategoriesAsync(default);
            Check(types.Select(x => x.Label).SequenceEqual(new[] { "优化", "魔法", "32 倍分辨率", "反射" }), "中文类型按项目分类且排除服务器标签");
        }
        await ResourceIconChecks.RunAsync();
        Console.WriteLine($"通过 {_checks} 项资源目录检查。");
    }

    /// <summary>实网验收五类资源、中文类型、筛选和多页结果，并下载解码一张 WebP 图标；不安装任何资源文件。</summary>
    public static async Task LiveAsync()
    {
        var service = new ModrinthCatalogService();
        var categories = await service.FetchCategoriesAsync(default);
        Check(categories.All(x => x.Label != "其他类型") && categories.Any(x => x.Id == "optimization" && x.Label == "优化"), "实网类型完整中文标签");
        var versions = await service.FetchGameVersionsAsync(default);
        Check(versions.Any(x => x.Id == "1.21.1"), "实网版本清单");
        foreach (string category in new[] { "mod", "modpack", "resourcepack", "datapack", "shader" })
        {
            var page = await service.SearchAsync(category, "", "1.21.1", "", 0, default);
            Check(page.Total > 0 && page.Projects.Count > 0 && page.Projects.All(x => x.Versions.Contains("1.21.1")), category + " 实网版本筛选");
            Console.WriteLine($"{category}: {page.Total} 项，首项 {page.Projects[0].Title}");
        }
        var fabric = await service.SearchAsync("mod", "sodium", "1.21.1", "fabric", 0, default);
        Check(fabric.Projects.Any(x => x.Title.Contains("Sodium") && x.Loaders.Contains("fabric")), "实网关键词与加载器");
        var first = await service.SearchAsync("mod", "", "", "", 0, default);
        var second = await service.SearchAsync("mod", "", "", "", 20, default);
        Check(second.Projects.Count > 0 && second.Projects[0].Id != first.Projects[0].Id, "实网分页");
        var third = await service.SearchAsync("mod", "", "", "", 40, default);
        Check(third.Projects.Count > 0 && third.Projects[0].Id != second.Projects[0].Id, "实网第三页");
        var optimized = await service.SearchAsync("mod", "sodium", "1.21.1", "fabric", 0, default, "optimization");
        Check(optimized.Projects.Any(x => x.Title == "Sodium"), "实网类型与版本、加载器共同筛选");
        var webp = first.Projects.First(x => x.IconUrl.EndsWith(".webp"));
        byte[] icon = await new ResourceIconService().FetchAsync(webp.IconUrl, default);
        Check(icon.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 }), "实网 WebP 图标转 PNG");
        Console.WriteLine($"通过 {_checks} 项资源目录实网检查。");
    }

    /// <summary>构造正常 JSON 响应，供真实请求编码、解析及错误处理逻辑使用。</summary>
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    /// <summary>断言具体业务结果，失败立即报告检查名称。</summary>
    private static void Check(bool value, string name) { if (!value) throw new Exception("失败：" + name); _checks++; }

    /// <summary>要求操作以指定异常失败，意外成功或错误类型不能计为通过。</summary>
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { _checks++; return; } throw new Exception("未抛出异常：" + typeof(T).Name); }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>仅替换网络传输，保留请求编码、解析和取消的实际实现。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
