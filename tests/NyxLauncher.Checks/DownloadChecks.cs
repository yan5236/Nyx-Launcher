using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using NyxLauncher.Services;

internal static class DownloadChecks
{
    private static int _checks;

    /// <summary>验证实际文件字节、分段响应退化、损坏保护、取消清理和五种隔离策略；无需访问外网。</summary>
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-download-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] data = new byte[5 * 1024 * 1024 + 13];
            new Random(32).NextBytes(data);
            string hash = Convert.ToHexString(SHA1.HashData(data));
            string empty = Path.Combine(root, "empty");
            File.WriteAllBytes(empty, []);
            Check(await DownloadTransport.ValidAsync(empty, "da39a3ee5e6b4b0d3255bfef95601890afd80709", 0, default), "Java 运行库合法空文件按哈希通过");
            Check(!await DownloadTransport.ValidAsync(empty, null, 0, default), "无哈希的空缓存不误判为完整依赖");
            foreach (bool ignoreRange in new[] { false, true })
            {
                var handler = new FileHandler(data, ignoreRange);
                using var transport = new DownloadTransport(new(MaxConnections: 3), new HttpClient(handler));
                string target = Path.Combine(root, ignoreRange + ".bin");
                await transport.DownloadAsync(new("https://example.test/file"), target, hash, data.Length, false, true, null, default);
                Check(File.ReadAllBytes(target).SequenceEqual(data), "分段/忽略 Range 均输出准确字节");
                Check(handler.Ranges > 0 && handler.Maximum <= 3, "执行 Range 且遵守连接上限");
                await Throws<IOException>(() => transport.DownloadAsync(new("https://example.test/file"), target, null, 0, false, true, null, default), "普通下载不覆盖已有文件");
            }
            using (var transport = new DownloadTransport(new(), new HttpClient(new FileHandler(data, false))))
            {
                string target = Path.Combine(root, "corrupt.bin");
                await Throws<IOException>(() => transport.DownloadAsync(new("https://example.test/file"), target, new string('0', 40), data.Length, false, false, null, default), "哈希不一致拒绝发布");
                Check(!File.Exists(target) && !Directory.EnumerateFiles(root, "*.part").Any(), "失败清理临时文件");
                using var cancel = new CancellationTokenSource();
                await Throws<OperationCanceledException>(() => transport.DownloadAsync(new("https://example.test/file"), target, null, data.Length,
                    false, false, (_, _) => cancel.Cancel(), cancel.Token), "下载中取消");
                Check(!File.Exists(target) && !Directory.EnumerateFiles(root, "*.part").Any(), "取消不发布半成品");
            }
            foreach (string name in new[] { "../escape", "..", "CON", "NUL.zip", "x.", "x ", "a/b" })
                await Throws<ArgumentException>(() => { GameInstallationService.ValidateName(name); return Task.CompletedTask; }, "非法目标名被拒绝");
            foreach (string url in new[] { "file:///c:/test", "https://user:password@example.com/file", "bad" })
                await Throws<ArgumentException>(() => { DownloadTransport.ValidateUrl(url); return Task.CompletedTask; }, "非法下载链接被拒绝");
            await Throws<IOException>(() =>
            {
                GameDownloadInstaller.ValidateDestination(root, new CmlLib.Core.Files.GameFile("escape")
                    { Path = Path.Combine(root, "..", "outside.jar"), Url = "https://libraries.minecraft.net/a.jar" });
                return Task.CompletedTask;
            }, "远程清单下载路径不能越过游戏目录");
            GameDownloadInstaller.ValidateDestination(root, new CmlLib.Core.Files.GameFile("library")
                { Path = Path.Combine(root, "libraries", "sample.jar"), Url = "https://libraries.minecraft.net/sample.jar" });
            string installerPath = Path.Combine(Path.GetTempPath(), "nyx-installer-check-" + Guid.NewGuid().ToString("N"), "installer.jar");
            foreach (string host in new[] { "maven.minecraftforge.net", "maven.neoforged.net" })
                GameDownloadInstaller.ValidateDestination(root, new CmlLib.Core.Files.GameFile("installer")
                    { Path = installerPath, Url = "https://" + host + "/installer.jar" });
            await Throws<IOException>(() =>
            {
                GameDownloadInstaller.ValidateDestination(root, new CmlLib.Core.Files.GameFile("installer")
                    { Path = installerPath, Url = "https://example.test/installer.jar" });
                return Task.CompletedTask;
            }, "临时目录例外仅允许官方加载器安装包");
            foreach (bool modded in new[] { false, true })
                foreach (bool release in new[] { false, true })
                    foreach (var mode in Enum.GetValues<IsolationMode>())
                    {
                        var version = new InstalledVersion("sample", Path.Combine(root, "versions", "sample"), "1.21.1", release ? "正式版" : "快照版", modded ? "Fabric" : "", null);
                        bool expected = mode == IsolationMode.All || mode == IsolationMode.Modded && modded ||
                            mode == IsolationMode.NonRelease && !release || mode == IsolationMode.ModdedOrNonRelease && (modded || !release);
                        Check(VersionIsolation.Resolve(root, version, mode) == (expected ? version.DirectoryPath : root), "五种隔离模式的真实路径");
                    }
            var settings = new DownloadSettingsStore(Path.Combine(root, "settings.json"));
            var manager = new DownloadTaskManager(settings, action => action());
            var finished = new DownloadTaskItem { Name = "empty", Destination = empty, State = DownloadState.Completed };
            manager.Tasks.Add(finished);
            Check(finished.CanDismiss && finished.DismissLabel == "删除下载记录" && finished.DismissGlyph == "\uE74D", "完成状态切换为删除记录图标");
            manager.RemoveFinished(finished);
            Check(manager.Tasks.Count == 0 && File.Exists(empty), "移除完成记录保留实际文件");
            Check(settings.Save(new(Isolation: IsolationMode.ModdedOrNonRelease)) is null &&
                new DownloadSettingsStore(Path.Combine(root, "settings.json")).Current.Isolation == IsolationMode.ModdedOrNonRelease, "隔离配置持久化");
            var minecraft = new CmlLib.Core.MinecraftPath(root);
            Directory.CreateDirectory(Path.Combine(minecraft.Versions, "base"));
            Directory.CreateDirectory(Path.Combine(minecraft.Versions, "child"));
            File.WriteAllText(minecraft.GetVersionJsonPath("base"), """{"id":"base","mainClass":"Vanilla","libraries":[{"name":"org.lwjgl:lwjgl:3.3.3"},{"name":"org.lwjgl:lwjgl:3.3.3:natives-windows"}],"arguments":{"game":["--username","${auth_player_name}"],"jvm":["-cp","${classpath}"]}}""");
            File.WriteAllText(minecraft.GetVersionJsonPath("child"), """{"id":"child","inheritsFrom":"base","mainClass":"Fabric","libraries":[{"name":"net.fabricmc:fabric-loader:0.18.0"}],"arguments":{"game":["--loader"],"jvm":["-Dloader=true"]}}""");
            var merged = GameInstallationService.ReadMerged(minecraft, "child", new(StringComparer.OrdinalIgnoreCase));
            Check(merged["libraries"]!.AsArray().Count == 3, "合并清单保留基础库及原生分类器");
            Check(merged["mainClass"]!.GetValue<string>() == "Fabric" && merged["arguments"]!["game"]!.AsArray().Count == 3 &&
                merged["arguments"]!["jvm"]!.AsArray().Count == 3, "加载器入口覆盖且保留基础游戏和 JVM 参数");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"通过 {_checks} 项下载与隔离检查。");
    }

    /// <summary>手动请求的实网验收：同一独立目录安装原版及三个加载器，校验版本管理和实际启动参数，可重复运行复用资源。</summary>
    public static async Task LiveAsync(string root)
    {
        Directory.CreateDirectory(root);
        var settings = new DownloadSettings(FileSource: SourceStrategy.OfficialFirst, MaxConnections: 8);
        using var transport = new DownloadTransport(settings);
        var service = new LoaderAvailabilityService();
        foreach (LoaderKind? kind in new LoaderKind?[] { null, LoaderKind.Fabric, LoaderKind.Forge, LoaderKind.NeoForge })
        {
            string name = "qa-" + (kind?.ToString() ?? "vanilla");
            if (!Directory.Exists(Path.Combine(root, "versions", name)))
            {
                LoaderVersion? loader = kind is null ? null : (await service.FetchAsync(kind.Value, "1.21.1", SourceStrategy.OfficialFirst, default))[0];
                Console.WriteLine($"安装 {name} {loader?.Id}");
                var installer = new GameInstallationService(root, name, settings);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                long last = -5000;
                await installer.InstallAsync(name, "1.21.1", loader, transport, (p, detail) =>
                {
                    if (watch.ElapsedMilliseconds - Interlocked.Read(ref last) < 5000) return;
                    Interlocked.Exchange(ref last, watch.ElapsedMilliseconds);
                    Console.WriteLine($"{name} {p:0}% {detail}");
                }, default);
                installer.Cleanup();
            }
            var version = InstalledVersionService.Scan(root).Single(x => x.Name == name);
            Check(version.Error is null && version.GameVersion == "1.21.1", name + "真实元数据");
            Check(kind is null ? version.Loader == "" : version.Loader.StartsWith(kind.ToString()!), name + "真实加载器");
            using var process = await GameLaunchService.BuildAsync(root, version, IsolationMode.All, new GameAccount("Steve"));
            Check(process.StartInfo.WorkingDirectory == version.DirectoryPath && process.StartInfo.Arguments.Contains(version.DirectoryPath), name + "隔离启动参数");
            await File.WriteAllTextAsync(Path.Combine(root, name + "-launch.txt"), process.StartInfo.FileName + "\n" + process.StartInfo.Arguments);
            Console.WriteLine($"已验证 {name}：{version.Detail}，Java={process.StartInfo.FileName}");
        }
    }

    /// <summary>断言具体业务结果，失败立即终止并报告检查名称。</summary>
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("失败：" + name); _checks++; }

    /// <summary>验证失败类型，防止本应失败的路径静默成功。</summary>
    private static async Task Throws<T>(Func<Task> action, string name) where T : Exception
    { try { await action(); } catch (T) { _checks++; return; } throw new Exception("未抛出异常：" + name); }

    /// <summary>可控 HTTP 文件端点，返回真实分段字节，检测并发及服务端忽略 Range 的回退。</summary>
    private sealed class FileHandler(byte[] data, bool ignoreRange) : HttpMessageHandler
    {
        private int _active;
        public int Ranges;
        public int Maximum;

        /// <summary>模拟带强 ETag 的完整文件与 Range 响应；使用短延迟使分段并发可被断言。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            int active = Interlocked.Increment(ref _active);
            Maximum = Math.Max(Maximum, active);
            try
            {
                await Task.Delay(10, token);
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.ETag = new EntityTagHeaderValue("\"revision1\"");
                response.Headers.AcceptRanges.Add("bytes");
                var range = request.Headers.Range?.Ranges.Single();
                if (range is not null) Interlocked.Increment(ref Ranges);
                if (range is not null && !ignoreRange)
                {
                    response.StatusCode = HttpStatusCode.PartialContent;
                    response.Content = new ByteArrayContent(data[(int)range.From!.Value..((int)range.To!.Value + 1)]);
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(range.From.Value, range.To.Value, data.Length);
                }
                else response.Content = new ByteArrayContent(data);
                return response;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
