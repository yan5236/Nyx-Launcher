using System.Diagnostics;
using CmlLib.Core.Auth;

namespace NyxLauncher.Services;

public enum LaunchStage { Idle, CheckingAssets, CheckingDependencies, LoggingIn, WaitingForWindow, Running, Stopping, Exited, Failed }
public sealed record LaunchStatus(LaunchStage Stage, string Message)
{
    // 阶段里程碑不代表下载字节比例；窗口出现前始终保留最后一段。
    public double Progress => Stage switch
    {
        LaunchStage.CheckingAssets => 10, LaunchStage.CheckingDependencies => 45,
        LaunchStage.LoggingIn => 75, LaunchStage.WaitingForWindow => 94, LaunchStage.Running => 100, _ => 0
    };
}

/// <summary>拥有一次完整启动及游戏进程生命周期；取消覆盖准备、进程创建竞态、等待窗口和运行阶段。</summary>
public sealed class GameLaunchSession
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Queue<string> _output = new();
    private int _started;
    private volatile LaunchStatus _status = new(LaunchStage.Idle, "");
    private readonly object _stateLock = new();
    private long _lastProgress;
    private string? _accessToken;
    private GameLaunchLog? _log;
    public string? LogPath => _log?.FilePath;
    public string? LogError => _log?.Error;
    public LaunchStatus Status => _status;
    public event EventHandler? Changed;

    /// <summary>请求停止本次启动；重复请求无副作用，不取消其他下载或其他游戏进程。</summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            if (_status.Stage is LaunchStage.Exited or LaunchStage.Failed or LaunchStage.Stopping) return;
            _cancellation.Cancel();
            Set(LaunchStage.Stopping, "正在停止…");
        }
    }

    /// <summary>创建独立日志并后台检查资源，在调用上下文认证及保存账户；取消覆盖网络与进程，最终关闭日志及回收句柄。</summary>
    public async Task RunAsync(string folder, InstalledVersion version, DownloadSettings settings, GameAccount account,
        Func<GameAccount, CancellationToken, Task<MSession>>? authenticate = null)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("启动会话不能重复运行。");
        Process? process = null;
        bool started = false;
        bool appeared = false;
        var token = _cancellation.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            _log = new GameLaunchLog(folder);
            _log.Write("启动版本：" + version.Name);
            await Task.Run(() => GameLaunchService.PrepareAsync(folder, version, settings, Report, token), token);
            token.ThrowIfCancellationRequested();
            Report(LaunchStage.LoggingIn, "登录账户 · " + account.Name + "（" + account.TypeLabel + "）");
            var identity = authenticate is null ? GameLaunchService.CreateSession(account) : await authenticate(account, token);
            _accessToken = identity.AccessToken;
            token.ThrowIfCancellationRequested();
            process = await GameLaunchService.BuildAsync(folder, version, settings.Isolation, account, token, identity);
            token.ThrowIfCancellationRequested();
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.OutputDataReceived += CaptureOutput;
            process.ErrorDataReceived += CaptureOutput;
            started = process.Start();
            if (!started) throw new IOException("系统未能创建游戏进程。");
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            Report(LaunchStage.WaitingForWindow, "等待游戏窗口出现");
            while (!process.HasExited)
            {
                token.ThrowIfCancellationRequested();
                if (!appeared && GameWindow.HasVisibleWindow(process.Id))
                {
                    appeared = true;
                    Report(LaunchStage.Running, "正在运行 · " + version.Name + " · " + identity.Username);
                }
                await Task.Delay(200, token);
            }
            process.WaitForExit();
            if (!appeared || process.ExitCode != 0)
                throw new IOException($"游戏{(appeared ? "异常退出" : "在窗口出现前退出")}（退出码 {process.ExitCode}）。\n" + RecentOutput());
            Set(LaunchStage.Exited, "游戏已退出");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            try
            {
                if (started) await EndProcessAsync(process!, appeared);
                Set(LaunchStage.Exited, appeared ? "已停止运行" : "已停止启动");
            }
            catch (Exception ex) { Set(LaunchStage.Failed, "停止游戏失败：" + ex.Message); }
        }
        catch (Exception ex)
        {
            string message = Redact(ex.Message, _accessToken);
            try { if (started) await EndProcessAsync(process!, false); }
            catch (Exception stop) { message += "\n停止游戏失败：" + stop.Message; }
            Set(LaunchStage.Failed, message);
        }
        finally { process?.Dispose(); _log?.Dispose(); _cancellation.Dispose(); _accessToken = null; }
    }

    /// <summary>先向运行窗口发送正常关闭，三秒后仍未退出才结束整个子进程树；准备阶段直接终止，完成后等待系统确认退出。</summary>
    private static async Task EndProcessAsync(Process process, bool graceful)
    {
        if (process.HasExited) return;
        if (graceful && process.CloseMainWindow())
            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(3000));
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }

    /// <summary>先删除游戏访问令牌，再保存完整输出及内存中的末尾摘要；持续排空两个管道防止游戏挂起。</summary>
    private void CaptureOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        string text = Redact(e.Data, _accessToken);
        _log?.Write(text);
        lock (_output) { _output.Enqueue(text.Length > 1000 ? text[..1000] : text); while (_output.Count > 12) _output.Dequeue(); }
    }

    /// <summary>在截断或展示日志前抹除本次会话令牌；空令牌不执行替换。</summary>
    internal static string Redact(string text, string? accessToken) =>
        string.IsNullOrEmpty(accessToken) ? text : text.Replace(accessToken, "[已隐藏令牌]", StringComparison.Ordinal);

    /// <summary>在锁内快照最近日志，避免 UI 读取时与输出回调竞争。</summary>
    private string RecentOutput() { lock (_output) return string.Join('\n', _output); }

    /// <summary>取消后拒绝迟到状态；同阶段进度最多每 100 毫秒通知，避免数千文件校验淹没 UI 队列。</summary>
    private void Report(LaunchStage stage, string message)
    {
        lock (_stateLock)
        {
            if (_cancellation.IsCancellationRequested) return;
            long now = Environment.TickCount64;
            if (_status.Stage == stage && now - _lastProgress < 100) return;
            _lastProgress = now;
            Set(stage, message);
        }
    }

    /// <summary>发布不可变状态并写入脱敏日志，防止退出和停止竞争时留下停止中状态；界面订阅者负责线程调度。</summary>
    private void Set(LaunchStage stage, string message)
    {
        lock (_stateLock) _status = new(stage, message);
        _log?.Write(Redact(message, _accessToken));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
