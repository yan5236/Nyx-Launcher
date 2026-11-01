using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NyxLauncher.Services;

internal static class ModManagementChecks
{
    /// <summary>验证实际目录、启停、轻量识别、更新筛选和替换异常边界；仅使用临时文件与确定性 HTTP 响应。</summary>
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-mod-management-" + Guid.NewGuid());
        string versionPath = Path.Combine(root, "versions", "A");
        Directory.CreateDirectory(versionPath);
        try
        {
            File.WriteAllText(Path.Combine(versionPath, "A.json"), """{"id":"A","clientVersion":"1.21.1","type":"release","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.0"}]}""");
            var version = InstalledVersionService.Scan(root).Single();
            string directory = ModManagementService.Resolve(root, version, IsolationMode.All);
            Check(directory == Path.Combine(versionPath, "mods"), "隔离目录");
            Check(ModManagementService.Resolve(root, version, IsolationMode.Disabled) == Path.Combine(root, "mods"), "共享目录");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "old.jar");
            File.WriteAllText(path, "old");
            File.WriteAllText(Path.Combine(directory, "ignored.tmp"), "ignored");
            var mod = (await ModManagementService.ScanAsync(directory, default)).Single();
            Check(mod.FileName == "old.jar" && mod.UpdateVisibility == "Collapsed", "本地文件和默认隐藏更新");
            mod = ModManagementService.Toggle(directory, mod);
            Check(mod.Disabled && File.Exists(path + ".disabled") && !File.Exists(path), "禁用保留内容");
            File.WriteAllText(path, "conflict");
            await RejectAsync(() => Task.FromResult(ModManagementService.Toggle(directory, mod)));
            Check(File.ReadAllText(path) == "conflict", "启用不覆盖");
            File.Delete(path);
            await RejectAsync(() => Task.Run(() => ModManagementService.Validate(root, mod)));

            int calls = 0;
            using var api = new HttpClient(new Handler(async (request, _) =>
            {
                calls++;
                if (request.RequestUri!.AbsolutePath.EndsWith("/version_files"))
                {
                    Check(request.Method == HttpMethod.Post, "批量哈希使用 POST");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    Check(body.RootElement.GetProperty("algorithm").GetString() == "sha1" &&
                        body.RootElement.GetProperty("hashes")[0].GetString() == mod.Sha1, "哈希识别契约");
                    return Json(JsonSerializer.Serialize(new Dictionary<string, object> { [mod.Sha1] = new
                        { project_id = "project", id = "old", date_published = "2025-01-01T00:00:00Z" } }));
                }
                Check(request.RequestUri.AbsolutePath.EndsWith("/projects"), "不加载其他详情接口");
                return Json("""[{"id":"project","title":"模组名称","description":"简介","icon_url":"https://evil.example/icon.png"}]""");
            }));
            var catalog = new ModrinthCatalogService(api);
            var unknown = new InstalledMod(Path.Combine(directory, "unknown.jar"), new string('0', 40));
            var identified = await catalog.IdentifyModsAsync([mod, unknown], default);
            mod = identified[0];
            Check(calls == 2 && mod.Title == "模组名称" && mod.Description == "简介" && mod.Project!.IconUrl == "", "摘要字段和图标域名");
            Check(identified[1].Project is null && identified[1].Title == "unknown.jar", "未知哈希保留文件名");

            byte[] content = Encoding.UTF8.GetBytes("new release");
            var update = new ModrinthModFile("project", "new", "new.jar", DateTimeOffset.Parse("2026-01-01Z"),
                "release", ["1.21.1"], ["fabric"], new Uri("https://cdn.modrinth.com/data/project/versions/new/new.jar"),
                Convert.ToHexStringLower(SHA1.HashData(content)), content.Length) { VersionNumber = "2.0" };
            Check(ModManagementService.SelectUpdate(mod, version, [update]) == update, "选择兼容新版");
            foreach (var rejected in new[] { update with { Channel = "beta" }, update with { Loaders = ["forge"] },
                update with { GameVersions = ["1.20"] }, update with { VersionId = "old" },
                update with { Published = mod.Published }, update with { Sha1 = mod.Sha1 } })
                Check(ModManagementService.SelectUpdate(mod, version, [rejected]) is null, "排除不适用发布");
            Check((mod with { Update = update }).UpdateLabel == "更新至 2.0" && (mod with { Update = update }).UpdateVisibility == "Visible", "更新版本提示");

            using (var updatesApi = new HttpClient(new Handler(async (request, _) =>
            {
                Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/version_files/update"), "更新使用批量接口而非历史列表");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(body.RootElement.GetProperty("loaders")[0].GetString() == "fabric" &&
                    body.RootElement.GetProperty("game_versions")[0].GetString() == "1.21.1" &&
                    body.RootElement.GetProperty("version_types")[0].GetString() == "release", "远程更新兼容筛选");
                return Json(JsonSerializer.Serialize(new Dictionary<string, object> { [mod.Sha1] = new
                {
                    project_id = "project", id = "new", date_published = "2026-01-01T00:00:00Z", version_type = "release",
                    version_number = "2.0", game_versions = new[] { "1.21.1" }, loaders = new[] { "fabric" },
                    files = new[] { new { filename = update.FileName, primary = true, url = update.Url.AbsoluteUri,
                        hashes = new { sha1 = update.Sha1 }, size = update.Size } }
                } }));
            })))
            {
                var checkedMods = await new ModrinthCatalogService(updatesApi).FindModUpdatesAsync([mod, unknown], version, default);
                Check(checkedMods[0].Update?.VersionNumber == "2.0" && checkedMods[1].Update is null, "批量更新保留目标版本号与未知模组");
            }

            using var transport = Transport(content);
            string target = Path.Combine(directory, "new.jar.disabled");
            File.WriteAllText(target, "existing");
            await RejectAsync(() => ModManagementService.UpdateAsync(directory, mod, update, transport, () => { }, default));
            Check(File.ReadAllText(target) == "existing" && File.Exists(mod.Path), "更新目标冲突保留两者");
            File.Delete(target);
            await RejectAsync(() => ModManagementService.UpdateAsync(directory, mod, update, transport,
                () => throw new IOException("game started"), default));
            Check(File.Exists(mod.Path) && !File.Exists(target), "发布前状态变化保留旧版");
            using (var bad = Transport(Encoding.UTF8.GetBytes("bad")))
                await RejectAsync(() => ModManagementService.UpdateAsync(directory, mod, update, bad, () => { }, default));
            Check(File.ReadAllText(mod.Path) == "old", "校验失败不破坏旧版");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { await ModManagementService.UpdateAsync(directory, mod, update, transport, () => { }, cancellation.Token); throw new Exception("取消失效"); }
                catch (OperationCanceledException) { }
            }
            await ModManagementService.UpdateAsync(directory, mod, update, transport, () => { }, default);
            Check(!File.Exists(mod.Path) && File.ReadAllText(target) == "new release", "更新实际替换并保留禁用状态");
            mod = (await ModManagementService.ScanAsync(directory, default)).Single() with { Project = mod.Project };
            mod = ModManagementService.Toggle(directory, mod);
            byte[] next = Encoding.UTF8.GetBytes("same name update");
            using var nextTransport = Transport(next);
            await ModManagementService.UpdateAsync(directory, mod, update with { Sha1 = Convert.ToHexStringLower(SHA1.HashData(next)), Size = next.Length }, nextTransport, () => { }, default);
            Check(File.ReadAllText(mod.Path) == "same name update", "同文件名原子替换");
            await RejectAsync(() => Task.Run(() => ModManagementService.Validate(directory, mod)));
            Check(!Directory.EnumerateFiles(directory).Any(x => Path.GetFileName(x).StartsWith(".nyx-update-")), "成功失败均清理暂存");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("模组管理：目录、启停、摘要、更新与失败保护检查通过。");
    }

    /// <summary>构造复用真实下载及校验代码的内存 HTTP 传输，避免外部网络改变测试结果。</summary>
    private static DownloadTransport Transport(byte[] content) => new(new DownloadSettings(FileSource: SourceStrategy.OfficialFirst),
        new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }))));
    /// <summary>创建标准 JSON 成功响应，用最少字段验证摘要查询不依赖下载页统计。</summary>
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    /// <summary>断言明确业务结果，失败信息指出被破坏的规则。</summary>
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    /// <summary>要求操作按 I/O 边界拒绝，不把成功执行当作异常测试通过。</summary>
    private static async Task RejectAsync(Func<Task> action)
    { try { await action(); } catch (IOException) { return; } throw new Exception("预期拒绝文件操作。"); }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>按测试场景提供协议响应，沿用调用方的取消令牌。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
