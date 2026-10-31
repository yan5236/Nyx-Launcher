using System.IO.Compression;
using System.Text;
using NyxLauncher.Services;

internal static class ModpackChecks
{
    /// <summary>用三种真实清单结构检查解析、客户端覆盖、安全边界和任务重名；临时包仅写入独立验收目录。</summary>
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-pack-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string mr = Write(root, "modrinth.index.json", """{"formatVersion":1,"game":"minecraft","name":"验收整合包","dependencies":{"minecraft":"1.21.1","fabric-loader":"0.16.10"},"files":[]} """,
                ("overrides/config/sample.txt", "common"), ("client-overrides/config/sample.txt", "client"));
            var pack = ModpackManifest.Read(mr);
            Check(pack.Name == "验收整合包" && pack.Game == "1.21.1" && pack.Loader?.Loader == LoaderKind.Fabric, "Modrinth 组件");
            Check(pack.Entries["config/sample.txt"] == "client-overrides/config/sample.txt", "客户端覆盖优先");
            string bbs = Write(root, "mcbbs.packmeta", """{"manifestVersion":1,"name":"MCBBS","addons":[{"id":"game","version":"1.20.1"},{"id":"forge","version":"47.3.0"}],"files":[]} """);
            Check(ModpackManifest.Read(bbs).Loader?.Id == "1.20.1-47.3.0", "MCBBS Forge 坐标");
            string mmc = Write(root, "instance/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.21.1"},{"uid":"net.neoforged","version":"21.1.1"},{"uid":"org.lwjgl3","version":"3.3.3"}]}""",
                ("instance/instance.cfg", "name=MultiMC 验收"), ("instance/.minecraft/config/a.txt", "ok"));
            var multi = ModpackManifest.Read(mmc);
            Check(multi.Name == "MultiMC 验收" && multi.Loader?.Loader == LoaderKind.NeoForge && multi.Entries.ContainsKey("config/a.txt"), "MultiMC 根目录与名称");
            foreach (string path in new[] { "../x", "/x", "C:/x", "mods/CON.jar", "mods/a:ads", "mods/a\\b", "mods/a. ", "mods//a" })
                Reject(() => ModpackManifest.SafePath(path), "越界路径 " + path);
            string bad = Write(root, "modrinth.index.json", "{}", ("../escape.txt", "bad"));
            Reject(() => ModpackManifest.Read(bad), "ZIP 越界");
            string unsupported = Write(root, "mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.21.1"},{"uid":"custom.patch","version":"1"}]}""");
            Reject(() => ModpackManifest.Read(unsupported), "未知组件拒绝");
            var tasks = new DownloadTaskManager(new DownloadSettingsStore(Path.Combine(root, "settings.json")), a => a());
            Directory.CreateDirectory(Path.Combine(root, "versions", "exists"));
            Reject(() => tasks.ValidateModpackName(root, "exists"), "已有版本重名");
            Reject(() => tasks.ValidateModpackName(root, "CON"), "保留名称");
            tasks.ValidateModpackName(root, "合法实例");
            string instance = Path.Combine(root, "versions", "exists");
            File.WriteAllText(Path.Combine(instance, ModpackInstallationService.IsolationMarker), "1");
            Check(VersionIsolation.Resolve(root, new("exists", instance, "1.21.1", "正式版", "", null), IsolationMode.Disabled) == instance, "整合包强制隔离");
            Console.WriteLine("整合包解析、安全路径、重名和隔离检查通过。");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>在显式指定验收目录创建三种格式的真实 Fabric 实例，下载 Fabric API 并验证任务完成、哈希与配置；保留结果供界面复核。</summary>
    public static async Task LiveAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var files = await new ModrinthCatalogService().FetchModFilesAsync("P7dR8mSH", timeout.Token);
        var file = files.First(x => x.GameVersions.Contains("1.21.1") && x.Loaders.Contains("fabric"));
        string hash = file.Sha1;
        string fixtures = Path.Combine(root, "pack-fixtures");
        Directory.CreateDirectory(fixtures);
        string json = System.Text.Json.JsonSerializer.Serialize(new {
            formatVersion = 1, game = "minecraft", name = "实网整合包验收",
            dependencies = new Dictionary<string,string> { ["minecraft"] = "1.21.1", ["fabric-loader"] = "0.16.10" },
            files = new[] { new { path = "mods/" + file.FileName, hashes = new { sha1 = hash }, fileSize = file.Size, downloads = new[] { file.Url.AbsoluteUri } } }
        });
        string mr = Write(fixtures, "modrinth.index.json", json, ("overrides/config/nyx-test.txt", "verified"));
        string bbs = Write(fixtures, "mcbbs.packmeta", System.Text.Json.JsonSerializer.Serialize(new {
            manifestVersion = 1, name = "MCBBS 实网验收", addons = new[] { new { id = "game", version = "1.21.1" }, new { id = "fabric", version = "0.16.10" } },
            files = new[] { new { type = "curse", fileName = file.FileName, url = file.Url.AbsoluteUri } }
        }), ("overrides/config/nyx-test.txt", "verified"));
        var store = new DownloadSettingsStore(Path.Combine(fixtures, "settings.json"));
        store.Save(new(FileSource: SourceStrategy.OfficialFirst, Isolation: IsolationMode.Disabled));
        var tasks = new DownloadTaskManager(store, a => a());
        foreach (var (label, archive) in new[] { ("modrinth", mr), ("mcbbs", bbs) })
        {
            string name = "pack-qa-" + label + "-" + DateTime.Now.ToString("HHmmss");
            var task = tasks.AddModpack(root, name, archive);
            await task.Execution.WaitAsync(timeout.Token);
            Check(task.State == DownloadState.Completed, label + "安装：" + task.Detail);
            Check(await DownloadTransport.ValidAsync(Path.Combine(task.Destination, "mods", file.FileName), hash, file.Size, timeout.Token), label + "模组哈希");
            var version = InstalledVersionService.Scan(root).Single(x => x.Name == name);
            Check(version.GameVersion == "1.21.1" && version.Loader.StartsWith("Fabric") && File.ReadAllText(Path.Combine(task.Destination, "config", "nyx-test.txt")) == "verified", label + "游戏加载器与配置");
            Check(VersionIsolation.Resolve(root, version, IsolationMode.Disabled) == task.Destination, label + "独立运行目录");
            Console.WriteLine(label + " 完成：" + task.Destination);
        }
        string mmc = Write(fixtures, "instance/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.21.1"},{"uid":"net.fabricmc.fabric-loader","version":"0.16.10"}]}""",
            ("instance/instance.cfg", "name=MultiMC 实网验收"), ("instance/.minecraft/config/nyx-test.txt", "verified"));
        using (var zip = ZipFile.Open(mmc, ZipArchiveMode.Update))
        {
            string installedMod = Path.Combine(tasks.Tasks[0].Destination, "mods", file.FileName);
            zip.CreateEntryFromFile(installedMod, "instance/.minecraft/mods/" + file.FileName);
        }
        var multi = tasks.AddModpack(root, "pack-qa-multimc-" + DateTime.Now.ToString("HHmmss"), mmc);
        await multi.Execution.WaitAsync(timeout.Token);
        Check(multi.State == DownloadState.Completed, "MultiMC 安装：" + multi.Detail);
        Check(await DownloadTransport.ValidAsync(Path.Combine(multi.Destination, "mods", file.FileName), hash, file.Size, timeout.Token), "MultiMC 内嵌模组哈希");
        Console.WriteLine("MultiMC 完成：" + multi.Destination);
        await tasks.ShutdownAsync();
    }
    /// <summary>通过可控 HTTP 响应验证实际资源下载、失败地址回退、覆盖优先和坏哈希阻断；不依赖网络。</summary>
    public static async Task FilesAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-pack-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
                        byte[] metadata = Encoding.UTF8.GetBytes("""[{"project_id":"abc123","id":"def456","game_versions":["1.21.1"],"loaders":["fabric"],"dependencies":[],"date_published":"2026-10-31T00:00:00Z","version_type":"release","files":[{"filename":"test.mrpack","primary":true,"url":"https://cdn.modrinth.com/data/abc123/versions/def456/test.mrpack","hashes":{"sha1":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"size":100}]}]""");
            using var catalogHttp = new HttpClient(new PackHandler(metadata));
            var catalog = new ModrinthCatalogService(catalogHttp);
            var releases = await catalog.FetchModFilesAsync("abc123", default, true);
            Check(releases.Count == 1 && releases[0].IsModpack && releases[0].FileName == "test.mrpack", "在线整合包发布文件解析");
            Check((await catalog.FetchModFilesAsync("abc123", default)).Count == 0, "模组查询不混入整合包");
            byte[] data = Encoding.UTF8.GetBytes("downloaded mod bytes");
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(data));
            string zip = Write(root, "modrinth.index.json", "{}", ("overrides/config/a.txt", "common"), ("client-overrides/config/a.txt", "client"));
            var manifest = new ModpackManifest("Test", "1.21.1", null,
                [new("mods/a.jar", hash, data.Length, [new("https://test.invalid/fail"), new("https://test.invalid/ok")])],
                new Dictionary<string,string> { ["config/a.txt"] = "client-overrides/config/a.txt" });
            string publish = Path.Combine(root, "publish");
            using var transport = new DownloadTransport(new(), new HttpClient(new PackHandler(data)));
            await ModpackInstallationService.InstallFilesAsync(zip, publish, "Test", manifest, transport, (_, _) => { }, default);
            Check(File.ReadAllBytes(Path.Combine(publish, "mods/a.jar")).SequenceEqual(data), "下载地址回退后保留真实字节");
            Check(File.ReadAllText(Path.Combine(publish, "config/a.txt")) == "client", "写入客户端覆盖内容");
            var bad = manifest with { Files = [new("mods/b.jar", new string('0',40), data.Length, [new("https://test.invalid/ok")])] };
            bool rejected = false;
            try { await ModpackInstallationService.InstallFilesAsync(zip, publish, "Test", bad, transport, (_, _) => { }, default); }
            catch (IOException) { rejected = true; }
            Check(rejected && !File.Exists(Path.Combine(publish, "mods/b.jar")), "坏哈希不发布文件");
            var protectedFile = manifest with { Entries = new Dictionary<string,string> { ["Test.json"] = "overrides/config/a.txt" } };
            rejected = false;
            try { await ModpackInstallationService.InstallFilesAsync(zip, publish, "Test", protectedFile, transport, (_, _) => { }, default); }
            catch (IOException) { rejected = true; }
            Check(rejected, "禁止覆盖启动清单");
                        string destination = Path.Combine(root, "published");
            await GameInstallationService.PublishAsync(publish, destination, default);
            Check(File.Exists(Path.Combine(destination, "mods/a.jar")) && !Directory.Exists(publish), "完整目录原子发布");
            Directory.CreateDirectory(publish);
            bool duplicate = false;
            try { await GameInstallationService.PublishAsync(publish, destination, default); }
            catch (IOException) { duplicate = true; }
            Check(duplicate && Directory.Exists(publish), "发布冲突保留暂存且不覆盖版本");
            Console.WriteLine("整合包资源传输、覆盖和校验检查通过。");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>返回固定字节或失败状态，模拟不同下载地址而不替代实际传输和文件校验。</summary>
    private sealed class PackHandler(byte[] bytes) : HttpMessageHandler
    {
        /// <summary>按 URL 返回 404 或完整文件响应；由生产传输代码负责重试与写入。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(request.RequestUri!.AbsolutePath == "/fail" ? System.Net.HttpStatusCode.NotFound : System.Net.HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes) });
    }
    /// <summary>创建最小 ZIP 输入；所有测试内容由调用方给定，不访问网络。</summary>
    private static string Write(string root, string manifest, string json, params (string Path, string Text)[] files)
    {
        string path = Path.Combine(root, Guid.NewGuid() + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files.Prepend((manifest, json)))
        {
            using var writer = new StreamWriter(zip.CreateEntry(file.Item1).Open(), new UTF8Encoding(false));
            writer.Write(file.Item2);
        }
        return path;
    }

    /// <summary>断言业务结果，失败立即中止检查并报告场景。</summary>
    private static void Check(bool value, string label)
    { if (!value) throw new Exception("整合包检查失败：" + label); }

    /// <summary>确认不可信输入被拒绝，不能以静默忽略或成功返回替代。</summary>
    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return; }
        throw new Exception("未拒绝：" + label);
    }
}
