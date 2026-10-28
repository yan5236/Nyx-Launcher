using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using NyxLauncher.Services;

internal static class LoaderChecks
{
    private static int _checks;

    /// <summary>验证精确匹配、历史坐标、新版和快照编号、格式失败、超时及取消；可选验证全部官方端点或仅 Fabric 26.3。</summary>
    public static async Task RunAsync(bool live, bool liveFabric = false)
    {
        byte[] forge = Maven("net.minecraftforge", "forge", "1.21.1-52.0.0", "1.20.1-47.1.0");
        Check(LoaderAvailabilityService.Parse(LoaderKind.Forge, "1.21.1", forge), "Forge 已发布版本可添加");
        Check(!LoaderAvailabilityService.Parse(LoaderKind.Forge, "1.21", forge), "Forge 不混淆 1.21 与 1.21.1");
        byte[] forgeSnapshot = Maven("net.minecraftforge", "forge", "26.1-snapshot-1-64.0.0");
        Check(!LoaderAvailabilityService.Parse(LoaderKind.Forge, "26.1", forgeSnapshot) &&
            LoaderAvailabilityService.Parse(LoaderKind.Forge, "26.1-snapshot-1", forgeSnapshot),
            "Forge 快照构建不能误报为正式版可用");
        byte[] neo = Maven("net.neoforged", "neoforge", "21.0.1-beta", "21.1.1", "26.1.0.5-beta",
            "26.1.1.1", "26.2.0.0-alpha.1+snapshot-1");
        foreach (string version in new[] { "1.21", "1.21.1", "26.1", "26.1.1", "26.2-snapshot-1" })
            Check(LoaderAvailabilityService.Parse(LoaderKind.NeoForge, version, neo), "NeoForge 匹配 " + version);
        foreach (string version in new[] { "1.21.10", "26.2", "26.2-snapshot-2", "24w14potato", "b1.7.3" })
            Check(!LoaderAvailabilityService.Parse(LoaderKind.NeoForge, version, neo), "NeoForge 不误报 " + version);
        byte[] legacy = Maven("net.neoforged", "forge", "1.20.1-47.1.106");
        Check(LoaderAvailabilityService.Parse(LoaderKind.NeoForge, "1.20.1", legacy), "NeoForge 1.20.1 旧坐标");
        const string fabric = """[{"loader":{"version":"0.16.0","stable":true},"intermediary":{"version":"1.21.1"}}]""";
        Check(LoaderAvailabilityService.Parse(LoaderKind.Fabric, "1.21.1", Encoding.UTF8.GetBytes(fabric)), "Fabric 返回可用构建");
        Check(!LoaderAvailabilityService.Parse(LoaderKind.Fabric, "1.21", Encoding.UTF8.GetBytes(fabric)), "Fabric 校验游戏 ID");
        const string unobfuscated = """[{"loader":{"version":"0.20.1","stable":true},"intermediary":{"maven":"net.fabricmc:intermediary:0.0.0","version":"0.0.0"}}]""";
        Check(LoaderAvailabilityService.Parse(LoaderKind.Fabric, "26.3", Encoding.UTF8.GetBytes(unobfuscated)),
            "Fabric 26.3 的 0.0.0 映射不是游戏版本，不应误报不支持");
        using (var client = new HttpClient(new Handler((request, token) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                request.RequestUri!.AbsoluteUri == LoaderAvailabilityService.FabricGamesUrl
                    ? "[{\"version\":\"26.3\"}]" : unobfuscated) }))))
        {
            var service = new LoaderAvailabilityService(client);
            Check(await service.CheckAsync(LoaderKind.Fabric, "26.3", default), "Fabric 26.3 支持清单与加载器探测完整流程");
            Check(!await service.CheckAsync(LoaderKind.Fabric, "b1.7.3", default), "通用映射不绕过 Fabric 游戏支持清单");
        }
        Check(!LoaderAvailabilityService.Parse(LoaderKind.Fabric, "b1.7.3", "[]"u8.ToArray()), "Fabric 空数组代表无构建");
        foreach (string invalid in new[] { "{}", "[{}]", "<html>", "[{\"loader\":null}]" })
            await Throws<JsonException>(() => Task.FromResult(LoaderAvailabilityService.Parse(LoaderKind.Fabric,
                "1.21.1", Encoding.UTF8.GetBytes(invalid))), "Fabric 拒绝坏响应");
        foreach (byte[] invalid in new[] { "<html/>"u8.ToArray(), neo,
            "<!DOCTYPE metadata [<!ENTITY x SYSTEM 'file:///example'>]><metadata>&x;</metadata>"u8.ToArray() })
            await Throws<XmlException>(() => Task.FromResult(LoaderAvailabilityService.Parse(LoaderKind.Forge,
                "1.21.1", invalid)), "拒绝错误 Maven 坐标、错误页及 DTD");

        var urls = new List<string>();
        using (var client = new HttpClient(new Handler((request, token) =>
        {
            urls.Add(request.RequestUri!.AbsoluteUri);
            byte[] data = request.RequestUri.AbsoluteUri == LoaderAvailabilityService.LegacyNeoForgeUrl ? legacy
                : request.RequestUri.AbsoluteUri == LoaderAvailabilityService.FabricGamesUrl
                    ? "[{\"version\":\"1.14 Pre-Release 1\"}]"u8.ToArray()
                : request.RequestUri.Host == "meta.fabricmc.net" ? "[]"u8.ToArray() : forge;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        })))
        {
            var service = new LoaderAvailabilityService(client);
            Check(await service.CheckAsync(LoaderKind.NeoForge, "1.20.1", default), "网络探测使用旧 NeoForge 坐标");
            await service.CheckAsync(LoaderKind.Fabric, "1.14 Pre-Release 1", default);
            Check(urls[2].EndsWith("1.14%20Pre-Release%201", StringComparison.Ordinal), "游戏 ID 编码为单一路径段");
            int before = urls.Count;
            Check(!await service.CheckAsync(LoaderKind.Fabric, "b1.7.3", default) && urls.Count == before + 1,
                "Fabric 不支持的游戏仅查询支持清单，不触发 loader 的 400 错误");
        }
        foreach (string body in new[] { "[]", "{}", "[{}]" })
        {
            using var client = new HttpClient(new Handler((request, token) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
            await Throws<HttpRequestException>(() => new LoaderAvailabilityService(client).CheckAsync(LoaderKind.Fabric,
                "1.21.1", default), "Fabric 支持清单损坏必须报错");
        }
        foreach (string body in new[] { "<html>", new string('x', 8 * 1024 * 1024 + 1) })
        {
            using var client = new HttpClient(new Handler((request, token) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
            await Throws<HttpRequestException>(() => new LoaderAvailabilityService(client).CheckAsync(LoaderKind.Forge,
                "1.21.1", default), "坏正文或超限必须失败，不能报告无版本");
        }
        using (var client = new HttpClient(new Handler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
            await Throws<HttpRequestException>(() => new LoaderAvailabilityService(client).CheckAsync(LoaderKind.Fabric,
                "1.21.1", default), "HTTP 错误不是不支持");
        using (var client = new HttpClient(new Handler(async (request, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })))
        {
            await Throws<HttpRequestException>(() => new LoaderAvailabilityService(client, TimeSpan.FromMilliseconds(20))
                .CheckAsync(LoaderKind.Forge, "1.21.1", default), "探测超时可重试");
            using var cancel = new CancellationTokenSource(20);
            await Throws<OperationCanceledException>(() => new LoaderAvailabilityService(client)
                .CheckAsync(LoaderKind.NeoForge, "1.21.1", cancel.Token), "离开页面取消请求");
        }
        if (live || liveFabric)
        {
            Check(await new LoaderAvailabilityService(timeout: TimeSpan.FromSeconds(30))
                .CheckAsync(LoaderKind.Fabric, "26.3", default), "Fabric 26.3 官方实时检测可添加");
            Console.WriteLine("Fabric 官方端点：26.3 可以添加。");
        }
        if (live)
        {
            var service = new LoaderAvailabilityService(timeout: TimeSpan.FromSeconds(30));
            foreach (var loader in Enum.GetValues<LoaderKind>())
            {
                Check(await service.CheckAsync(loader, "1.21.1", default), loader + " 真实支持 1.21.1");
                Check(!await service.CheckAsync(loader, "b1.7.3", default), loader + " 真实不支持 b1.7.3");
                Console.WriteLine($"{loader} 官方端点：1.21.1 可以添加；b1.7.3 暂无可用版本。");
            }
            Check(await service.CheckAsync(LoaderKind.NeoForge, "1.20.1", default), "NeoForge 真实旧坐标");
            Check(await service.CheckAsync(LoaderKind.NeoForge, "26.1", default), "NeoForge 真实新编号");
            Check(await service.CheckAsync(LoaderKind.NeoForge, "26.1-snapshot-1", default), "NeoForge 真实快照编号");
        }
        Console.WriteLine($"通过 {_checks} 项加载器检查。");
    }

    /// <summary>生成具有官方坐标和指定构建号的最小 Maven 清单，供匹配边界测试。</summary>
    private static byte[] Maven(string group, string artifact, params string[] versions) => Encoding.UTF8.GetBytes(
        $"<metadata><groupId>{group}</groupId><artifactId>{artifact}</artifactId><versioning><versions>" +
        string.Concat(versions.Select(x => $"<version>{x}</version>")) + "</versions></versioning></metadata>");

    /// <summary>断言具体业务结果；失败时中止并报告场景名称。</summary>
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }

    /// <summary>要求操作抛出指定异常，防止失败被误判为正常不支持。</summary>
    private static async Task Throws<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { _checks++; return; }
        throw new Exception(name);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>向测试场景传递 HTTP 请求和取消令牌，不访问网络。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
