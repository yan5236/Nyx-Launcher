using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NyxLauncher.Services;

internal static class ModpackExportChecks
{
    /// <summary>验证三格式往返、资源策略、选择与安全边界；使用临时游戏目录和确定性平台响应，不访问真实账户或网络。</summary>
    public static async Task RunAsync()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nyx-export-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(workspace, "game");
        Directory.CreateDirectory(root);
        try
        {
            foreach (string folder in new[] { "mods", "config", "saves/world", "screenshots", "schematics", "assets", ".nyx-launch" })
                Directory.CreateDirectory(Path.Combine(root, folder));
            foreach (var (path, content) in new[] { ("mods/known.jar", "known"), ("mods/unknown.jar", "unknown"),
                ("config/test.txt", "settings"), ("saves/world/level.dat", "world"), ("options.txt", "options"),
                ("accounts.json", "secret"), ("assets/secret", "library"), (".nyx-launch/token", "secret") })
                File.WriteAllText(Path.Combine(root, path), content);
            var tree = ModpackExportFiles.Scan(root, default);
            Check(!tree.Any(x => x.RelativePath is "accounts.json" or "assets" or ".nyx-launch"), "排除凭据、运行库与内部状态");
            Check(tree.Single(x => x.RelativePath == "saves").Selected == false, "存档默认不选");
            var mods = tree.Single(x => x.RelativePath == "mods");
            mods.Children[0].Select(false);
            Check(mods.Selected is null, "父项半选");
            mods.Select(true);
            Check(mods.Children.All(x => x.Selected == true), "父项选择全部子项");
            var version = new InstalledVersion("Test", root, "1.21.1", "正式版", "Fabric: 0.16.0", null);
            byte[] known = Encoding.UTF8.GetBytes("known");
            string sha1 = Convert.ToHexStringLower(SHA1.HashData(known));
            string sha512 = Convert.ToHexStringLower(SHA512.HashData(known));
            int calls = 0;
            using var client = new HttpClient(new Handler(async request =>
            {
                calls++;
                Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/version_files"), "精确哈希批量接口");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(body.RootElement.GetProperty("algorithm").GetString() == "sha1", "按 SHA-1 请求");
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    [sha1] = new { files = new[] { new { hashes = new { sha1, sha512 }, size = known.Length,
                        url = "https://cdn.modrinth.com/data/project/versions/version/known.jar" } } }
                })) };
            }));
            var catalog = new ModrinthCatalogService(client);
            var options = new ModpackExportOptions("测试整合包", "作者", "1.0", Path.Combine(workspace, "default.mrpack"), ModpackExportFormat.Modrinth, false, false);
            Check(await ModpackExportService.ExportAsync(root, version, options, tree, catalog, null, default) == 0, "默认不跳过未知模组");
            var manifest = ModpackManifest.Read(options.Destination);
            Check(manifest.Game == "1.21.1" && manifest.Loader?.Version == "0.16.0", "游戏和加载器往返");
            Check(manifest.Files.Single().Path == "mods/known.jar" && !manifest.Entries.ContainsKey("mods/known.jar") && manifest.Entries.ContainsKey("mods/unknown.jar"), "已知模组下载，未知模组内置");
            Check(manifest.Entries.ContainsKey("config/test.txt") && !manifest.Entries.ContainsKey("saves/world/level.dat"), "严格遵守文件选择");
            options = options with { Destination = Path.Combine(workspace, "only.mrpack"), OnlyModrinth = true };
            Check(await ModpackExportService.ExportAsync(root, version, options, tree, catalog, null, default) == 1, "仅平台模式报告跳过数量");
            Check(!ModpackManifest.Read(options.Destination).Entries.ContainsKey("mods/unknown.jar"), "仅平台模式不内置未知模组");
            int before = calls;
            foreach (var format in Enum.GetValues<ModpackExportFormat>())
            {
                options = options with { Destination = Path.Combine(workspace, format + (format == ModpackExportFormat.Modrinth ? ".mrpack" : ".zip")),
                    Format = format, BundleResources = true, OnlyModrinth = false };
                await ModpackExportService.ExportAsync(root, version, options, tree, catalog, null, default);
                manifest = ModpackManifest.Read(options.Destination);
                Check(manifest.Name == "测试整合包" && manifest.Loader?.Loader == LoaderKind.Fabric &&
                    manifest.Entries.ContainsKey("mods/known.jar") && manifest.Entries.ContainsKey("mods/unknown.jar"), "三格式内置资源及元数据往返");
                using var zip = ZipFile.OpenRead(options.Destination);
                using var reader = new StreamReader(zip.GetEntry(manifest.Entries["mods/known.jar"])!.Open());
                Check(reader.ReadToEnd() == "known", "原文件字节保留");
            }
            Check(calls == before, "内置模式不联网");
            byte[] previous = File.ReadAllBytes(options.Destination);
            await Reject(() => ModpackExportService.ExportAsync(root, version, options, tree, catalog, null, default));
            Check(previous.SequenceEqual(File.ReadAllBytes(options.Destination)), "同名目标不覆盖");
            options = options with { Destination = Path.Combine(workspace, "bad.zip") };
            await Reject(() => ModpackExportService.ExportAsync(root, version, options, [new("../secret", false, true)], catalog, null, default));
            Check(!File.Exists(options.Destination), "路径穿越不发布");
            options = options with { Destination = Path.Combine(workspace, "cancel.zip") };
            var cancel = new CancellationTokenSource();
            var progress = new ImmediateProgress(_ => cancel.Cancel());
            await Reject(() => ModpackExportService.ExportAsync(root, version, options, tree, catalog, progress, cancel.Token));
            Check(!File.Exists(options.Destination) && !Directory.EnumerateFiles(workspace, "*.tmp").Any(), "取消清理暂存且不发布");
            using var failed = new HttpClient(new Handler(_ => throw new HttpRequestException("offline")));
            options = options with { Destination = Path.Combine(workspace, "offline.mrpack"), Format = ModpackExportFormat.Modrinth, BundleResources = false };
            await Reject(() => ModpackExportService.ExportAsync(root, version, options, tree, new(failed), null, default));
            Check(!File.Exists(options.Destination), "网络失败不能降级内置");
            using var changed = new HttpClient(new Handler(_ =>
            {
                File.WriteAllText(Path.Combine(root, "mods/known.jar"), "changed");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }));
            options = options with { Destination = Path.Combine(workspace, "changed.mrpack") };
            await Reject(() => ModpackExportService.ExportAsync(root, version, options, tree, new(changed), null, default));
            Check(!File.Exists(options.Destination) && !Directory.EnumerateFiles(workspace, "*.tmp").Any(), "识别后文件变化时拒绝发布并清理");
            foreach (string loader in new[] { "", "Forge: 52.0.1", "NeoForge: 21.1.1" })
            {
                options = options with { Destination = Path.Combine(workspace, "loader-" + Guid.NewGuid() + ".mrpack"), BundleResources = true };
                await ModpackExportService.ExportAsync(root, version with { Loader = loader }, options, tree, catalog, null, default);
                var restored = ModpackManifest.Read(options.Destination);
                Check(loader.Length == 0 ? restored.Loader is null : restored.Loader?.Version == loader.Split(':')[1].Trim(), "原版及 Forge/NeoForge 版本往返");
            }
            Console.WriteLine("Modpack export checks passed.");
        }
        finally { Directory.Delete(workspace, true); }
    }

    /// <summary>断言具体业务结果，失败立即终止检查。</summary>
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    /// <summary>确认被测试操作明确失败，正常完成时抛出检查错误。</summary>
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or OperationCanceledException or HttpRequestException) { return; }
        throw new Exception("应拒绝此操作。");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        /// <summary>将平台请求交给确定性夹具，不建立网络连接。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handler(request);
    }

    private sealed class ImmediateProgress(Action<string> report) : IProgress<string>
    {
        /// <summary>同步触发取消，验证写 ZIP 阶段的清理行为。</summary>
        public void Report(string value) => report(value);
    }
}
