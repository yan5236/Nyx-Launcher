using System.Text.Json;
using System.Net;
using System.Security.Cryptography;
using NyxLauncher.Services;

internal static class AccountLaunchChecks
{
    private static int _checks;

    /// <summary>验证离线身份稳定性、配置事务、空账户、窗口判定和取消入口；使用独立临时配置，不接触真实账户。</summary>
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-accounts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await VerifyAssetCopiesAsync(root);
            VerifyAccountRemoval(root);
            string path = Path.Combine(root, "accounts.json");
            var accounts = new AccountStore(path);
            Check(accounts.Accounts.Count == 0 && accounts.Selected is null && !File.Exists(path), "默认空账户");
            accounts.Add("Steve");
            Check(accounts.Selected!.Uuid == "5627dd98e6be3c21b8a8e92344183641", "Java 标准离线 UUID");
            Check(accounts.Selected.IsAlex, "名称 Steve 的离线 UUID 为奇数哈希，使用 Alex");
            accounts.Add("Alex");
            Check(accounts.Selected!.Uuid == "36532b5ec4423dbba24cc7e55d0f979a" && !accounts.Selected.IsAlex, "名称 Alex 的离线 UUID 为偶数哈希，使用 Steve");
            var reread = new AccountStore(path);
            Check(reread.Accounts.Count == 2 && reread.Selected == accounts.Selected, "新增和选择重启恢复");
            reread.Select(reread.Accounts[0]);
            Check(new AccountStore(path).Selected!.Name == "Steve", "卡片切换落盘");
            var session = GameLaunchService.CreateSession(accounts.Selected);
            Check(session.Username == "Alex" && session.UUID == accounts.Selected.Uuid, "真实启动会话身份");
            foreach (string name in new[] { "", " ", "abc def", "../x", "中文", "12345678901234567", "x\n" })
                Throws<ArgumentException>(() => accounts.Add(name), "拒绝非法名称");
            Throws<ArgumentException>(() => accounts.Add("alex"), "拒绝大小写重复");
            Throws<ArgumentException>(() => accounts.Select(new("Missing")), "不能切换未登记账户");
            Check(accounts.Accounts.Count == 2 && accounts.Selected.Name == "Alex", "校验失败保留选择");
            File.WriteAllText(path, "broken");
            var broken = new AccountStore(path);
            Throws<IOException>(() => broken.Add("Test"), "损坏配置拒绝覆盖");
            Check(File.ReadAllText(path) == "broken" && broken.Selected is null, "原损坏文件保留");
            string blocked = Path.Combine(root, "blocked");
            Directory.CreateDirectory(blocked);
            var failed = new AccountStore(blocked);
            Throws<UnauthorizedAccessException>(() => failed.Add("Test"), "写入失败");
            Check(failed.Accounts.Count == 0 && failed.Selected is null, "写入失败不改内存");
            foreach (string text in new[] { "{}", "null", "{\"Accounts\":[null]}", "{\"Accounts\":[],\"Selected\":\"x\"}" })
            { File.WriteAllText(path, text); Check(new AccountStore(path).LoadError is not null, "配置结构校验"); }
            foreach (string cls in new[] { "GLFW30", "LWJGL", "SDL_app", "SunAwtFrame" })
                Check(GameWindow.IsGameWindow(cls, "自定义游戏标题", true), "允许真实游戏类 " + cls);
            foreach (string title in new[] { "", "GLFW message window", "Minecraft Hidden Utility Window", "FML early window", "Quilt Loader", "SDL_app", "PopupMessageWindow" })
                Check(!GameWindow.IsGameWindow("GLFW30", title, true), "排除辅助窗口 " + title);
            Check(!GameWindow.IsGameWindow("GLFW30", "Minecraft", false) && !GameWindow.IsGameWindow("ConsoleWindowClass", "Minecraft", true), "排除隐藏窗口和控制台");
            var cancelled = new GameLaunchSession();
            cancelled.Stop(); cancelled.Stop();
            await cancelled.RunAsync(root, new("missing", root, "", "", "", null), new(), new("Test"));
            Check(cancelled.Status == new LaunchStatus(LaunchStage.Exited, "已停止启动"), "准备前取消不读取版本或启动进程");
            var missing = new GameLaunchSession();
            await missing.RunAsync(root, new("missing", root, "", "", "", null), new(), new("Test"));
            Check(missing.Status.Stage == LaunchStage.Failed && missing.Status.Message.Contains("不存在"), "缺失版本明确失败");
            Console.WriteLine($"通过 {_checks} 项账户与启动检查。");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>验证账户删除的选择回退、清空与重启恢复；模拟磁盘发布失败，确保账户和事件均不提前变化。</summary>
    private static void VerifyAccountRemoval(string root)
    {
        string path = Path.Combine(root, "remove-accounts.json");
        var accounts = new AccountStore(path);
        accounts.Add("First"); accounts.Add("Second"); accounts.Add("Third");
        var first = accounts.Accounts[0];
        var second = accounts.Accounts[1];
        var third = accounts.Accounts[2];
        int changes = 0;
        accounts.Changed += (_, _) => changes++;
        accounts.Remove(second);
        Check(accounts.Accounts.SequenceEqual(new[] { first, third }) && accounts.Selected == third && changes == 1,
            "删除未选中账户保留当前身份并发布一次变化");
        accounts.Remove(third);
        Check(accounts.Selected == first && new AccountStore(path).Selected == first, "删除当前账户回退首项并落盘");
        var session = GameLaunchService.CreateSession(first);
        accounts.Select(first);
        Check(session.Username == first.Name && session.UUID == first.Uuid && changes == 2, "离线重新登录保持身份且不重复写入");
        Throws<ArgumentException>(() => accounts.Remove(new("Missing")), "拒绝删除未登记账户");
        File.Delete(path);
        Directory.CreateDirectory(path);
        Throws<UnauthorizedAccessException>(() => accounts.Remove(first), "删除保存失败向调用方报告");
        Check(accounts.Accounts.SequenceEqual(new[] { first }) && accounts.Selected == first && changes == 2,
            "删除失败保留原列表选择且不发布变化");
        Directory.Delete(path);
        accounts.Remove(first);
        var reread = new AccountStore(path);
        Check(accounts.Accounts.Count == 0 && accounts.Selected is null && reread.Accounts.Count == 0 &&
            reread.Selected is null && reread.LoadError is null && changes == 3, "删除最后账户后空状态可恢复");
    }

    /// <summary>复现旧版资源映射副本同长度损坏不修复的问题，并验证后处理不能写出游戏根目录。</summary>
    private static async Task VerifyAssetCopiesAsync(string root)
    {
        string game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);
        string source = Path.Combine(game, "object");
        string copy = Path.Combine(game, "resources", "sound.ogg");
        byte[] original = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(source, original);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        await File.WriteAllBytesAsync(copy, [4, 3, 2, 1]);
        var file = new CmlLib.Core.Files.GameFile("sound")
        {
            Path = source, Hash = Convert.ToHexString(SHA1.HashData(original)), Size = original.Length,
            UpdateTask = [new CmlLib.Core.Tasks.FileCopyTask(copy)]
        };
        using var transport = new DownloadTransport(new());
        var installer = new GameDownloadInstaller(transport, game);
        await installer.Install([file], null, null, default);
        Check(File.ReadAllBytes(copy).SequenceEqual(original), "资源映射副本同长度损坏也按哈希修复");
        string outside = Path.Combine(root, "outside");
        await File.WriteAllTextAsync(outside, "protected");
        try
        {
            await installer.Install([file with { UpdateTask = [new CmlLib.Core.Tasks.FileCopyTask(outside)] }], null, null, default);
            throw new Exception("越界资源副本未被拒绝");
        }
        catch (IOException) { Check(File.ReadAllText(outside) == "protected", "资源后处理拒绝越界且保留原文件"); }
    }

    /// <summary>使用已有独立验收目录启动四种真实客户端；验证会话阶段、账户参数及停止后进程结束，不安装新版本。</summary>
    public static async Task LiveAsync(string root)
    {
        var settings = new DownloadSettings(FileSource: SourceStrategy.OfficialFirst, VersionSource: SourceStrategy.OfficialFirst, MaxConnections: 8);
        var versions = InstalledVersionService.Scan(root);
        await VerifyRepairAsync(root, versions.Single(x => x.Name == "qa-vanilla"), settings);
        foreach (string name in new[] { "qa-vanilla", "qa-Fabric", "qa-Forge", "qa-NeoForge" })
        {
            var version = versions.Single(x => x.Name == name && x.Error is null);
            var account = new OfflineAccount(name is "qa-Fabric" or "qa-NeoForge" ? "Alex" : "Steve");
            using (var process = await GameLaunchService.BuildAsync(root, version, settings.Isolation, account))
                Check(process.StartInfo.Arguments.Contains(account.Name) && process.StartInfo.Arguments.Contains(account.Uuid), name + "实际账户参数");
            var session = new GameLaunchSession();
            var stages = new List<LaunchStage>();
            session.Changed += (_, _) =>
            {
                lock (stages)
                    if (stages.Count == 0 || stages[^1] != session.Status.Stage)
                    { stages.Add(session.Status.Stage); Console.WriteLine(name + " " + session.Status.Message); }
            };
            var running = Task.Run(() => session.RunAsync(root, version, settings, account));
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                while (!running.IsCompleted && session.Status.Stage != LaunchStage.Running) await Task.Delay(200, timeout.Token);
                Check(session.Status.Stage == LaunchStage.Running, name + "真实可见游戏窗口：" + session.Status.Message);
                await Task.Delay(12000, timeout.Token);
                Check(!running.IsCompleted, name + "窗口出现后持续运行");
            }
            finally { session.Stop(); await running.WaitAsync(TimeSpan.FromSeconds(20)); }
            Check(session.Status.Message == "已停止运行", name + "停止并等待真实进程退出");
            Check(new[] { LaunchStage.CheckingAssets, LaunchStage.CheckingDependencies, LaunchStage.LoggingIn, LaunchStage.WaitingForWindow, LaunchStage.Running }.All(stages.Contains), name + "完整阶段");
        }
        var vanilla = versions.Single(x => x.Name == "qa-vanilla");
        foreach (var stage in new[] { LaunchStage.CheckingAssets, LaunchStage.CheckingDependencies, LaunchStage.LoggingIn, LaunchStage.WaitingForWindow })
        {
            var session = new GameLaunchSession();
            session.Changed += (_, _) => { if (session.Status.Stage == stage) session.Stop(); };
            await Task.Run(() => session.RunAsync(root, vanilla, settings, new("CancelTest"))).WaitAsync(TimeSpan.FromMinutes(2));
            Check(session.Status.Message == "已停止启动", stage + "真实取消");
            Console.WriteLine(stage + " 取消通过");
        }
        Console.WriteLine($"通过 {_checks} 项实机启动检查。");
    }

    /// <summary>只在独立验收版本内损坏一个小资源和一个库，验证启动前按哈希修复；随后封闭测试进程的网络以验证暖缓存检查，最终恢复原字节。</summary>
    private static async Task VerifyRepairAsync(string root, InstalledVersion version, DownloadSettings settings)
    {
        using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(version.DirectoryPath, version.Name + ".json")));
        string indexId = profile.RootElement.GetProperty("assetIndex").GetProperty("id").GetString()!;
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "assets", "indexes", indexId + ".json")));
        string assetHash = index.RootElement.GetProperty("objects").EnumerateObject().Select(x => x.Value)
            .First(x => x.GetProperty("size").GetInt64() is > 0 and < 1000).GetProperty("hash").GetString()!;
        var library = profile.RootElement.GetProperty("libraries").EnumerateArray()
            .Where(x => x.TryGetProperty("downloads", out var d) && d.TryGetProperty("artifact", out _))
            .Select(x => x.GetProperty("downloads").GetProperty("artifact"))
            .Where(x => x.TryGetProperty("sha1", out _) && x.GetProperty("size").GetInt64() > 0)
            .OrderBy(x => x.GetProperty("size").GetInt64()).First();
        var files = new[]
        {
            (Path: Path.Combine(root, "assets", "objects", assetHash[..2], assetHash), Hash: assetHash),
            (Path: Path.Combine(root, "libraries", library.GetProperty("path").GetString()!), Hash: library.GetProperty("sha1").GetString()!)
        };
        foreach (var file in files)
        {
            if (!Path.GetFullPath(file.Path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("验收文件越界。");
            GameInstallationService.EnsureNoLinks(file.Path);
        }
        var originals = files.Select(x => File.ReadAllBytes(x.Path)).ToArray();
        try
        {
            foreach (var file in files) await File.WriteAllTextAsync(file.Path, "deliberately-corrupt-test-file");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await GameLaunchService.PrepareAsync(root, version, settings, (_, _) => { }, timeout.Token);
            foreach (var file in files)
                Check(Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(file.Path))) == file.Hash, "真实哈希修复 " + Path.GetFileName(file.Path));
            var originalProxy = HttpClient.DefaultProxy;
            try
            {
                HttpClient.DefaultProxy = new WebProxy("http://127.0.0.1:1");
                var stages = new HashSet<LaunchStage>();
                await GameLaunchService.PrepareAsync(root, version, settings, (stage, _) => { lock (stages) stages.Add(stage); }, timeout.Token);
                Check(stages.SetEquals([LaunchStage.CheckingAssets, LaunchStage.CheckingDependencies]) &&
                    files.All(file => Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(file.Path))) == file.Hash),
                    "断网暖缓存完成两个检查阶段且资源哈希保持正确");
            }
            finally { HttpClient.DefaultProxy = originalProxy; }
            Console.WriteLine("真实资源/依赖修复及断网暖缓存检查通过");
        }
        finally { for (int i = 0; i < files.Length; i++) await File.WriteAllBytesAsync(files[i].Path, originals[i]); }
    }

    /// <summary>检查具体业务结果，失败终止并携带名称。</summary>
    private static void Check(bool value, string name) { if (!value) throw new Exception("失败：" + name); _checks++; }

    /// <summary>验证操作按预期失败，防止把未执行或错误类型当成通过。</summary>
    private static void Throws<T>(Action action, string name) where T : Exception
    { try { action(); } catch (T) { _checks++; return; } throw new Exception("未抛出异常：" + name); }
}
