using System.Net;
using System.Text;
using System.Text.Json;
using NyxLauncher.Services;

internal static class Program
{
    private const string Valid = """{"versions":[{"id":"1.0","type":"release","releaseTime":"2011-11-18T00:00:00Z"}]}""";
    private static int _checks;

    /// <summary>运行离线断言；--live 验证游戏清单，--live-loaders 验证加载器，--live-fabric 仅实测 Fabric 26.3，均不下载游戏文件。</summary>
    private static async Task Main(string[] args)
    {
        var rows = AprilFoolsVersions.Ids.Select(id => new { id, type = "snapshot", releaseTime = "2026-04-01T12:00:00Z" })
            .Concat(new[] {
                new { id = "normal-april", type = "release", releaseTime = "2026-04-01T11:00:00Z" },
                new { id = "b1.7.3", type = "old_beta", releaseTime = "2011-07-08T00:00:00Z" },
                new { id = "normal-april", type = "release", releaseTime = "2026-04-01T10:00:00Z" } });
        var parsed = VersionCatalogService.Parse(JsonSerializer.SerializeToUtf8Bytes(new { versions = rows }));
        Check(parsed.Count == 11 && parsed.Count(x => x.Kind == "愚人节版") == 9, "愚人节独立清单和去重");
        Check(parsed.Single(x => x.Title == "normal-april").Kind == "正式版", "四月一日普通版本不误判");
        Check(parsed[^1].Title == "b1.7.3" && parsed[^1].Kind == "远古版", "发布时间排序和远古版分类");
        foreach (string invalid in new[] { "{}", "[]", """{"versions":[]}""", """{"versions":[{"id":"x"}]}""", "<html>" })
            await Throws<JsonException>(() => Task.FromResult(VersionCatalogService.Parse(Encoding.UTF8.GetBytes(invalid))), "拒绝无效清单");

        foreach (var strategy in Enum.GetValues<SourceStrategy>())
        {
            var hosts = new List<string>();
            using var client = new HttpClient(new Handler((request, token) =>
            {
                hosts.Add(request.RequestUri!.Host);
                return Task.FromResult(Response(hosts.Count == 1 ? "<html>" : Valid));
            }));
            var result = await new VersionCatalogService(client).FetchAsync(strategy, default);
            Check(result[0].Title == "1.0" && hosts.Count == 2 &&
                hosts[0] == (strategy == SourceStrategy.MirrorFirst ? "bmclapi2.bangbang93.com" : "piston-meta.mojang.com"),
                "三种策略顺序及坏响应回退");
        }

        bool loserCancelled = false;
        using (var client = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "piston-meta.mojang.com")
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { loserCancelled = true; throw; }
            }
            return Response(Valid);
        })))
        {
            var result = await new VersionCatalogService(client, TimeSpan.FromMilliseconds(20))
                .FetchAsync(SourceStrategy.OfficialWithFallback, default);
            Check(result.Count == 1 && loserCancelled, "慢官方触发镜像且取消落后请求");
        }

        int requests = 0;
        using (var client = new HttpClient(new Handler((request, token) =>
        {
            requests++;
            return Task.FromResult(Response(Valid));
        })))
        {
            await new VersionCatalogService(client).FetchAsync(SourceStrategy.OfficialWithFallback, default);
            Check(requests == 1, "快速官方成功不请求镜像");
        }

        using (var client = new HttpClient(new Handler((request, token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
        {
            try
            {
                await new VersionCatalogService(client).FetchAsync(SourceStrategy.OfficialFirst, default);
                throw new Exception("应报告双源失败");
            }
            catch (HttpRequestException ex)
            {
                Check(ex.Message.Contains("官方源") && ex.Message.Contains("BMCLAPI") && ex.Message.Contains("503"),
                    "双源错误包含来源及状态码");
            }
        }

        using (var client = new HttpClient(new Handler(async (request, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Response(Valid);
        })))
        {
            await Throws<HttpRequestException>(() => new VersionCatalogService(client, timeout: TimeSpan.FromMilliseconds(20))
                .FetchAsync(SourceStrategy.OfficialFirst, default), "双源超时终止");
            using var cancel = new CancellationTokenSource(20);
            await Throws<OperationCanceledException>(() => new VersionCatalogService(client)
                .FetchAsync(SourceStrategy.OfficialWithFallback, cancel.Token), "用户取消不包装为网络失败");
        }

        string directory = Path.Combine(Path.GetTempPath(), "nyx-settings-check-" + Guid.NewGuid());
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new DownloadSettingsStore(path);
            Check(store.Current.MaxConnections == 16 && store.Current.SpeedLimitMiB == 0, "缺失设置使用默认值");
            var settings = new DownloadSettings(SourceStrategy.MirrorFirst, SourceStrategy.OfficialFirst, 1, 100);
            Check(store.Save(settings) is null && new DownloadSettingsStore(path).Current == settings, "设置持久化回读");
            Check(store.Save(settings with { MaxConnections = 0 }) is not null && store.Current == settings, "拒绝越界且保留旧值");
            File.WriteAllText(path, "{broken");
            Check(new DownloadSettingsStore(path).LoadError is not null && File.ReadAllText(path) == "{broken", "损坏设置报告错误且不覆盖");
            var blocked = new DownloadSettingsStore(directory);
            Check(blocked.Save(settings) is not null && blocked.Current != settings, "写入失败不改变内存设置");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        await LoaderChecks.RunAsync(args.Contains("--live-loaders"), args.Contains("--live-fabric"));
        if (args.Contains("--live"))
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            foreach (string url in new[] { VersionCatalogService.OfficialUrl, VersionCatalogService.MirrorUrl })
            {
                var live = VersionCatalogService.Parse(await client.GetByteArrayAsync(url));
                Check(live.Count > 100 && live.Any(x => x.Kind == "愚人节版"), "真实端点清单");
                Console.WriteLine($"{new Uri(url).Host}: {live.Count} 条，愚人节 {live.Count(x => x.Kind == "愚人节版")} 条");
            }
        }
        Console.WriteLine($"通过 {_checks} 项检查。");
    }

    /// <summary>创建 HTTP 测试响应，模拟真实 JSON 或错误正文。</summary>
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>断言业务结果；不满足时立即失败并标明行为。</summary>
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }

    /// <summary>验证异步操作必须抛出指定异常，防止错误被静默吞掉。</summary>
    private static async Task Throws<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { _checks++; return; }
        throw new Exception(name);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>将请求交给测试场景，并保留真实取消令牌。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
