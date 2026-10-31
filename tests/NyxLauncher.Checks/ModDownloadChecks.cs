using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NyxLauncher.Services;

internal static class ModDownloadChecks
{
    private static int _checks;
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("verified mod jar fixture");
    private static readonly string Hash = Convert.ToHexString(SHA1.HashData(Payload));

    /// <summary>构造包含完整上游校验及兼容范围的文件；测试传输只替换 HTTP 响应，不替换安装或校验逻辑。</summary>
    private static ModrinthModFile FileSample() => new("TestMod1", "Version1", "example.jar", DateTimeOffset.UtcNow,
        "release", ["1.21.1"], ["fabric"], new Uri("https://cdn.modrinth.com/data/TestMod1/versions/Version1/example.jar"), Hash, Payload.Length);

    /// <summary>构造 API 的真实字段结构及非运行附件，验证不遗漏多文件发布且不将源码作为可安装模组。</summary>
    private static JsonObject VersionSample() => new()
    {
        ["project_id"] = "TestMod1", ["id"] = "Version1", ["date_published"] = "2026-10-29T00:00:00Z",
        ["version_type"] = "release", ["game_versions"] = new JsonArray("1.21.1", "1.20.1"),
        ["loaders"] = new JsonArray("fabric", "quilt"),
        ["files"] = new JsonArray(
            new JsonObject { ["filename"] = "example.jar", ["url"] = FileSample().Url.AbsoluteUri,
                ["primary"] = true, ["size"] = Payload.Length, ["hashes"] = new JsonObject { ["sha1"] = Hash } },
            new JsonObject { ["filename"] = "example-sources.jar", ["primary"] = false, ["file_type"] = "sources-jar" })
    };

    /// <summary>验证真实清单解析、官方双斜杠旧链接及未知大小，覆盖坏元数据、取消、隔离路径、兼容校验、原子下载、同名保护和共享任务接入。</summary>
    public static async Task RunAsync()
    {
        var sample = VersionSample();
        var extra = sample["files"]![0]!.DeepClone(); extra["filename"] = "example-extra.jar"; extra["primary"] = false;
        sample["files"]!.AsArray().Add(extra);
        var old = VersionSample(); old["id"] = "Version2"; old["date_published"] = "2025-01-01T00:00:00Z"; old["version_type"] = "beta";
        using (var client = JsonClient(new JsonArray(old, sample).ToJsonString(), request =>
            Check(request.RequestUri!.AbsolutePath == "/v2/project/TestMod1/version" &&
                request.RequestUri.Query == "?include_changelog=false" && request.Headers.UserAgent.Count > 0,
                "使用完整发布端点并省略全部更新日志")))
        {
            var files = await new ModrinthCatalogService(client).FetchModFilesAsync("TestMod1", default);
            Check(files.Count == 3 && files[0].FileName == "example.jar" && files[1].FileName == "example-extra.jar" &&
                files[2].ChannelLabel == "测试版" && files[2].Badge == "B", "全部运行文件、主文件优先及发布顺序和中文渠道");
            Check(files[0].GameVersions.SequenceEqual(new[] { "1.21.1", "1.20.1" }) && files[0].Loaders.Contains("quilt") &&
                files[0].Detail == files[0].Published.ToLocalTime().ToString("yyyy-MM-dd") + " · 正式版" && files[0].Badge == "R", "保留精确兼容范围及日期和 R 图标");
        }
        var legacy = VersionSample();
        legacy["id"] = "Legacy1";
        legacy["files"]![0]!["url"] = "https://cdn.modrinth.com//data/TestMod1/versions/Legacy1/example.jar";
        legacy["files"]![0]!["size"] = 0;
        using (var client = JsonClient(new JsonArray(VersionSample(), legacy).ToJsonString()))
        {
            var files = await new ModrinthCatalogService(client).FetchModFilesAsync("TestMod1", default);
            Check(files.Count == 2 && files.Any(x => x.VersionId == "Legacy1" && x.Url.AbsolutePath.StartsWith("//data/")),
                "官方双斜杠旧链接及未知大小不会阻断整个项目，原始下载地址保持不变");
        }
        foreach (string body in new[] { "{}", "<html>", "[{}]", Mutated("project_id", "OtherMod"), Mutated("version_type", "unknown"),
            MutatedFile("filename", "../outside.jar"), MutatedFile("filename", null),
            MutatedFile("url", "https://example.com/example.jar"),
            MutatedFile("url", "https://example.com//data/TestMod1/versions/Legacy1/example.jar"),
            MutatedFile("url", "https://cdn.modrinth.com//data/OtherMod/versions/Legacy1/example.jar"),
            MutatedFile("url", "https://cdn.modrinth.com//data/TestMod1-other/versions/Legacy1/example.jar"),
            MutatedFile("size", -1), MutatedFile("hashes", new JsonObject()) })
        {
            using var client = JsonClient(body);
            await Throws<HttpRequestException>(() => new ModrinthCatalogService(client).FetchModFilesAsync("TestMod1", default));
        }
        using (var client = JsonClient("[]"))
        {
            var service = new ModrinthCatalogService(client);
            Check((await service.FetchModFilesAsync("TestMod1", default)).Count == 0, "没有发布是正常空结果");
            await Throws<ArgumentException>(() => service.FetchModFilesAsync("../escape", default));
        }
        using (var client = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(); })))
        {
            var service = new ModrinthCatalogService(client, TimeSpan.FromMilliseconds(20));
            await Throws<HttpRequestException>(() => service.FetchModFilesAsync("TestMod1", default));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Throws<OperationCanceledException>(() => service.FetchModFilesAsync("TestMod1", cancellation.Token));
        }
        string root = Path.Combine(Path.GetTempPath(), "nyx-mod-check-" + Guid.NewGuid());
        try
        {
            var target = CreateTarget(root);
            var file = FileSample() with { Url = new Uri("https://cdn.modrinth.com//data/TestMod1/versions/Version1/example.jar"), Size = 0 };
            string isolated = ModInstallationService.ResolveMods(root, target, file, "1.21.1", "fabric", IsolationMode.All);
            Check(isolated == Path.Combine(target.DirectoryPath, "mods") && !Directory.Exists(isolated), "隔离路径计算不写文件");
            Check(ModInstallationService.ResolveMods(root, target, file, "1.21.1", "fabric", IsolationMode.Disabled) == Path.Combine(root, "mods"),
                "关闭隔离时使用实际共享 mods");
            Check(!ModInstallationService.IsCompatible(target, "1.20.1", "fabric") &&
                !ModInstallationService.IsCompatible(target, "1.21.1", "forge") &&
                !ModInstallationService.IsCompatible(target with { Error = "bad" }, "1.21.1", "fabric"), "游戏版本、加载器及坏清单均严格过滤");
            await Throws<ArgumentException>(() => Task.FromResult(ModInstallationService.ResolveMods(root, target, file, "1.21.1", "forge", IsolationMode.All)));
            await Throws<IOException>(() => Task.FromResult(ModInstallationService.ResolveMods(root, target with { DirectoryPath = root }, file, "1.21.1", "fabric", IsolationMode.All)));
            int requests = 0;
            Uri? requestedUrl = null;
            using var client = new HttpClient(new Handler((request, _) =>
            {
                requests++;
                requestedUrl = request.RequestUri;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
            }));
            using var transport = new DownloadTransport(new(MaxConnections: 1), client);
            await ModInstallationService.InstallAsync(file, isolated, transport, null, default);
            string destination = Path.Combine(isolated, file.FileName);
            Check(await DownloadTransport.ValidAsync(destination, Hash, Payload.Length, default) && requests == 1 && requestedUrl == file.Url,
                "双斜杠旧链接原样交给统一传输，并验证实际字节和哈希");
            await ModInstallationService.InstallAsync(file, isolated, transport, null, default);
            Check(requests == 1, "完整同名文件不重复下载");
            await File.WriteAllTextAsync(destination, "existing user file");
            await Throws<IOException>(() => ModInstallationService.InstallAsync(file, isolated, transport, null, default));
            Check(await File.ReadAllTextAsync(destination) == "existing user file" && requests == 1, "冲突不覆盖也不发请求");
            File.Delete(destination);
            await Throws<IOException>(() => ModInstallationService.InstallAsync(file with { Sha1 = new string('0', 40) }, isolated, transport, null, default));
            Check(!File.Exists(destination) && !Directory.EnumerateFiles(isolated).Any(), "哈希错误不发布且清理所有临时文件");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Throws<OperationCanceledException>(() => ModInstallationService.InstallAsync(file, isolated, transport, null, cancel.Token));
            Check(!Directory.EnumerateFiles(isolated).Any(), "取消不遗留目标或临时文件");
            await File.WriteAllBytesAsync(destination, Payload);
            var settings = new DownloadSettingsStore(Path.Combine(root, "settings.json"));
            var manager = new DownloadTaskManager(settings, action => action());
            var task = manager.AddMod(file, root, target, "1.21.1", "fabric");
            await task.Execution;
            Check(manager.Tasks.Single() == task && task.State == DownloadState.Completed && task.Destination == destination,
                "模组任务确实进入共享下载管理器并完成");
            await manager.ShutdownAsync();
            File.Delete(Path.Combine(target.DirectoryPath, target.Name + ".json"));
            await Throws<IOException>(() => Task.FromResult(ModInstallationService.ResolveMods(root, target, file, "1.21.1", "fabric", IsolationMode.All)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"通过 {_checks} 项模组下载检查。");
    }

    /// <summary>在独立验收目录实际下载一个 Sodium 文件，通过共享任务管理器分别写入隔离和共享 mods 并校验，不启动游戏。</summary>
    public static async Task LiveAsync(string root)
    {
        var service = new ModrinthCatalogService();
        var files = await service.FetchModFilesAsync("AANobbMI", default);
        var file = files.First(x => x.GameVersions.Contains("1.21.1") && x.Loaders.Contains("fabric"));
        var target = CreateTarget(root);
        var settings = new DownloadSettingsStore(Path.Combine(root, "settings.json"));
        foreach (var mode in new[] { IsolationMode.All, IsolationMode.Disabled })
        {
            Check(settings.Save(settings.Current with { Isolation = mode }) is null, "保存验收隔离模式");
            var manager = new DownloadTaskManager(settings, action => action());
            try
            {
                var task = manager.AddMod(file, root, target, "1.21.1", "fabric");
                await task.Execution;
                Check(task.State == DownloadState.Completed, "实网任务完成：" + task.Detail);
                Check(await DownloadTransport.ValidAsync(task.Destination, file.Sha1, file.Size, default), "实网文件长度与 SHA-1 正确");
                Console.WriteLine($"{mode}: {task.Destination} · {file.Size} 字节 · SHA-1 {file.Sha1}");
            }
            finally { await manager.ShutdownAsync(); }
        }
        Console.WriteLine($"通过 {_checks} 项模组实网检查；本地版本仅为扫描夹具，未启动游戏。");
    }

    /// <summary>创建可被真实版本扫描器识别的最小清单，仅用于安装路径与兼容验收，不代表已安装可运行游戏。</summary>
    private static InstalledVersion CreateTarget(string root)
    {
        string directory = Path.Combine(root, "versions", "qa-fabric-mod");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "qa-fabric-mod.json");
        if (!File.Exists(path)) File.WriteAllText(path,
            """{"id":"qa-fabric-mod","clientVersion":"1.21.1","type":"release","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.0"}]}""");
        return InstalledVersionService.Scan(root).Single(x => x.Name == "qa-fabric-mod");
    }

    /// <summary>改变发布顶层字段以验证接口边界，其他字段仍保持有效。</summary>
    private static string Mutated(string key, JsonNode? value)
    { var version = VersionSample(); version[key] = value; return new JsonArray(version).ToJsonString(); }

    /// <summary>只破坏一个运行文件字段，确保解析拒绝不安全元数据而非忽略错误。</summary>
    private static string MutatedFile(string key, JsonNode? value)
    { var version = VersionSample(); version["files"]![0]![key] = value; return new JsonArray(version).ToJsonString(); }

    /// <summary>注入 JSON 响应并允许检查真实请求地址和客户端标识。</summary>
    private static HttpClient JsonClient(string json, Action<HttpRequestMessage>? inspect = null) =>
        new(new Handler((request, _) => { inspect?.Invoke(request); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }); }));

    /// <summary>断言具体业务结果，失败时输出对应名称。</summary>
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; }

    /// <summary>要求边界条件以指定异常拒绝，避免静默成功也被计为通过。</summary>
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { _checks++; return; } throw new Exception("未抛出 " + typeof(T).Name); }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>仅替代 HTTP 发送，保留服务的请求构造、取消、解析和安装行为。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
