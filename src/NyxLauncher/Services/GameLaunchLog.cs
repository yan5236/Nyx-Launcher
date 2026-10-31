using System.Text;

namespace NyxLauncher.Services;

/// <summary>保存单次启动阶段及游戏输出；写入失败只禁用日志，不中断游戏或吞掉原始启动错误。</summary>
internal sealed class GameLaunchLog : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;
    public string? FilePath { get; private set; }
    public string? Error { get; private set; }

    /// <summary>在指定游戏目录的启动缓存中创建唯一 UTF-8 日志，拒绝链接路径；文件不可写时记录原因供失败弹窗显示。</summary>
    public GameLaunchLog(string folder, bool gameOutput = false)
    {
        try
        {
            string directory = Path.Combine(folder, ".nyx-launch", "logs");
            GameInstallationService.EnsureNoLinks(directory);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{(gameOutput ? "game" : "launch")}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            FilePath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Error = "无法保存启动日志：" + ex.Message; }
    }

    /// <summary>串行保存已经脱敏的阶段或输出；磁盘故障保留原路径和错误，不向进程输出线程抛异常。</summary>
    public void Write(string text)
    {
        lock (_gate)
        {
            try { _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Error = "启动日志写入失败：" + ex.Message; Close(); }
        }
    }

    /// <summary>结束会话后关闭日志；与仍在排空的输出回调串行，重复调用安全。</summary>
    public void Dispose() { lock (_gate) Close(); }

    /// <summary>在持锁状态释放文件；刷新失败保留错误提示并清除写入器，防止后续回调写入已关闭文件。</summary>
    private void Close()
    {
        try { _writer?.Dispose(); }
        catch (IOException ex) { Error = "启动日志关闭失败：" + ex.Message; }
        finally { _writer = null; }
    }
}
