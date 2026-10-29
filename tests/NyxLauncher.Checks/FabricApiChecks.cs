using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NyxLauncher.Services;

internal static class FabricApiChecks
{
    private static int _checks;
    private static readonly byte[] Content = [1, 2, 3, 4, 5];

    /// <summary>构造明确属于官方项目的最小发布数据，所有测试只修改待验证字段。</summary>
    private static JsonObject Release(string id = "version", string game = "1.21.1", string loader = "fabric") => new()
    {
        ["id"] = id, ["project_id"] = FabricApiService.ProjectId, ["version_number"] = "0.1+" + game,
        ["game_versions"] = new JsonArray(game), ["loaders"] = new JsonArray(loader), ["version_type"] = "release",
        ["date_published"] = "2026-10-01T00:00:00Z",
        ["files"] = new JsonArray(new JsonObject
        {
            ["filename"] = "fabric-api-test.jar", ["primary"] = true, ["size"] = Content.Length,
            ["url"] = $"https://cdn.modrinth.com/data/{FabricApiService.ProjectId}/versions/version/fabric-api-test.jar",
            ["hashes"] = new JsonObject { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(Content)) }
        })
    };

    /// <summary>验证筛选、主文件选择、失败/取消、真实文件字节和冲突保护，不访问网络或用户游戏目录。</summary>
    public static async Task RunAsync()
    {
        var release = Release();
        byte[] list = JsonSerializer.SerializeToUtf8Bytes(new[] { release, Release("other", "1.21"), Release("forge", loader: "forge"), release });
        var parsed = FabricApiService.Parse("1.21.1", list);
        Check(parsed.Count == 1 && parsed[0].GameVersion == "1.21.1", "精确游戏及加载器筛选、去重");
        var api = parsed[0];
        var older = Release("older"); older["date_published"] = "2025-01-01T00:00:00Z";
        Check(FabricApiService.Parse("1.21.1", JsonSerializer.SerializeToUtf8Bytes(new[] { older, release }))[0].Id == "version", "按真实发布日期降序");
        Check(FabricApiService.Parse("missing", list).Count == 0 && FabricApiService.Parse("1.21.1", "[]"u8.ToArray()).Count == 0, "空结果保留无版本语义");
        var primary = Release();
        var source = primary["files"]![0]!.DeepClone(); source["primary"] = false; source["filename"] = "sources.jar";
        primary["files"]!.AsArray().Insert(0, source);
        Check(FabricApiService.Parse("1.21.1", JsonSerializer.SerializeToUtf8Bytes(new[] { primary }))[0].FileName == api.FileName, "下载主 JAR 而非首个源码文件");
        release["files"]![0]!["primary"] = false;
        Check(FabricApiService.Parse("1.21.1", JsonSerializer.SerializeToUtf8Bytes(new[] { release }))[0].FileName == api.FileName, "无主标记按接口契约取第一文件");
        var fabric = new LoaderVersion(LoaderKind.Fabric, "0.20.1", "0.20.1", "稳定版");
        FabricApiService.ValidateSelection("1.21.1", fabric, api);
        await Throws<ArgumentException>(() => { FabricApiService.ValidateSelection("1.21", fabric, api); return Task.CompletedTask; }, "阻止错游戏安装");
        await Throws<ArgumentException>(() => { FabricApiService.ValidateSelection("1.21.1", null, api); return Task.CompletedTask; }, "阻止无加载器安装");
        await Throws<ArgumentException>(() => { FabricApiService.ValidateSelection("1.21.1", fabric with { Loader = LoaderKind.Forge }, api); return Task.CompletedTask; }, "阻止 Forge 搭配 API");
        using (var client = new HttpClient(new Handler((request, _) =>
        {
            string query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Check(query.Contains("game_versions=[\"1.21.1\"]") && query.Contains("loaders=[\"fabric\"]") && request.Headers.UserAgent.Count > 0, "真实请求编码和 User-Agent");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(list) });
        }))) Check((await new FabricApiService(client).FetchAsync("1.21.1", default)).Count == 1, "请求结果经过再次筛选");
        foreach (var mutation in new Action<JsonObject>[]
        {
            x => x["project_id"] = "wrong", x => x["files"]![0]!["filename"] = "../outside.jar",
            x => x["files"]![0]!["hashes"]!["sha1"] = "bad", x => x["files"]![0]!["url"] = "http://cdn.modrinth.com/file",
            x => x["files"]![0]!["size"] = 0, x => x["files"]![0]!["file_type"] = "sources-jar",
            x => x["files"] = new JsonArray(), x => x["date_published"] = "invalid"
        })
        {
            var invalid = Release(); mutation(invalid);
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(new JsonArray(invalid).ToJsonString()) })));
            await Throws<HttpRequestException>(() => new FabricApiService(client).FetchAsync("1.21.1", default), "拒绝无效远程元数据");
        }
        using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)))))
            await Throws<HttpRequestException>(() => new FabricApiService(client).FetchAsync("1.21.1", default), "HTTP 失败不当作无版本");
        using (var client = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(); })))
        {
            await Throws<HttpRequestException>(() => new FabricApiService(client, TimeSpan.FromMilliseconds(20)).FetchAsync("1.21.1", default), "清单总超时");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Throws<OperationCanceledException>(() => new FabricApiService(client).FetchAsync("1.21.1", cancel.Token), "离开页面主动取消");
        }
        using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(new string(' ', 8 * 1024 * 1024 + 1)) }))))
            await Throws<HttpRequestException>(() => new FabricApiService(client).FetchAsync("1.21.1", default), "限制清单内存大小");

        string root = Path.Combine(Path.GetTempPath(), "nyx-api-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int requests = 0;
            using var transport = new DownloadTransport(new(), new HttpClient(new Handler((_, _) =>
            { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) }); })));
            string mods = Path.Combine(root, "mods");
            await FabricApiService.InstallAsync(api, mods, transport, default);
            Check(File.ReadAllBytes(Path.Combine(mods, api.FileName)).SequenceEqual(Content), "API 下载实际字节及哈希");
            await FabricApiService.InstallAsync(api, mods, transport, default);
            Check(requests == 1, "相同 API 经哈希验证复用");
            await Throws<IOException>(() => FabricApiService.InstallAsync(api with { FileName = "fabric-api-other.jar" }, mods, transport, default), "不同 API 不叠加");
            Check(Directory.GetFiles(mods).Length == 1, "冲突不删除已有 API");
            await File.WriteAllTextAsync(Path.Combine(mods, api.FileName), "keep-corrupt");
            await Throws<IOException>(() => FabricApiService.InstallAsync(api, mods, transport, default), "不覆盖已有同名文件");
            Check(File.ReadAllText(Path.Combine(mods, api.FileName)) == "keep-corrupt", "失败保留已有内容");
            string bad = Path.Combine(root, "bad");
            await Throws<IOException>(() => FabricApiService.InstallAsync(api with { Sha1 = new string('0', 40) }, bad, transport, default), "哈希失败拒绝发布");
            Check(!Directory.EnumerateFiles(bad).Any(), "哈希失败清理半成品");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Throws<OperationCanceledException>(() => FabricApiService.InstallAsync(api, Path.Combine(root, "cancel"), transport, cancellation.Token), "API 下载取消");
            var manager = new DownloadTaskManager(new DownloadSettingsStore(Path.Combine(root, "settings.json")), action => action());
            await Throws<ArgumentException>(() => { manager.AddGame(root, "bad-selection", "1.21.1", null, api); return Task.CompletedTask; }, "任务入口校验 API 前置条件");
            Check(manager.Tasks.Count == 0, "无效组合不创建任务");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"通过 {_checks} 项 Fabric API 检查。");
    }

    /// <summary>在已有独立验收目录真实安装 Fabric API，分别检查隔离与共享目录；真实启动隔离版本，确认日志加载 API。</summary>
    public static async Task LiveAsync(string root)
    {
        var api = (await new FabricApiService().FetchAsync("1.21.1", default))[0];
        var loader = (await new LoaderAvailabilityService().FetchAsync(LoaderKind.Fabric, "1.21.1", SourceStrategy.OfficialFirst, default))[0];
        Console.WriteLine($"Fabric API {api.Version} / {api.FileName} / SHA-1 {api.Sha1}");
        foreach (var mode in new[] { IsolationMode.All, IsolationMode.Disabled })
        {
            string name = "qa-FabricApi-" + mode;
            var settings = new DownloadSettings(FileSource: SourceStrategy.OfficialFirst, VersionSource: SourceStrategy.OfficialFirst, MaxConnections: 8, Isolation: mode);
            using var transport = new DownloadTransport(settings);
            var installer = new GameInstallationService(root, name, settings);
            if (!Directory.Exists(Path.Combine(root, "versions", name)))
                await installer.InstallAsync(name, "1.21.1", loader, transport, (_, _) => { }, default, api);
            var version = InstalledVersionService.Scan(root).Single(x => x.Name == name);
            string gameDir = VersionIsolation.Resolve(root, version, mode);
            string jar = Path.Combine(gameDir, "mods", api.FileName);
            Check(await DownloadTransport.ValidAsync(jar, api.Sha1, api.Size, default), mode + " 实际目录 API 长度及哈希");
            Check(!Directory.EnumerateFiles(Path.Combine(gameDir, "mods"), "*.part", SearchOption.TopDirectoryOnly).Any(), "无未完成文件");
            installer.Cleanup();
            Console.WriteLine(mode + " installed " + jar);
            if (mode != IsolationMode.All) continue;
            var session = new GameLaunchSession();
            var running = Task.Run(() => session.RunAsync(root, version, settings, new OfflineAccount("ApiTest")));
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                while (!running.IsCompleted && session.Status.Stage != LaunchStage.Running) await Task.Delay(250, timeout.Token);
                Check(session.Status.Stage == LaunchStage.Running, "Fabric API 实际游戏窗口：" + session.Status.Message);
                await Task.Delay(12000, timeout.Token);
                Check(!running.IsCompleted, "API 游戏持续运行");
            }
            finally { session.Stop(); await running.WaitAsync(TimeSpan.FromSeconds(20)); }
            Check(File.ReadAllText(Path.Combine(gameDir, "logs", "latest.log")).Contains("fabric-api " + api.Version), "游戏日志确认加载所选 API");
        }
        Console.WriteLine($"通过 {_checks} 项 Fabric API 实网检查。");
    }

    /// <summary>断言具体业务结果；失败立即终止并输出检查名称。</summary>
    private static void Check(bool value, string name) { if (!value) throw new Exception("失败：" + name); _checks++; }

    /// <summary>验证异步失败类型；错误类型或意外成功不能计为通过。</summary>
    private static async Task Throws<T>(Func<Task> action, string name) where T : Exception
    { try { await action(); } catch (T) { _checks++; return; } throw new Exception("未抛出异常：" + name); }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>只替换网络响应，解析、校验、文件发布与取消均执行真实业务代码。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
