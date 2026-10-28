using System.Net;
using System.Text;
using System.Text.Json;
using NyxLauncher.Services;

internal static class LoaderVersionChecks
{
    private static int _checks;
    private const string Fabric = """[{"loader":{"version":"0.9.0","stable":false},"intermediary":{"version":"1.21.1"}},{"loader":{"version":"0.20.1","stable":true},"intermediary":{"version":"1.21.1"}},{"loader":{"version":"0.20.1","stable":true},"intermediary":{"version":"1.21.1"}}]""";
    private const string Forge = """[{"mcversion":"1.21.1","version":"52.0.1","modified":"2024-08-08T14:10:06.000Z"}]""";
    private const string ForgeMaven = "<metadata><groupId>net.minecraftforge</groupId><artifactId>forge</artifactId><versioning><versions><version>1.21.1-52.0.1</version></versions></versioning></metadata>";

    /// <summary>覆盖真实字段、排序去重、日期缺失、源策略、取消与大小限制；可选验证三个加载器的官方和 BMCLAPI 实网清单。</summary>
    public static async Task RunAsync(bool live)
    {
        var fabric = LoaderAvailabilityService.ParseVersions(LoaderKind.Fabric, "1.21.1", Encoding.UTF8.GetBytes(Fabric));
        Check(fabric.Count == 2 && fabric[0].Version == "0.20.1" && fabric[0].Detail == "稳定版" &&
            fabric[1].Detail == "测试版", "Fabric 按 loader.stable 显示稳定性，并自然排序去重");
        foreach (string invalid in new[] { Fabric.Replace("true", "null"), Fabric.Replace("\"stable\":false", "\"stable\":\"false\"") })
            await Throws<JsonException>(() => Task.FromResult(LoaderAvailabilityService.ParseVersions(
                LoaderKind.Fabric, "1.21.1", Encoding.UTF8.GetBytes(invalid))), "不能猜测 Fabric 稳定性");
        var forge = LoaderVersionParser.ParseForgeMirror("1.21.1", Encoding.UTF8.GetBytes(Forge));
        Check(forge[0].Detail == "2024-08-08" && forge[0].Id == "1.21.1-52.0.1", "Forge 镜像使用真实日期和完整坐标");
        var branch = LoaderVersionParser.ParseForgeMirror("1.21.1", Encoding.UTF8.GetBytes(Forge.Replace("\"version\":", "\"branch\":\"legacy\",\"version\":")));
        Check(branch[0].Id == "1.21.1-52.0.1-legacy", "Forge 保留历史分支坐标");
        foreach (string invalid in new[] { Forge.Replace("1.21.1", "1.21"), Forge.Replace("2024-08-08T14:10:06.000Z", "bad"), "{}" })
            await Throws<JsonException>(() => Task.FromResult(LoaderVersionParser.ParseForgeMirror(
                "1.21.1", Encoding.UTF8.GetBytes(invalid))), "Forge 拒绝错误游戏和日期");
        var dates = LoaderVersionParser.AddForgeDates(forge, """<td class="download-version">52.0.1<i></i></td><td class="download-time" title="2024-08-08 14:10:06">2024-08-08</td>"""u8.ToArray());
        Check(dates[0].Detail == "2024-08-08", "Forge 官方发布行日期匹配");
        await Throws<JsonException>(() => Task.FromResult(LoaderVersionParser.AddForgeDates(forge, "<html/>"u8.ToArray())), "官方日期缺失必须回退");
        string neoXml = "<metadata><groupId>net.neoforged</groupId><artifactId>neoforge</artifactId><versioning><versions><version>21.1.9-beta</version><version>21.1.10</version><version>21.10.1</version></versions></versioning></metadata>";
        var neo = LoaderAvailabilityService.ParseVersions(LoaderKind.NeoForge, "1.21.1", Encoding.UTF8.GetBytes(neoXml));
        Check(neo.Count == 2 && neo[0].Version == "21.1.10" && neo[0].Detail == "稳定版" && neo[1].Detail == "测试版", "NeoForge 稳定性、排序和游戏匹配");

        foreach (var strategy in Enum.GetValues<SourceStrategy>())
        {
            var urls = new List<Uri>();
            using var client = new HttpClient(new Handler((request, token) =>
            {
                urls.Add(request.RequestUri!);
                return Task.FromResult(Response(urls.Count == 1 ? "<bad>" :
                    request.RequestUri!.AbsolutePath.EndsWith("/game") ? "[{\"version\":\"1.21.1\"}]" : Fabric));
            }));
            var versions = await new LoaderAvailabilityService(client).FetchAsync(LoaderKind.Fabric, "1.21.1", strategy, default);
            Check(versions.Count == 2 && urls.Count == 3 &&
                urls[0].Host == (strategy == SourceStrategy.MirrorFirst ? "bmclapi2.bangbang93.com" : "meta.fabricmc.net") &&
                urls[0].Host != urls[1].Host && urls[1].Host == urls[2].Host,
                "三个源策略均回退完整有效清单，不混用 Fabric 支持列表和 loader 来源");
        }
        using (var client = new HttpClient(new Handler((request, token) => Task.FromResult(Response(
            request.RequestUri!.Host == "bmclapi2.bangbang93.com" ? Forge :
            request.RequestUri.Host == "maven.minecraftforge.net" ? ForgeMaven : "<html/>")))))
        {
            var versions = await new LoaderAvailabilityService(client).FetchAsync(LoaderKind.Forge, "1.21.1", SourceStrategy.OfficialFirst, default);
            Check(versions[0].Detail == "2024-08-08", "Forge 官方日期不可用时镜像补足完整列表");
        }
        bool cancelled = false;
        using (var client = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host != "bmclapi2.bangbang93.com")
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled = true; throw; }
            }
            return Response(neoXml);
        })))
        {
            var versions = await new LoaderAvailabilityService(client, fallbackDelay: TimeSpan.FromMilliseconds(10))
                .FetchAsync(LoaderKind.NeoForge, "1.21.1", SourceStrategy.OfficialWithFallback, default);
            Check(versions.Count == 2 && cancelled, "官方缓慢时镜像获胜并取消旧请求");
        }
        foreach (string body in new[] { "<html>", new string('x', 8 * 1024 * 1024 + 1) })
        {
            using var client = new HttpClient(new Handler((request, token) => Task.FromResult(Response(body))));
            await Throws<HttpRequestException>(() => new LoaderAvailabilityService(client).FetchAsync(
                LoaderKind.Forge, "1.21.1", SourceStrategy.MirrorFirst, default), "双源坏数据和超限不能当作空列表");
        }
        using (var client = new HttpClient(new Handler(async (request, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Response("[]");
        })))
        {
            await Throws<HttpRequestException>(() => new LoaderAvailabilityService(client, TimeSpan.FromMilliseconds(10))
                .FetchAsync(LoaderKind.Forge, "1.21.1", SourceStrategy.MirrorFirst, default), "双源超时明确失败");
            using var cancel = new CancellationTokenSource(20);
            await Throws<OperationCanceledException>(() => new LoaderAvailabilityService(client)
                .FetchAsync(LoaderKind.Fabric, "1.21.1", SourceStrategy.OfficialWithFallback, cancel.Token), "离开安装页取消版本列表");
        }
        if (live) await LiveAsync();
        Console.WriteLine($"通过 {_checks} 项加载器版本列表检查。");
    }

    /// <summary>分别限定官方或镜像域名实测，禁止回退掩盖单源失败；打印实际版本数量与首项，且不下载游戏文件。</summary>
    private static async Task LiveAsync()
    {
        foreach (bool mirror in new[] { false, true })
        {
            using var handler = new SourceOnlyHandler(mirror);
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var service = new LoaderAvailabilityService(client, TimeSpan.FromSeconds(30));
            foreach (var loader in Enum.GetValues<LoaderKind>())
            {
                var list = await service.FetchAsync(loader, "1.21.1", mirror ? SourceStrategy.MirrorFirst : SourceStrategy.OfficialFirst, default);
                Check(list.Count > 0 && list.All(x => !string.IsNullOrWhiteSpace(x.Detail)), "真实清单含版本及副标题");
                Console.WriteLine($"{(mirror ? "BMCLAPI" : "官方")} {loader}: {list.Count} 个版本，{list[0].Version} / {list[0].Detail}");
            }
            Check((await service.FetchAsync(LoaderKind.NeoForge, "1.20.1", mirror ? SourceStrategy.MirrorFirst : SourceStrategy.OfficialFirst, default)).Count > 0, "真实 NeoForge 历史清单");
            Check((await service.FetchAsync(LoaderKind.Fabric, "26.3", mirror ? SourceStrategy.MirrorFirst : SourceStrategy.OfficialFirst, default)).Count > 0, "真实 Fabric 通用映射清单");
        }
    }

    /// <summary>创建测试 HTTP 正文，不访问网络。</summary>
    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    /// <summary>断言具体清单结果，失败时报告场景。</summary>
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }

    /// <summary>验证异常类型，防止源失败被报告成没有版本。</summary>
    private static async Task Throws<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { _checks++; return; }
        throw new Exception(name);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>交由测试场景提供响应，同时保留真实取消令牌。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class SourceOnlyHandler(bool mirror) : DelegatingHandler(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        /// <summary>限制单次实网测试只能使用指定来源；其他域名返回 503 以暴露首选源失败。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            (request.RequestUri!.Host == "bmclapi2.bangbang93.com") == mirror ? base.SendAsync(request, cancellationToken) :
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
