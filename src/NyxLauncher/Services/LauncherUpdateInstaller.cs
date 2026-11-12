using System.Diagnostics;
using System.Security.Cryptography;

namespace NyxLauncher.Services;

/// <summary>下载并替换单文件启动器；旧进程继续运行，保留备份供回滚，下次启动再清理。</summary>
public sealed class LauncherUpdateInstaller
{
    private readonly LauncherRelease _release;
    private readonly string _executable;
    private readonly string _staging;
    private readonly string _backup;
    private bool _published;

    /// <summary>固定当前 EXE 及相邻暂存/备份路径，拒绝链接目标；调用方必须先确认此构建可单文件更新。</summary>
    public LauncherUpdateInstaller(LauncherRelease release, string executable)
    {
        _release = release;
        _executable = Path.GetFullPath(executable);
        _staging = _executable + ".update";
        _backup = _executable + ".previous";
        GameInstallationService.EnsureNoLinks(_executable);
    }

    /// <summary>持有跨进程更新锁，共享下载预算后验证长度与 SHA-256；替换前再次检查版本和取消，提交阶段不退出当前进程。</summary>
    public async Task InstallAsync(DownloadTransport transport, Action<double, string> report, CancellationToken token)
    {
        if (_published) return;
        using var updateLock = new FileStream(_executable + ".update-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (string path in new[] { _executable, _staging, _backup }) GameInstallationService.EnsureNoLinks(path);
        string? installed = FileVersionInfo.GetVersionInfo(_executable).ProductVersion;
        if (!string.IsNullOrWhiteSpace(installed) && ReleaseVersion.Parse(installed).CompareTo(ReleaseVersion.Parse(_release.Version)) >= 0)
            throw new IOException("磁盘上的启动器已是此版本或更新版本，请下次启动后再检查。");
        if (!await IsValidAsync(_staging, token))
        {
            if (File.Exists(_staging)) File.Delete(_staging);
            await transport.DownloadAsync(_release.Download, _staging, null, _release.Size, false, true,
                (done, total) => report(total > 0 ? done * 95d / total : 0, $"下载启动器 · {done / 1048576d:0.0} / {total / 1048576d:0.0} MiB"), token);
        }
        report(96, "校验更新文件");
        if (!await IsValidAsync(_staging, token))
        { File.Delete(_staging); throw new IOException("更新文件 SHA-256 或大小不符，已保留当前版本，请重试。"); }
        token.ThrowIfCancellationRequested();
        ReplaceExecutable(_staging, _executable, _backup);
        _published = true;
    }

    /// <summary>仅接受与发布元数据完全匹配的文件；取消直接抛出，损坏缓存返回假以便重新下载。</summary>
    private async Task<bool> IsValidAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        return stream.Length == _release.Size && Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(_release.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>先将运行中的旧 EXE 改名为备份，再移动同卷新文件；第二步失败恢复旧路径，回滚失败仍保留旧 EXE 备份并报告两个错误。</summary>
    internal static void ReplaceExecutable(string staging, string executable, string backup)
    {
        if (File.Exists(backup)) throw new IOException("上次更新的备份尚未清理，请关闭其他启动器窗口后重新启动再更新。");
        File.Move(executable, backup);
        try { File.Move(staging, executable); }
        catch (Exception failure)
        {
            try { File.Move(backup, executable); }
            catch (Exception rollback) { throw new AggregateException("更新失败且旧路径恢复失败，旧版仍保存在：" + backup, failure, rollback); }
            throw;
        }
    }

    /// <summary>取消或完成时只清理本任务的下载暂存；从不删除当前 EXE 或仍在运行的旧版备份。</summary>
    public void Cleanup()
    {
        using var updateLock = new FileStream(_executable + ".update-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        GameInstallationService.EnsureNoLinks(_staging);
        if (File.Exists(_staging)) File.Delete(_staging);
    }

    /// <summary>新版启动后尝试清理相邻的旧版备份；旧进程仍占用或无权限时保留，启动不因此失败。</summary>
    public static void CleanupBackup(string executable)
    {
        if (!File.Exists(executable + ".previous")) return;
        try
        {
            using var updateLock = new FileStream(executable + ".update-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string backup = executable + ".previous";
            GameInstallationService.EnsureNoLinks(backup);
            if (File.Exists(executable) && File.Exists(backup)) File.Delete(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
