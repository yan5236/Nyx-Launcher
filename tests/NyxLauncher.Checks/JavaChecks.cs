using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CmlLib.Core.Version;
using NyxLauncher.Services;

internal static class JavaChecks
{
    private static int _checks;

    /// <summary>用临时 Java 元数据和真实 ZIP 验证发现、持久化、选择、继承、路径保护、下载校验及取消；不执行伪造程序。</summary>
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-java-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var older = CreateJava(Path.Combine(root, "jdk21-old"), "21.0.9");
            var latest = CreateJava(Path.Combine(root, "jdk21-new"), "21.0.10");
            var eight = CreateJava(Path.Combine(root, "jdk8"), "1.8.0_472");
            var x86 = CreateJava(Path.Combine(root, "jdk21-x86"), "21.0.11", 0x14c);
            Check(JavaRuntime.Inspect(Path.Combine(eight.Home, "bin"))?.Major == 8 && JavaRuntime.ParseMajor("8u51-cacert") == 8, "Java 8 版本格式及 bin 导入");
            Check(string.CompareOrdinal(JavaRuntime.SortKey("1.8.0_472"), JavaRuntime.SortKey("8u51-cacert462b08")) > 0, "Java 8 跨发行商格式正确排序");
            Check(string.CompareOrdinal(JavaRuntime.SortKey("21.0.10+7"), JavaRuntime.SortKey("21+35-LTS")) > 0, "省略零段的早期版本不会排在安全更新之前");
            CreateJava(Path.Combine(root, "Java", ".staging", "pending", "jdk"), "25.0.1");
            var scan = JavaDiscovery.Scan([root, root], null, default);
            Check(scan.Runtimes.Count == 4 && scan.Runtimes.Count(x => x.Architecture == "x86") == 1, "扫描去重、识别架构并排除未发布 Java");
            var store = new JavaRuntimeStore(Path.Combine(root, "java.json"));
            Check(store.TryBeginAutomaticScan() && !store.TryBeginAutomaticScan(), "新配置只允许一次自动扫描");
            Check(!new JavaRuntimeStore(Path.Combine(root, "java.json")).TryBeginAutomaticScan(), "空扫描结果也跨重启保留首次扫描状态");
            string legacyPath = Path.Combine(root, "legacy.json");
            File.WriteAllText(legacyPath, """{"Runtimes":[],"SelectedHome":null}""");
            Check(!new JavaRuntimeStore(legacyPath).TryBeginAutomaticScan(), "旧配置升级不重复自动扫描");
            store.Add(scan.Runtimes); store.Add([latest]); store.Select(latest.Home);
            Check(new JavaRuntimeStore(Path.Combine(root, "java.json")).Current.SelectedHome == latest.Home && store.Current.Runtimes.Count == 4, "导入去重和选择持久化");
            store.Remove(latest.Home);
            Check(File.Exists(latest.Executable) && store.Current.SelectedHome is null && store.Current.Runtimes.Count == 3, "移除保留文件并回退自动");
            Check(!new JavaRuntimeStore(Path.Combine(root, "java.json")).TryBeginAutomaticScan(), "导入、选择和移除不会重置首次扫描标记");
            store.Add(JavaDiscovery.Scan([root], null, default).Runtimes);
            Check(store.Current.Runtimes.Count == 4, "重新扫描恢复移除记录");
            Check(JavaRuntimeStore.Resolve(store.Current, 21, []).Home == latest.Home, "自动选同主版本最新 x64 而非 x86 或字典序");
            Check(JavaRuntimeStore.Resolve(new([], null), 8, [eight]).Home == eight.Home, "自动模式可复用游戏内置 Java");
            await ThrowsAsync<IOException>(() => Task.FromResult(JavaRuntimeStore.Resolve(store.Current, 25, [])), "缺少 Java 25 不降级");
            await ThrowsAsync<IOException>(() => Task.FromResult(JavaRuntimeStore.Resolve(store.Current with { SelectedHome = eight.Home }, 21, [])), "手动不兼容 Java 明确拒绝");
            File.Delete(older.Executable);
            await ThrowsAsync<IOException>(() => Task.FromResult(JavaRuntimeStore.Resolve(store.Current with { SelectedHome = older.Home }, 21, [])), "失效手动选择明确拒绝");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => Task.FromResult(JavaDiscovery.Scan([root], null, cancelled.Token)), "取消扫描不产生半份结果");
            string blocked = Path.Combine(root, "blocked.json"); Directory.CreateDirectory(blocked);
            var blockedStore = new JavaRuntimeStore(blocked);
            await ThrowsAsync<UnauthorizedAccessException>(() => { blockedStore.TryBeginAutomaticScan(); return Task.CompletedTask; }, "首次扫描标记保存失败明确报错");
            Check(!blockedStore.Current.AutoScanAttempted, "首次扫描标记保存失败不改变内存");
            await ThrowsAsync<UnauthorizedAccessException>(() => { blockedStore.Add([latest]); return Task.CompletedTask; }, "保存失败不更新内存");
            Check(blockedStore.Current.Runtimes.Count == 0, "保存失败保持原列表");
            CheckRequirements();
            CheckCatalogs();
            await CheckInstallationAsync(root, latest);
            await CheckLaunchAsync(root, latest);
            string shallowRoot = Path.Combine(root, "shallow");
            var boundary = CreateJava(Path.Combine(shallowRoot, "one", "two", "jdk"), "21.0.1");
            var deep = CreateJava(Path.Combine(shallowRoot, "one", "two", "three", "jdk"), "25.0.1");
            var shallow = JavaDiscovery.Scan([shallowRoot], null, default);
            Check(shallow.Runtimes.Count == 1 && shallow.Runtimes[0].Home == boundary.Home, "浅搜索包含第三层 Java，排除第四层");
            Check(JavaDiscovery.Scan([shallowRoot, deep.Home], null, default).Runtimes.Count == 2, "深层已知 Java 可作为独立浅搜索入口");
            Check(JavaDiscovery.SearchRoots([deep.Home]).Contains(deep.Home), "搜索入口包含已登记深层目录");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"通过 {_checks} 项 Java 管理检查。");
    }

    /// <summary>模拟本地 Java 的 PE 架构和 release 文件，仅供静态发现与安装校验使用，测试不会执行此文件。</summary>
    private static JavaRuntime CreateJava(string home, string version, ushort machine = 0x8664)
    {
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        byte[] bytes = new byte[128];
        BitConverter.GetBytes((ushort)0x5a4d).CopyTo(bytes, 0); BitConverter.GetBytes(64).CopyTo(bytes, 60);
        BitConverter.GetBytes(0x4550).CopyTo(bytes, 64); BitConverter.GetBytes(machine).CopyTo(bytes, 68);
        File.WriteAllBytes(Path.Combine(home, "bin", "java.exe"), bytes);
        File.WriteAllText(Path.Combine(home, "release"), $"JAVA_VERSION=\"{version}\"\nIMPLEMENTOR=\"Test\"\n");
        return JavaRuntime.Inspect(home) ?? throw new Exception("Java 夹具无效。");
    }

    /// <summary>验证加载器继承、子级覆盖、老清单和新 Java 25 声明，保证选择依据游戏元数据而非名称。</summary>
    private static void CheckRequirements()
    {
        using var old = new JsonVersion(JsonDocument.Parse("""{"id":"old"}"""), new());
        using var parent = new JsonVersion(JsonDocument.Parse("""{"id":"base","javaVersion":{"component":"java-runtime-epsilon","majorVersion":25}}"""), new());
        using var child = new JsonVersion(JsonDocument.Parse("""{"id":"child","inheritsFrom":"base"}"""), new()) { ParentVersion = parent };
        using var overridden = new JsonVersion(JsonDocument.Parse("""{"id":"child","javaVersion":{"component":"java-runtime-delta","majorVersion":21}}"""), new()) { ParentVersion = parent };
        Check(GameLaunchService.RequiredJavaMajor(old) == 8 && GameLaunchService.RequiredJavaMajor(child) == 25 &&
            GameLaunchService.RequiredJavaMajor(overridden) == 21, "Java 声明继承、覆盖及老版本默认值");
    }

    /// <summary>校验三个渠道的真实响应形状，排除错误架构、其他主版本及非游戏运行时组件。</summary>
    private static void CheckCatalogs()
    {
        string hash = new('a', 40);
        using var mojang = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["windows-x64"] = new Dictionary<string, object>
            {
                ["java-runtime-epsilon"] = new[] { new { version = new { name = "25.0.1" }, manifest = new { url = "https://example.test/manifest", sha1 = hash, size = 100 } } },
                ["minecraft-java-exe"] = new[] { new { version = new { name = "25" }, manifest = new { url = "https://example.test/fake", sha1 = hash, size = 100 } } }
            }
        }));
        Check(JavaCatalogService.Parse(mojang.RootElement, 25, JavaChannel.Mojang).Count == 1 && JavaCatalogService.Parse(mojang.RootElement, 21, JavaChannel.Mojang).Count == 0,
            "Mojang 按实际主版本过滤并排除启动包装器");
        using var temurin = JsonDocument.Parse(JsonSerializer.Serialize(new[] { new
        {
            version_data = new { major = 21, openjdk_version = "21.0.10+7" }, release_name = "jdk-21.0.10+7",
            binaries = new[] { new { architecture = "x64", os = "windows", image_type = "jdk", package = new { link = "https://example.test/java.zip", checksum = new string('b', 64), size = 100 } } }
        } }));
        Check(JavaCatalogService.Parse(temurin.RootElement, 21, JavaChannel.Temurin).Single().Hash?.Length == 64, "Temurin 包保留 SHA-256");
        using var zulu = JsonDocument.Parse("""[{"java_version":[25,0,1],"openjdk_build_number":8,"name":"zulu25-ca-jdk25.0.1-win_x64.zip","package_uuid":"47a980b0-effe-4a4d-aaa3-3206176140b8","download_url":"https://cdn.azul.com/zulu/bin/java.zip"}]""");
        Check(JavaCatalogService.Parse(zulu.RootElement, 25, JavaChannel.Zulu).Single().Version == "25.0.1+8", "Zulu 保留具体版本及包 ID");
    }

    /// <summary>以真实 ZIP 和共享传输器验证校验后发布、自动登记、清理范围、哈希失败、越界及取消。</summary>
    private static async Task CheckInstallationAsync(string root, JavaRuntime fixture)
    {
        string zip = Path.Combine(root, "fixture.zip");
        ZipFile.CreateFromDirectory(fixture.Home, zip);
        byte[] bytes = await File.ReadAllBytesAsync(zip);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        var package = new JavaPackage(JavaChannel.Temurin, 21, fixture.Version, "fixture", "https://example.test/java.zip", hash, bytes.Length);
        var store = new JavaRuntimeStore(Path.Combine(root, "installed.json"));
        using var transport = new DownloadTransport(new(), new HttpClient(new BytesHandler(bytes)));
        var install = new JavaInstallationService(root, "install", package, store);
        await install.InstallAsync(transport, (_, _) => { }, default);
        install.Cleanup();
        Check(store.Current.Runtimes.Single().Home == install.Destination && File.Exists(Path.Combine(install.Destination, "bin", "java.exe")), "校验、解压、发布、自动登记完整流程");
        Check(!Directory.Exists(Path.Combine(root, "Java", ".staging", "install")), "完成清理暂存并保留安装内容");
        await ThrowsAsync<IOException>(() => Task.FromResult(new JavaInstallationService(root, "duplicate", package, store)), "拒绝覆盖已有 Java");
        var corrupt = new JavaInstallationService(root, "corrupt", package with { Id = "bad", Hash = new string('0', 64) }, store);
        await ThrowsAsync<IOException>(() => corrupt.InstallAsync(transport, (_, _) => { }, default), "SHA-256 不符不发布");
        Check(!Directory.Exists(corrupt.Destination) && store.Current.Runtimes.Count == 1, "坏包不污染 Java 列表");
        corrupt.Cleanup();
        var cancelled = new JavaInstallationService(root, "cancel", package with { Id = "cancel" }, store);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => cancelled.InstallAsync(transport, (_, _) => { }, cancellation.Token), "取消安装不发布");
        cancelled.Cleanup();
        foreach (string name in new[] { "../escape", "a/../../escape", "C:/escape", "bin/java.exe:stream", "CON", "a\\..\\escape" })
            await ThrowsAsync<ArgumentException>(() => Task.FromResult(JavaInstallationService.SafePath(root, name)), "拒绝远程危险路径");
        string malicious = Path.Combine(root, "bad.zip");
        using (var archive = ZipFile.Open(malicious, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open()); writer.Write("bad"); }
        await ThrowsAsync<ArgumentException>(() => JavaInstallationService.ExtractAsync(malicious, Path.Combine(root, "safe"), default), "ZIP 穿越在写入前拒绝");
        Check(!File.Exists(Path.Combine(root, "outside.txt")), "越界文件未写入");
    }

    /// <summary>使用独立本地清单走真实准备与进程构造，验证固定的 Java 路径进入启动参数；不执行测试程序。</summary>
    private static async Task CheckLaunchAsync(string root, JavaRuntime runtime)
    {
        string game = Path.Combine(root, "game");
        string versionPath = Path.Combine(game, "versions", "java-check");
        Directory.CreateDirectory(versionPath);
        await File.WriteAllTextAsync(Path.Combine(versionPath, "java-check.json"), """
            {"id":"java-check","type":"release","mainClass":"test.Main","libraries":[],
             "javaVersion":{"component":"java-runtime-delta","majorVersion":21},
             "arguments":{"jvm":["-cp","${classpath}"],"game":["--username","${auth_player_name}"]}}
            """);
        var version = InstalledVersionService.Scan(game).Single();
        string? selected = await GameLaunchService.PrepareAsync(game, version, new(), (_, _) => { }, default, new([runtime], null));
        using var process = await GameLaunchService.BuildAsync(game, version, IsolationMode.All, new("JavaTest"), javaPath: selected);
        Check(selected == runtime.Executable && process.StartInfo.FileName == selected && process.StartInfo.Arguments.Contains("JavaTest"),
            "Java 自动选择贯通准备与真实游戏进程参数");
        await ThrowsAsync<IOException>(() => GameLaunchService.PrepareAsync(game, version, new(), (_, _) => { }, default,
            new([], Path.Combine(root, "missing-java"))), "启动准备明确拒绝失效手动 Java");
    }

    /// <summary>显式浅搜索常见位置和全部盘符，限时两分钟，记录版本与耗时；只写验收目录，不修改用户配置。</summary>
    public static async Task ScanLiveAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var drives = JavaDiscovery.DriveRoots();
        Console.WriteLine("扫描盘符：" + string.Join(", ", drives));
        var result = await Task.Run(() => JavaDiscovery.Scan(JavaDiscovery.SearchRoots([]), null, timeout.Token));
        await File.WriteAllTextAsync(Path.Combine(root, "shallow-scan.json"), JsonSerializer.Serialize(result));
        Console.WriteLine($"浅搜索：{result.Runtimes.Count} 个 Java，跳过 {result.SkippedDirectories} 个目录，耗时 {watch.Elapsed.TotalSeconds:0.0}s");
        foreach (var version in result.Runtimes.GroupBy(x => x.Major).OrderBy(x => x.Key))
            Console.WriteLine($"Java {version.Key}：{version.Count()} 项");
    }

    /// <summary>显式实网检查三个渠道各主版本列表；可进一步为每个渠道创建真实下载任务并执行下载后 Java -version。</summary>
    public static async Task LiveAsync(string root, bool install)
    {
        Directory.CreateDirectory(root);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        var catalog = new JavaCatalogService(http);
        var settings = new DownloadSettingsStore(Path.Combine(root, "download-settings.json"));
        settings.Save(new(FileSource: SourceStrategy.OfficialFirst, MaxConnections: 8));
        var manager = new DownloadTaskManager(settings, action => action());
        var store = new JavaRuntimeStore(Path.Combine(root, "java-settings.json"));
        try
        {
            foreach (var channel in Enum.GetValues<JavaChannel>())
            {
                foreach (int major in new[] { 8, 17, 21, 25 })
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    var versions = await catalog.FetchAsync(major, channel, timeout.Token);
                    Check(versions.Count > 0 && versions.All(x => x.Major == major), channel + " " + major + " 真实版本列表");
                    var selected = versions[0];
                    Console.WriteLine($"{channel} Java {major}：{versions.Count} 项，首项 {selected.Version}");
                    if (!install || major != (channel == JavaChannel.Mojang ? 25 : 8)) continue;
                    string destination = Path.Combine(root, "Java", selected.FolderName);
                    if (!Directory.Exists(destination))
                    {
                        var task = manager.AddJava(selected, store, root);
                        await task.Execution.WaitAsync(TimeSpan.FromMinutes(8));
                        Check(task.State == DownloadState.Completed, "真实 Java 下载任务完成：" + task.Detail);
                        Check(store.Current.Runtimes.Any(x => x.Home == task.Destination), "真实 Java 下载完成自动入列表");
                        manager.RemoveFinished(task);
                        Check(Directory.Exists(destination), "移除下载记录保留 Java");
                    }
                    var runtime = JavaRuntime.Inspect(destination) ?? throw new Exception("下载后 Java 无效");
                    using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(runtime.Executable, "-version")
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })!;
                    var stderr = process.StandardError.ReadToEndAsync();
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    string output = await stderr + await stdout;
                    Check(process.ExitCode == 0, "下载后 Java 可实际执行");
                    Console.WriteLine(output.Trim());
                }
            }
        }
        finally { await manager.ShutdownAsync(); }
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        /// <summary>为归档集成检查提供固定文件字节，保留真实下载器的读写和哈希行为。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }
    }

    /// <summary>断言具体结果并计数，失败提供明确的业务说明。</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); _checks++; }

    /// <summary>要求异步流程抛出指定异常，未抛出或类型错误均令测试失败。</summary>
    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { _checks++; return; } throw new Exception(message); }
}
