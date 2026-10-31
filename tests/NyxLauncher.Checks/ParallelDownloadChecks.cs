using System.Net;
using System.Security.Cryptography;
using CmlLib.Core.Files;
using NyxLauncher.Services;

internal static class ParallelDownloadChecks
{
    private static int _checks;

    /// <summary>用可控 HTTP 响应验证游戏资源与普通文件共享并行额度、取消释放额度、设置兼容及任务删除；不访问外网。</summary>
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-parallel-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var limits in new[] { (Files: 1, Connections: 8), (Files: 3, Connections: 8), (Files: 8, Connections: 2) })
                await CheckResourcesAsync(root, limits.Files, limits.Connections);
            await CheckCancellationAsync(root);
            await CheckTasksAsync(root);
            await CheckShutdownAsync(root);
            CheckVersionProtection(root);
            CheckSettings(root);
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"通过 {_checks} 项并行下载与取消记录检查。");
    }

    /// <summary>阻塞第一批请求以观察真实并发，释放后校验资源字节、去重和缓存命中，并验证普通文件占用同一预算。</summary>
    private static async Task CheckResourcesAsync(string root, int files, int connections)
    {
        string folder = Path.Combine(root, $"resources-{files}-{connections}");
        int expected = Math.Min(files, connections);
        using var handler = new GatedHandler(expected);
        using var transport = new DownloadTransport(new(MaxConnections: connections, MaxParallelDownloads: files), new HttpClient(handler));
        var installer = new GameDownloadInstaller(transport, root);
        string hash = Convert.ToHexString(SHA1.HashData(GatedHandler.Data));
        var resources = Enumerable.Range(0, 7).Select(i => new GameFile("asset-" + i)
        {
            Path = Path.Combine(folder, "assets", "objects", i.ToString()),
            Url = "https://example.test/assets/" + i,
            Hash = hash,
            Size = GatedHandler.Data.Length
        }).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ordinary = transport.DownloadAsync(new("https://example.test/file"), Path.Combine(folder, "ordinary"),
            hash, GatedHandler.Data.Length, false, false, null, deadline.Token);
        var game = installer.Install(resources.Append(resources[0]), null, null, deadline.Token).AsTask();
        try
        {
            await handler.Filled.Task.WaitAsync(deadline.Token);
            Check(handler.Maximum == expected && handler.Requests == expected, "普通文件与游戏资源共享文件及连接上限");
        }
        finally { handler.Release.TrySetResult(); await Task.WhenAll(ordinary, game); }
        Check(handler.Maximum == expected && handler.Requests == 8, "整批资源按指定并行数下载且相同路径只下载一次");
        Check(resources.All(file => File.ReadAllBytes(file.Path!).SequenceEqual(GatedHandler.Data)), "并行资源全部通过哈希并输出准确字节");
        await installer.Install(resources, null, null, deadline.Token);
        Check(handler.Requests == 8, "完整资源再次安装复用缓存");
    }

    /// <summary>占满唯一文件额度，取消等待任务和正在传输的资源批次，验证不发布半成品且后续下载仍能获得额度。</summary>
    private static async Task CheckCancellationAsync(string root)
    {
        using var handler = new GatedHandler(1);
        using var transport = new DownloadTransport(new(MaxParallelDownloads: 1), new HttpClient(handler));
        using var activeCancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var installer = new GameDownloadInstaller(transport, root);
        string activePath = Path.Combine(root, "cancel-active");
        var active = installer.Install([new GameFile("active")
        {
            Path = activePath, Url = "https://example.test/active", Size = GatedHandler.Data.Length
        }], null, null, activeCancel.Token).AsTask();
        await handler.Filled.Task.WaitAsync(activeCancel.Token);
        using var waitingCancel = new CancellationTokenSource();
        string waitingPath = Path.Combine(root, "cancel-waiting");
        var waiting = transport.DownloadAsync(new("https://example.test/waiting"), waitingPath,
            null, 0, false, false, null, waitingCancel.Token);
        waitingCancel.Cancel();
        await ExpectCancellationAsync(waiting);
        Check(handler.Requests == 1 && !File.Exists(waitingPath), "等待额度的任务取消后不发送请求、不发布文件");
        activeCancel.Cancel();
        await ExpectCancellationAsync(active);
        Check(!File.Exists(activePath) && !Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any(),
            "取消资源批次等待所有工作结束并删除临时文件");
        handler.Release.TrySetResult();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string resumed = Path.Combine(root, "after-cancel");
        await transport.DownloadAsync(new("https://example.test/resumed"), resumed,
            null, 0, false, false, null, deadline.Token);
        Check(File.ReadAllBytes(resumed).SequenceEqual(GatedHandler.Data), "取消后释放文件与连接额度供下一次下载");
    }

    /// <summary>运行真实任务管理器的暂停、继续和取消生命周期，验证批次设置刷新及清理结束后的无损记录删除。</summary>
    private static async Task CheckTasksAsync(string root)
    {
        var settings = new DownloadSettingsStore(Path.Combine(root, "task-settings.json"));
        var manager = new DownloadTaskManager(settings, action => action());
        string retained = Path.Combine(root, "retained-resource");
        File.WriteAllText(retained, "keep");
        int cleanups = 0;
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new DownloadTaskItem
        {
            Name = "cancelled", Destination = retained, State = DownloadState.Paused,
            Work = async (transport, token) =>
            {
                started.TrySetResult(transport.MaxParallelDownloads);
                await Task.Delay(Timeout.Infinite, token);
            },
            Cleanup = () => cleanups++
        };
        manager.Tasks.Add(item);
        try
        {
            Check(settings.Save(settings.Current with { MaxParallelDownloads = 2 }) is null, "保存初始并行上限");
            manager.PauseOrResume(item);
            Check(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)) == 2, "首次继续采纳最新并行上限");
            manager.RemoveFinished(item);
            Check(manager.Tasks.Contains(item), "运行中的任务不可直接移除");
            manager.PauseOrResume(item);
            await item.Execution.WaitAsync(TimeSpan.FromSeconds(5));
            Check(item.State == DownloadState.Paused && cleanups == 0, "暂停保留暂存状态");
            Check(settings.Save(settings.Current with { MaxParallelDownloads = 4 }) is null, "保存新的并行上限");
            started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.PauseOrResume(item);
            Check(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)) == 4, "空闲后继续刷新传输器的并行上限");
            manager.Cancel(item);
            await item.Execution.WaitAsync(TimeSpan.FromSeconds(5));
            Check(item.State == DownloadState.Cancelled && item.CanDismiss && item.DismissLabel == "删除下载记录" &&
                item.DismissGlyph == "\uE74D" && cleanups == 1, "取消清理结束后显示可用删除按钮");
            manager.RemoveFinished(item);
            Check(manager.Tasks.Count == 0 && File.ReadAllText(retained) == "keep" && cleanups == 1,
                "删除取消记录保留资源且不重复清理");
            var cancelling = new DownloadTaskItem { Name = "pending", Destination = retained, State = DownloadState.Cancelling };
            manager.Tasks.Add(cancelling);
            manager.RemoveFinished(cancelling);
            Check(!cancelling.CanDismiss && manager.Tasks.Contains(cancelling), "正在取消的任务禁止删除");
        }
        finally { await manager.ShutdownAsync(); }
    }

    /// <summary>旧 JSON 缺失新字段时保留兼容默认值，新上限可回读且越界保存不破坏现有配置。</summary>
    private static void CheckSettings(string root)
    {
        string path = Path.Combine(root, "legacy-settings.json");
        File.WriteAllText(path, "{\"MaxConnections\":8,\"SpeedLimitMiB\":2}");
        var store = new DownloadSettingsStore(path);
        Check(store.LoadError is null && store.Current.MaxParallelDownloads == 16 && store.Current.MaxConnections == 8,
            "旧配置兼容新并行默认值");
        var saved = store.Current with { MaxParallelDownloads = 3 };
        Check(store.Save(saved) is null && new DownloadSettingsStore(path).Current == saved, "并行限制持久化回读");
        foreach (int invalid in new[] { 0, 65 })
            Check(store.Save(saved with { MaxParallelDownloads = invalid }) is not null && store.Current == saved,
                "拒绝并行上限越界且保留配置");
    }

    /// <summary>逐状态验证游戏和模组共用版本保护；普通文件与 Java 不阻断版本修改，暂停及终态释放保护。</summary>
    private static void CheckVersionProtection(string root)
    {
        var manager = new DownloadTaskManager(new(Path.Combine(root, "protection.json")), action => action());
        foreach (string kind in new[] { "game", "mod", "java", "file" })
        foreach (var state in Enum.GetValues<DownloadState>())
        {
            manager.Tasks.Clear();
            manager.Tasks.Add(new() { Name = kind, Destination = root, IsGame = kind == "game",
                IsMod = kind == "mod", IsJava = kind == "java", State = state });
            bool blocked = (kind is "game" or "mod") &&
                (state is DownloadState.Queued or DownloadState.Running or DownloadState.Pausing or DownloadState.Cancelling);
            Check(manager.HasActiveVersionTasks == blocked, $"{kind}/{state} 的版本修改保护正确");
        }
    }

    /// <summary>延迟取消收尾，验证关闭等待期间及完成后拒绝新任务、忽略恢复、复用关闭任务，并真正等待清理结束。</summary>
    private static async Task CheckShutdownAsync(string root)
    {
        var manager = new DownloadTaskManager(new(Path.Combine(root, "shutdown.json")), action => action());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cleaned = false;
        var item = new DownloadTaskItem
        {
            Name = "shutdown", Destination = Path.Combine(root, "shutdown"), State = DownloadState.Paused,
            Work = async (_, token) => { entered.SetResult(); await release.Task; token.ThrowIfCancellationRequested(); },
            Cleanup = () => cleaned = true
        };
        manager.Tasks.Add(item);
        manager.PauseOrResume(item);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task closing = manager.ShutdownAsync();
            Check(!closing.IsCompleted && !cleaned, "关闭等待正在取消的任务完成收尾");
            Check(ReferenceEquals(closing, manager.ShutdownAsync()), "重复关闭等待同一轮清理");
            try { manager.AddFile("https://example.test/late.jar", root); throw new Exception("关闭期间接受了新任务"); }
            catch (IOException) { Check(manager.Tasks.Count == 1, "关闭期间拒绝新下载且不添加记录"); }
            var late = new DownloadTaskItem { Name = "late", Destination = root, State = DownloadState.Paused,
                Work = (_, _) => throw new Exception("关闭期间恢复了任务") };
            manager.PauseOrResume(late);
            Check(late.State == DownloadState.Paused && late.Cancellation is null, "关闭期间忽略迟到的恢复操作");
            release.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Check(cleaned && item.State == DownloadState.Cancelled && item.Cancellation is null, "关闭返回前完成取消和清理");
            try { manager.AddFile("https://example.test/after.jar", root); throw new Exception("关闭后接受了新任务"); }
            catch (IOException) { Check(manager.Tasks.Count == 1, "关闭完成后仍拒绝新下载"); }
        }
        finally { release.TrySetResult(); await manager.ShutdownAsync(); }
    }

    /// <summary>确认异步操作按取消结束，正常完成或其他异常均使检查失败。</summary>
    private static async Task ExpectCancellationAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { _checks++; return; }
        throw new Exception("取消操作未按取消结束。");
    }

    /// <summary>断言具体结果并累计通过数，失败立即报告对应业务行为。</summary>
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("失败：" + name);
        _checks++;
    }

    private sealed class GatedHandler(int expected) : HttpMessageHandler
    {
        internal static readonly byte[] Data = [1, 3, 5, 7, 9];
        internal readonly TaskCompletionSource Filled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();
        private int _active;
        internal int Maximum;
        internal int Requests;

        /// <summary>累计真实重叠请求数，达到预期并发后通知测试；门闩释放前保持请求活跃并响应取消。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            lock (_sync)
            {
                Requests++;
                Maximum = Math.Max(Maximum, ++_active);
                if (_active == expected) Filled.TrySetResult();
            }
            try
            {
                await Release.Task.WaitAsync(token);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Data) };
            }
            finally { lock (_sync) _active--; }
        }
    }
}
