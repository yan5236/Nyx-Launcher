using System.Net;
using System.Security.Cryptography;
using System.Text;
using NyxLauncher.Services;

internal static class ResourceDownloadChecks
{
    /// <summary>验证三类 ZIP 清单、隔离路径、数据包存档边界和共享任务；仅使用本地响应与临时版本，不访问网络。</summary>
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-resource-check-" + Guid.NewGuid());
        int checks = 0;
        try
        {
            string version = Path.Combine(root, "versions", "client");
            Directory.CreateDirectory(version);
            await File.WriteAllTextAsync(Path.Combine(version, "client.json"), """{"id":"client","clientVersion":"1.21.1","type":"release"}""");
            var target = InstalledVersionService.Scan(root).Single();
            byte[] payload = Encoding.UTF8.GetBytes("resource fixture");
            string hash = Convert.ToHexString(SHA1.HashData(payload));
            foreach (string category in new[] { "resourcepack", "shader", "datapack" })
            {
                string loader = category == "resourcepack" ? "minecraft" : category == "shader" ? "iris" : "datapack";
                string json = $$$"""[{"project_id":"Test1","id":"Version1","game_versions":["1.21.1"],"loaders":["{{{loader}}}"],"dependencies":[],"date_published":"2026-10-31T00:00:00Z","version_type":"release","files":[{"filename":"test.zip","url":"https://cdn.modrinth.com/data/Test1/versions/Version1/test.zip","size":{{{payload.Length}}},"primary":true,"hashes":{"sha1":"{{{hash}}}"}}]}]""";
                using var client = new HttpClient(new Handler(json));
                var file = (await new ModrinthCatalogService(client).FetchModFilesAsync("Test1", default, category: category)).Single();
                Check(file.IsResource && file.Loaders.Single() == loader, "ZIP 发布保留资源类型和平台加载器");
                Check((await new ModrinthCatalogService(client).FetchModFilesAsync("Test1", default)).Count == 0, "模组不混入 ZIP");
                string world = Path.Combine(version, "saves", "世界一");
                Directory.CreateDirectory(world);
                await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "fixture");
                Directory.CreateDirectory(Path.Combine(version, "saves", "not-world"));
                Check(ResourceInstallationService.Worlds(version).SequenceEqual(new[] { world }), "存档列表忽略普通目录");
                string directory = ResourceInstallationService.Resolve(root, target, file, "1.21.1", IsolationMode.All, world);
                string expected = category == "datapack" ? Path.Combine(world, "datapacks") : Path.Combine(version, category == "shader" ? "shaderpacks" : "resourcepacks");
                Check(directory == expected && !Directory.Exists(directory), "目标路径正确且解析不写入");
                Reject(() => ResourceInstallationService.Resolve(root, target, file, "1.20.1", IsolationMode.All, world));
                Reject(() => ResourceInstallationService.Resolve(root, target with { DirectoryPath = root }, file, "1.21.1", IsolationMode.All, world));
                if (category == "datapack")
                {
                    Reject(() => ResourceInstallationService.Resolve(root, target, file, "1.21.1", IsolationMode.Disabled, world));
                    Reject(() => ResourceInstallationService.Resolve(root, target, file, "1.21.1", IsolationMode.All));
                }
                else Check(ResourceInstallationService.Resolve(root, target, file, "1.21.1", IsolationMode.Disabled) == Path.Combine(root, category == "shader" ? "shaderpacks" : "resourcepacks"), "共享目录规则正确");
                Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(Path.Combine(directory, file.FileName), payload);
                var settings = new DownloadSettingsStore(Path.Combine(root, "settings.json"));
                settings.Save(settings.Current with { Isolation = IsolationMode.All });
                var manager = new DownloadTaskManager(settings, action => action());
                var task = manager.AddResource(file, root, target, "1.21.1", world);
                await task.Execution;
                Check(task.State == DownloadState.Completed && task.Destination == Path.Combine(expected, file.FileName), "资源任务复用完整同名文件并完成");
                var manual = manager.AddModToFolder(file, directory);
                await manual.Execution;
                Check(manual.State == DownloadState.Completed, "资源文件夹下载复用任务校验");
                if (category == "datapack")
                {
                    File.Delete(Path.Combine(world, "level.dat"));
                    Reject(() => ResourceInstallationService.Resolve(root, target, file, "1.21.1", IsolationMode.All, world));
                }
                await manager.ShutdownAsync();
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"Resource download checks passed: {checks}");

        // 断言仅检查外部可观察结果，失败时保留具体业务说明。
        void Check(bool value, string message)
        { if (!value) throw new Exception(message); checks++; }
        // 无效客户端、版本和存档必须拒绝，不能静默退回游戏根目录。
        void Reject(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or ArgumentException) { checks++; return; }
            throw new Exception("无效资源目标未被拒绝");
        }
    }

    /// <summary>显式实网验收：每类读取热门项目发布，下载其中最小 ZIP 到独立目录并验证 SHA-1；不修改用户客户端。</summary>
    public static async Task LiveAsync(string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var catalog = new ModrinthCatalogService();
        using var transport = new DownloadTransport(new DownloadSettings());
        foreach (string category in new[] { "resourcepack", "shader", "datapack" })
        {
            var page = await catalog.SearchAsync(category, "", "", "", 0, timeout.Token);
            var project = page.Projects[0];
            var files = await catalog.FetchModFilesAsync(project.Id, timeout.Token, category: category);
            var file = files.OrderBy(x => x.Size > 0 ? x.Size : long.MaxValue).First();
            string directory = Path.Combine(root, category);
            await ModInstallationService.InstallAsync(file, directory, transport, null, timeout.Token);
            if (!await DownloadTransport.ValidAsync(Path.Combine(directory, file.FileName), file.Sha1, file.Size, timeout.Token))
                throw new Exception("实网资源校验失败");
            Console.WriteLine($"LIVE {category}: {project.Title}, {files.Count} files, {file.FileName}, SHA-1 verified");
        }
    }

    private sealed class Handler(string json) : HttpMessageHandler
    {
        /// <summary>返回固定上游发布清单，用于验证真实解析而不依赖外部服务。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
