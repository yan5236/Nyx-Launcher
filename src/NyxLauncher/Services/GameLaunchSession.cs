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
    private int _started;
    private volatile LaunchStatus _status = new(LaunchStage.Idle, "");
    private readonly object _stateLock = new();
    private long _lastProgress;
    private string? _accessToken;
    private GameLaunchLog? _log;
    private GameLaunchLog? _gameLog;
    private bool _canDetach;
    public string? LogPath => _log?.FilePath;
    public string? LogError => _log?.Error;
    public bool Crashed { get; private set; }
    public bool IsDetached { get; private set; }
    public string? GameDirectory { get; private set; }
    public DateTime StartedUtc { get; private set; }
    public string? GameOutputPath => _gameLog?.FilePath;
    public LaunchStatus Status => _status;
    public event EventHandler? Changed;

    /// <summary>取消本次会话；托盘退出时可仅解除对已就绪且未重定向输出的游戏进程的监控。</summary>
    public void Stop(bool leaveGameRunning = false)
    {
        lock (_stateLock)
        {
            if (IsDetached || _status.Stage is LaunchStage.Exited or LaunchStage.Failed or LaunchStage.Stopping) return;
            IsDetached = leaveGameRunning && _canDetach && _status.Stage == LaunchStage.Running;
            if (!IsDetached) Set(LaunchStage.Stopping, "正在停止…");
            _cancellation.Cancel();
        }
    }

    /// <summary>准备并启动游戏，等待真实窗口就绪；按本次偏好释放进程控制权或等待退出，最终回收句柄。</summary>
    public async Task RunAsync(string folder, InstalledVersion version, DownloadSettings settings, GameAccount account,
        Func<GameAccount, CancellationToken, Task<MSession>>? authenticate = null, JavaSelection? java = null, ServerEndpoint? server = null)
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
            var advanced = AdvancedLaunchSettingsStore.Read(version);
            string? javaPath = await Task.Run(() => GameLaunchService.PrepareAsync(folder, version, settings, Report, token, java), token);
            if (javaPath is not null) _log.Write("Java：" + javaPath);
            token.ThrowIfCancellationRequested();
            Report(LaunchStage.LoggingIn, "登录账户 · " + account.Name + "（" + account.TypeLabel + "）");
            var identity = authenticate is null ? GameLaunchService.CreateSession(account) : await authenticate(account, token);
            _accessToken = identity.AccessToken;
            token.ThrowIfCancellationRequested();
            process = await GameLaunchService.BuildAsync(folder, version, settings.Isolation, account, token, identity, javaPath, advanced, server);
            GameDirectory = process.StartInfo.WorkingDirectory;
            StartedUtc = DateTime.UtcNow;
            bool captureOutput = settings.AfterLaunch == LauncherAfterLaunch.KeepOpen;
            _canDetach = !captureOutput;
            token.ThrowIfCancellationRequested();
            // 可独立运行的游戏不依赖启动器排空输出管道，诊断从游戏自身的日志与崩溃文件读取。
            if (captureOutput)
            {
                _gameLog = new GameLaunchLog(folder, gameOutput: true);
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.OutputDataReceived += CaptureOutput;
                process.ErrorDataReceived += CaptureOutput;
            }
            LaunchCompatibility.ConfigureGpu(process.StartInfo.FileName, advanced.HighPerformanceGpu);
            token.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started) throw new IOException("系统未能创建游戏进程。");
            if (captureOutput) { process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
            Report(LaunchStage.WaitingForWindow, "等待游戏窗口出现");
            while (!process.HasExited)
            {
                token.ThrowIfCancellationRequested();
                if (GameWindow.HasVisibleWindow(process.Id))
                {
                    lock (_stateLock)
                    {
                        token.ThrowIfCancellationRequested();
                        appeared = true;
                        IsDetached = settings.AfterLaunch == LauncherAfterLaunch.Close;
                        Set(LaunchStage.Running, "正在运行 · " + version.Name + " · " + identity.Username);
                    }
                    if (IsDetached) return;
                    break;
                }
                await Task.Delay(200, token);
            }
            await process.WaitForExitAsync(token);
            token.ThrowIfCancellationRequested();
            if (!appeared || process.ExitCode != 0)
            {
                Crashed = process.ExitCode != 0;
                throw new IOException($"游戏{(Crashed ? "发生了崩溃" : "在窗口出现前退出")}（退出码 {process.ExitCode}）。");
            }
            Set(LaunchStage.Exited, "游戏已退出");
        }
        catch (OperationCanceledException) when (IsDetached) { }
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
            _log?.Write("失败阶段：" + Status.Stage + "\n" + NetworkFailureDetails.Format(ex));
            string message = Redact(ex.Message, _accessToken);
            try { if (started && !IsDetached) await EndProcessAsync(process!, false); }
            catch (Exception stop) { message += "\n停止游戏失败：" + stop.Message; }
            Set(LaunchStage.Failed, message);
        }
        finally { process?.Dispose(); _gameLog?.Dispose(); _log?.Dispose(); _cancellation.Dispose(); _accessToken = null; }
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

    /// <summary>先删除游戏访问令牌，再分别保存启动记录与纯游戏输出；持续排空两个管道防止游戏挂起。</summary>
    private void CaptureOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        string text = Redact(e.Data, _accessToken);
        _log?.Write(text);
        _gameLog?.Write(text);
    }

    /// <summary>在截断或展示日志前抹除本次会话令牌；空令牌不执行替换。</summary>
    internal static string Redact(string text, string? accessToken) =>
        string.IsNullOrEmpty(accessToken) ? text : text.Replace(accessToken, "[已隐藏令牌]", StringComparison.Ordinal);

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
