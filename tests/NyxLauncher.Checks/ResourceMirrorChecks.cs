using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using NyxLauncher.Services;
using SkiaSharp;

internal static class ResourceMirrorChecks
{
    /// <summary>验证域名边界、三种策略、动态设置、404 回退、取消、图标重定向及带哈希文件传输，所有输出位于独立临时目录。</summary>
    public static async Task RunAsync()
    {
        foreach (var (input, expected) in new[] {
            ("https://api.modrinth.com/v2/search?q=a%2Bb", "https://mod.mcimirror.top/modrinth/v2/search?q=a%2Bb"),
            ("https://api.curseforge.com/v1/mods/1", "https://mod.mcimirror.top/curseforge/v1/mods/1"),
            ("https://cdn.modrinth.com//data/a.jar", "https://mod.mcimirror.top/data/a.jar"),
            ("https://edge.forgecdn.net/files/1/a.jar", "https://mod.mcimirror.top/files/1/a.jar") })
            Check(ResourceMirror.Map(new(input))?.AbsoluteUri == expected, "端点映射和编码");
        foreach (string url in new[] { "https://mediafilez.forgecdn.net/files/a", "https://cdn.modrinth.com.evil/a",
            "http://cdn.modrinth.com/a", "https://cdn.modrinth.com:444/a", "https://user@cdn.modrinth.com/a" })
            Check(ResourceMirror.Map(new(url)) is null, "不改写非契约地址");
        var hosts = new List<string>();
        using var client = new HttpClient(new Handler((r, _) =>
        {
            hosts.Add(r.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"hits\":[],\"total_hits\":0}") });
        }));
        var settings = new DownloadSettings(VersionSource: SourceStrategy.MirrorFirst);
        var catalog = new ModrinthCatalogService(client) { Source = () => settings.VersionSource };
        foreach (var strategy in Enum.GetValues<SourceStrategy>())
        {
            settings = settings with { VersionSource = strategy };
            hosts.Clear();
            await catalog.SearchAsync("mod", "", "", "", 0, default);
            Check(hosts.SequenceEqual(new[] { strategy == SourceStrategy.MirrorFirst ? "mod.mcimirror.top" : "api.modrinth.com" }), "最新设置控制首选源");
        }
        hosts.Clear();
        using var missing = new HttpClient(new Handler((r, _) =>
        {
            hosts.Add(r.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(hosts.Count == 1 ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                { Content = new StringContent("[]") });
        }));
        await new FabricApiService(missing).FetchAsync("1.21.1", default, SourceStrategy.MirrorFirst);
        Check(hosts.SequenceEqual(new[] { "mod.mcimirror.top", "api.modrinth.com" }), "镜像未收录回退官方");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await catalog.FetchCategoriesAsync(cancel.Token); throw new Exception("取消失效"); }
        catch (OperationCanceledException) { }

        using var bitmap = new SKBitmap(2, 2);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        int calls = 0;
        using var icons = new HttpClient(new Handler((r, _) =>
        {
            calls++;
            if (calls == 1)
            {
                Check(r.RequestUri!.Host == "mod.mcimirror.top", "图标采用文件镜像源");
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://files.example/icon.png");
                return Task.FromResult(redirect);
            }
            Check(r.RequestUri!.Host == "files.example", "跟随镜像图标重定向");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png.ToArray()) });
        }));
        await new ResourceIconService(icons) { Source = () => SourceStrategy.MirrorFirst }
            .FetchAsync("https://cdn.modrinth.com/data/" + Guid.NewGuid() + "/icon.png", default);
        Check(calls == 2, "图标两跳均带 UA");
        bool slowCancelled = false;
        using var slow = new HttpClient(new Handler(async (r, ct) =>
        {
            if (r.RequestUri!.Host == "api.modrinth.com")
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { slowCancelled = true; throw; }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"hits\":[],\"total_hits\":0}") };
        }));
        await new ModrinthCatalogService(slow).SearchAsync("mod", "", "", "", 0, default);
        Check(slowCancelled, "官方缓慢触发镜像并取消旧请求");
        int unsafeCalls = 0;
        using var unsafeIcon = new HttpClient(new Handler((_, _) =>
        {
            unsafeCalls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://localhost/private");
            return Task.FromResult(response);
        }));
        try
        {
            await new ResourceIconService(unsafeIcon).FetchAsync("https://cdn.modrinth.com/data/" + Guid.NewGuid() + "/icon.png", default);
            throw new Exception("接受不安全重定向");
        }
        catch (HttpRequestException) { Check(unsafeCalls == 2, "拒绝降级重定向后仅尝试另一源"); }
        await FilesAsync();
        Console.WriteLine("MCIM 映射、设置、回退、UA、重定向与文件完整性检查通过。");
    }

    /// <summary>模拟分段文件验证 HEAD、Range 和 GET 的 UA；镜像损坏回退官方，禁止覆盖语义及自定义 URL 保持不变。</summary>
    private static async Task FilesAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-mcim-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            byte[] data = new byte[4 * 1024 * 1024]; data[0] = 42;
            string sha1 = Convert.ToHexString(SHA1.HashData(data));
            var requests = new System.Collections.Concurrent.ConcurrentBag<(string Host, string Method, bool Range)>();
            using var http = new HttpClient(new Handler((r, _) =>
            {
                requests.Add((r.RequestUri!.Host, r.Method.Method, r.Headers.Range is not null));
                var range = r.Headers.Range?.Ranges.Single();
                long start = range?.From ?? 0, end = range?.To ?? data.Length - 1;
                var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                    { Content = new ByteArrayContent(data[(int)start..((int)end + 1)]) };
                response.Headers.ETag = new EntityTagHeaderValue("\"fixture\"");
                response.Headers.AcceptRanges.Add("bytes");
                if (range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.Length);
                return Task.FromResult(response);
            }));
            using var transport = new DownloadTransport(new(FileSource: SourceStrategy.MirrorFirst), http);
            var url = new Uri("https://cdn.modrinth.com/data/test/versions/one/a.jar");
            await transport.DownloadAsync(url, Path.Combine(root, "a.jar"), sha1, data.Length, false, true, null, default, mirrorResource: true);
            Check(requests.All(x => x.Host == "mod.mcimirror.top") && requests.Any(x => x.Method == "HEAD") && requests.Count(x => x.Range) == 4,
                "分段文件与 HEAD 均使用镜像和 UA");
            requests.Clear();
            await transport.DownloadAsync(url, Path.Combine(root, "custom.jar"), sha1, data.Length, false, false, null, default);
            Check(requests.Single().Host == "cdn.modrinth.com", "用户自定义下载不改写");
            int attempt = 0;
            using var corrupt = new HttpClient(new Handler((r, _) =>
            {
                Check(r.RequestUri!.Host == (++attempt == 1 ? "mod.mcimirror.top" : "cdn.modrinth.com"), "损坏文件回退官方");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(attempt == 1 ? [0] : [42]) });
            }));
            using var fallback = new DownloadTransport(new(FileSource: SourceStrategy.MirrorFirst), corrupt);
            await fallback.DownloadAsync(url, Path.Combine(root, "fallback.jar"), Convert.ToHexString(SHA1.HashData([42])), 1,
                false, false, null, default, mirrorResource: true);
            Check(File.ReadAllBytes(Path.Combine(root, "fallback.jar")).SequenceEqual(new byte[] { 42 }) && attempt == 2 &&
                !Directory.EnumerateFiles(root, "*.part").Any(), "校验成功后发布并清理临时文件");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>显式实网验收仅访问公开 MCIM：读取资源与发布并下载一个真实文件到临时目录，验证 SHA-1 后清理。</summary>
    public static async Task LiveAsync()
    {
        HttpClient.DefaultProxy = new WebProxy();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var http = new HttpClient(new LiveHandler());
        var catalog = new ModrinthCatalogService(http) { Source = () => SourceStrategy.MirrorFirst };
        var page = await catalog.SearchAsync("mod", "Fabric API", "1.21.1", "fabric", 0, timeout.Token);
        Check(page.Projects.Count > 0, "镜像搜索");
        var file = (await new FabricApiService(http).FetchAsync("1.21.1", timeout.Token, SourceStrategy.MirrorFirst)).First();
        await new ResourceIconService(http) { Source = () => SourceStrategy.MirrorFirst }.FetchAsync(page.Projects[0].IconUrl, timeout.Token);
        string root = Path.Combine(Path.GetTempPath(), "nyx-mcim-live-" + Guid.NewGuid());
        try
        {
            using var transport = new DownloadTransport(new(FileSource: SourceStrategy.MirrorFirst), http);
            await FabricApiService.InstallAsync(file, root, transport, timeout.Token);
            Check(await DownloadTransport.ValidAsync(Path.Combine(root, file.FileName), file.Sha1, file.Size, timeout.Token), "真实文件 SHA-1");
            Console.WriteLine("MCIM 优先实网搜索、发布、图标与下载校验通过：" + file.FileName);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>断言实际请求或文件结果，失败立即停止验收。</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class LiveHandler : DelegatingHandler
    {
        /// <summary>使用标准 TLS 和重定向访问真实镜像，独立于应用代理设置。</summary>
        public LiveHandler() : base(new HttpClientHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.All }) { }

        /// <summary>禁止官方回退掩盖镜像故障，记录请求类型并核对登记 UA；CDN 重定向由标准处理器完成。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri!.Host == "mod.mcimirror.top", "实网验收不允许回退官方");
            Check(request.Headers.UserAgent.ToString() == ResourceMirror.UserAgent, "实网 UA");
            Console.WriteLine(request.Method + " " + request.RequestUri.GetLeftPart(UriPartial.Path) + " UA=" + ResourceMirror.UserAgent);
            try
            {
                var response = await base.SendAsync(request, token);
                Console.WriteLine("HTTP " + (int)response.StatusCode);
                return response;
            }
            catch (HttpRequestException ex) { Console.WriteLine(ex.ToString()); throw; }
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>拦截每一个请求验证完整 UA，模拟响应但保留生产回退、解析、校验与取消链路。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(request.Headers.UserAgent.ToString() == ResourceMirror.UserAgent && ResourceMirror.UserAgent.StartsWith("Nyx-Launcher/"), "报备 UA");
            return send(request, token);
        }
    }
}
