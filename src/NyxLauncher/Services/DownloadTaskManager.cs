using System.ComponentModel;

namespace NyxLauncher.Services;

public enum DownloadState { Queued, Running, Pausing, Paused, Cancelling, Cancelled, Failed, Completed }

/// <summary>下载任务的可观察状态；后台更新通过主线程调度后通知绑定。</summary>
public sealed class DownloadTaskItem : INotifyPropertyChanged
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public required string Destination { get; init; }
    public bool IsGame { get; init; }
    public DownloadState State { get; internal set; } = DownloadState.Queued;
    public string Detail { get; internal set; } = "等待下载";
    public double Percent { get; internal set; }
    public string PercentText => $"{Percent:0}%";
    public string Glyph => IsGame ? "\uE7FC" : "\uE896";
    public string PauseLabel => State is DownloadState.Paused or DownloadState.Failed ? "继续" : "暂停";
    public string PauseGlyph => State is DownloadState.Paused or DownloadState.Failed ? "\uE768" : "\uE769";
    public bool CanPause => State is DownloadState.Running or DownloadState.Queued or DownloadState.Paused or DownloadState.Failed;
    public bool CanCancel => State is DownloadState.Running or DownloadState.Queued or DownloadState.Paused or DownloadState.Failed;
    public bool CanRemove => (State is DownloadState.Completed or DownloadState.Cancelled) && Cancellation is null;
    public bool CanDismiss => CanCancel || CanRemove;
    public string DismissLabel => State is DownloadState.Completed or DownloadState.Cancelled ? "删除下载记录" : "取消下载";
    public string DismissGlyph => State is DownloadState.Completed or DownloadState.Cancelled ? "\uE74D" : "\uE711";
    public bool IsBusy => State is DownloadState.Running or DownloadState.Queued or DownloadState.Pausing or DownloadState.Cancelling;
    internal CancellationTokenSource? Cancellation;
    internal Func<DownloadTransport, CancellationToken, Task>? Work;
    internal Action? Cleanup;
    internal Task Execution = Task.CompletedTask;
    internal long LastProgress;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>一次刷新所有派生状态，确保进度、按钮与筛选同步；由管理器在界面线程调用。</summary>
    internal void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    /// <summary>辅助技术读取真实任务名称和状态，避免朗读内部类型名称。</summary>
    public override string ToString() => Name + "，" + Detail;
}

/// <summary>拥有后台任务生命周期，页面只订阅状态；暂停取消当前请求，继续复用已经校验的游戏文件。</summary>
public sealed class DownloadTaskManager
{
    private readonly DownloadSettingsStore _settings;
    private readonly Action<Action> _dispatch;
    private DownloadTransport? _transport;
    public List<DownloadTaskItem> Tasks { get; } = [];
    public event EventHandler? Changed;
    public event EventHandler? TaskAdded;
    public event EventHandler? InstallationCompleted;

    /// <summary>注入共享设置和 UI 调度器；测试可使用同步调度，不依赖 WinUI。</summary>
    public DownloadTaskManager(DownloadSettingsStore settings, Action<Action> dispatch)
    { _settings = settings; _dispatch = dispatch; }

    /// <summary>校验链接、目录及文件名后建立普通任务；不覆盖已有文件，不把链接查询参数写入文件名。</summary>
    public DownloadTaskItem AddFile(string url, string folder)
    {
        var uri = DownloadTransport.ValidateUrl(url.Trim());
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) throw new IOException("请选择可访问的下载目录。");
        string name = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last());
        GameInstallationService.ValidateName(name);
        string path = Path.Combine(folder, name);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("目标名称已存在，请选择其他目录。");
        var item = new DownloadTaskItem { Name = name, Destination = path };
        item.Work = (transport, token) => transport.DownloadAsync(uri, path, null, 0, false, true,
            (done, total) => Report(item, total > 0 ? done * 100d / total : 0,
                $"下载文件 · {done / 1048576d:0.0} MiB" + (total > 0 ? $" / {total / 1048576d:0.0} MiB" : "")), token);
        Add(item);
        return item;
    }

    /// <summary>锁定创建时的游戏目录、名称和加载器选择，先验证冲突；任务创建成功立即返回，不等待网络。</summary>
    public DownloadTaskItem AddGame(string folder, string name, string game, LoaderVersion? loader)
    {
        GameInstallationService.ValidateName(name);
        if (!Directory.Exists(folder)) throw new IOException("请先在版本管理中选择有效的游戏目录。");
        string destination = Path.Combine(folder, "versions", name);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("该版本名称已存在，请换一个名称。");
        var item = new DownloadTaskItem { Name = name, Destination = destination, IsGame = true };
        var settings = _settings.Current;
        var installation = new GameInstallationService(folder, item.Id, settings);
        item.Work = (transport, token) => installation.InstallAsync(name, game, loader, transport,
            (percent, detail) => Report(item, percent, detail), token);
        item.Cleanup = installation.Cleanup;
        Add(item);
        return item;
    }

    /// <summary>阻止同目标的重复任务，加入列表并通知新任务提示，再交给启动入口应用传输预算。</summary>
    private void Add(DownloadTaskItem item)
    {
        if (Tasks.Any(x => x.State is not (DownloadState.Completed or DownloadState.Cancelled) &&
            string.Equals(x.Destination, item.Destination, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("相同目标已经有下载任务。");
        Tasks.Insert(0, item);
        TaskAdded?.Invoke(this, EventArgs.Empty);
        Start(item);
    }

    /// <summary>启动或继续任务；没有其他活跃任务时采用最新并行、连接和限速设置，否则沿用当前批次预算。</summary>
    private void Start(DownloadTaskItem item)
    {
        if (!Tasks.Any(x => x != item && x.IsBusy)) { _transport?.Dispose(); _transport = new(_settings.Current); }
        item.Cancellation = new();
        item.State = DownloadState.Queued;
        item.Detail = "等待下载";
        Notify(item);
        item.Execution = RunAsync(item, item.Cancellation);
    }

    /// <summary>在线程池执行下载并等待清理；只有工作真实完成才标为成功，取消后不允许迟到进度覆盖状态。</summary>
    private async Task RunAsync(DownloadTaskItem item, CancellationTokenSource cancellation)
    {
        try
        {
            item.State = DownloadState.Running;
            Notify(item);
            await Task.Run(() => item.Work!(_transport!, cancellation.Token));
            item.State = DownloadState.Completed;
            item.Percent = 100;
            item.Detail = "已完成";
            if (item.IsGame) _dispatch(() => InstallationCompleted?.Invoke(this, EventArgs.Empty));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            bool paused = item.State == DownloadState.Pausing;
            item.State = paused ? DownloadState.Paused : DownloadState.Cancelled;
            item.Detail = paused ? "已暂停" : "已取消";
        }
        catch (Exception ex)
        {
            item.State = cancellation.IsCancellationRequested ?
                (item.State == DownloadState.Pausing ? DownloadState.Paused : DownloadState.Cancelled) : DownloadState.Failed;
            item.Detail = item.State == DownloadState.Failed ? "失败：" + ex.Message :
                item.State == DownloadState.Paused ? "已暂停" : "已取消";
        }
        finally
        {
            cancellation.Dispose();
            if (item.State is DownloadState.Cancelled or DownloadState.Completed)
            {
                try { item.Cleanup?.Invoke(); }
                catch (Exception ex) { item.Detail += "；暂存清理失败：" + ex.Message; }
            }
            item.Cancellation = null;
            Notify(item);
        }
    }

    /// <summary>运行中暂停会取消网络与处理器，清理完成后才能继续；失败任务可用同一入口重试。</summary>
    public void PauseOrResume(DownloadTaskItem item)
    {
        if (item.State is DownloadState.Paused or DownloadState.Failed) { Start(item); return; }
        if (!item.CanPause || item.Cancellation is null) return;
        item.State = DownloadState.Pausing;
        item.Detail = "正在暂停…";
        item.Cancellation.Cancel();
        Notify(item);
    }

    /// <summary>取消活跃任务或移除暂停任务的暂存文件；不删除已经完成的目标，也不删除共享游戏资源。</summary>
    public void Cancel(DownloadTaskItem item)
    {
        if (!item.CanCancel) return;
        if (item.Cancellation is not null)
        { item.State = DownloadState.Cancelling; item.Detail = "正在取消…"; item.Cancellation.Cancel(); }
        else
        {
            item.State = DownloadState.Cancelled;
            item.Detail = "已取消";
            try { item.Cleanup?.Invoke(); } catch (Exception ex) { item.Detail += "；暂存清理失败：" + ex.Message; }
        }
        Notify(item);
    }

    /// <summary>移除已完成或取消清理结束的任务记录；不重复清理、不删除下载目标及共享资源，活跃任务不可移除。</summary>
    public void RemoveFinished(DownloadTaskItem item)
    {
        if (!item.CanRemove) return;
        Tasks.Remove(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>更新百分比和阶段；UI 调度时再次检查状态，禁止完成、暂停或取消后的迟到回调修改任务。</summary>
    private void Report(DownloadTaskItem item, double percent, string detail)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref item.LastProgress) < 100) return;
        Interlocked.Exchange(ref item.LastProgress, now);
        _dispatch(() =>
        {
            if (item.State != DownloadState.Running) return;
            item.Percent = Math.Clamp(percent, 0, 99.9);
            item.Detail = detail;
            item.Notify();
        });
    }

    /// <summary>关闭应用时先取消任务，再等待处理器和临时文件清理结束；避免退出后遗留后台安装进程。</summary>
    public async Task ShutdownAsync()
    {
        foreach (var item in Tasks)
        {
            if (item.State == DownloadState.Pausing) item.State = DownloadState.Cancelling;
            else Cancel(item);
        }
        await Task.WhenAll(Tasks.Select(x => x.Execution));
        _transport?.Dispose();
    }

    /// <summary>将状态变化投递给界面，同时通知筛选器重新计算可见项。</summary>
    private void Notify(DownloadTaskItem item) => _dispatch(() => { item.Notify(); Changed?.Invoke(this, EventArgs.Empty); });
}
