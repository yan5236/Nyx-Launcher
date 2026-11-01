using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace NyxLauncher.Services;

/// <summary>收集本轮游戏及启动诊断日志并给出保守诊断；不读取存档或账户配置。</summary>
public sealed class GameCrashReport
{
    public Dictionary<string, string> Logs { get; } = new();
    public string? PrimaryPath { get; private set; }
    public string Diagnosis { get; private set; } = "";
    public string Warning { get; private set; } = "";

    /// <summary>收集本轮游戏日志及启动诊断，确保进程创建前的失败也可查看与导出；单文件限制 8 MiB，读取失败保留警告。</summary>
    public static GameCrashReport Collect(GameLaunchSession session)
    {
        var report = Collect(session.GameDirectory, session.GameOutputPath, session.StartedUtc);
        if (session.LogPath is not null) report.Read(session.LogPath, "launcher.log", DateTime.MinValue);
        return report;
    }

    /// <summary>按固定目录和启动时间收集快照，便于隔离验证旧报告排除和日志导出。</summary>
    internal static GameCrashReport Collect(string? directory, string? output, DateTime startedUtc)
    {
        var report = new GameCrashReport();
        if (directory is not null)
        {
            report.Read(Path.Combine(directory, "logs", "latest.log"), "logs/latest.log", startedUtc);
            report.Read(Path.Combine(directory, "logs", "debug.log"), "logs/debug.log", startedUtc);
            report.ReadDirectory(Path.Combine(directory, "crash-reports"), "crash-*.txt", "crash-reports", startedUtc);
            report.ReadDirectory(directory, "hs_err_pid*.log", "jvm", startedUtc);
        }
        if (output is not null) report.Read(output, "game-output.log", DateTime.MinValue);
        report.Diagnosis = Diagnose(string.Join('\n', report.Logs.Values));
        return report;
    }

    /// <summary>从指定目录收集最新的最多五个本轮报告；目录不可访问时返回明确警告。</summary>
    private void ReadDirectory(string directory, string pattern, string prefix, DateTime since)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            GameInstallationService.EnsureNoLinks(directory);
            foreach (string path in Directory.EnumerateFiles(directory, pattern).OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
                Read(path, prefix + "/" + Path.GetFileName(path), since);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warning += "无法收集部分游戏日志：" + ex.Message + "\n"; }
    }

    /// <summary>读取共享打开的 UTF-8 日志并脱敏；超限文件保留头尾，避免无界内存占用，原始文件不被修改。</summary>
    private void Read(string path, string name, DateTime since)
    {
        try
        {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < since) return;
            GameInstallationService.EnsureNoLinks(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int limit = 4 * 1024 * 1024;
            byte[] bytes = new byte[(int)Math.Min(stream.Length, limit)];
            stream.ReadExactly(bytes);
            string text = Encoding.UTF8.GetString(bytes);
            if (stream.Length > limit)
            {
                stream.Seek(Math.Max(limit, stream.Length - limit), SeekOrigin.Begin);
                bytes = new byte[(int)Math.Min(stream.Length - stream.Position, limit)];
                stream.ReadExactly(bytes);
                text += "\n[大日志仅保留头尾]\n" + Encoding.UTF8.GetString(bytes);
            }
            Logs[name] = Sanitize(text);
            PrimaryPath ??= path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warning += "无法读取 " + name + "：" + ex.Message + "\n"; }
    }

    /// <summary>移除常见认证参数与 Bearer 凭据，供导出快照使用；不改变磁盘上的游戏原始日志。</summary>
    internal static string Sanitize(string text) => Regex.Replace(text,
        @"(?i)(\b(?:accessToken|access_token|clientToken|client_token|Authorization|Bearer)\b[\s="":]+)(?:Bearer\s+)?([^\s,""}]+)", "$1[已隐藏令牌]");

    /// <summary>按明确错误特征区分内存、图形、Java 和模组问题；无证据时返回未知，不用退出码猜具体原因。</summary>
    internal static string Diagnose(string text)
    {
        bool Has(params string[] patterns) => patterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (Has("OutOfMemoryError: Java heap space", "OutOfMemoryError: GC overhead", "OutOfMemoryError: Metaspace"))
            return "可能的原因：Java 可用内存不足。\n建议操作：在物理内存允许的范围内提高游戏内存上限，关闭后台程序，或减少模组与资源包。";
        if (Has("Native memory allocation (malloc) failed", "Could not reserve enough space", "Out of swap space", "OutOfMemoryError"))
            return "可能的原因：系统或 Java 内存不足。\n建议操作：关闭占用内存的程序，检查系统可用内存和分页文件；若游戏分配过高，请降低内存上限。";
        if (Has("GLFW error 65542", "GLFW error 65543", "does not support OpenGL", "Failed to create window", "EXCEPTION_ACCESS_VIOLATION") &&
            Has("OpenGL", "GLFW", "atio", "nvoglv", "igxel", "ig7icd"))
            return "可能的原因：显卡驱动或 OpenGL 初始化异常。\n建议操作：从显卡厂商更新或重装驱动，确认使用支持该游戏版本的显卡；尝试关闭光影。";
        if (Has("UnsupportedClassVersionError", "compiled by a more recent version of the Java Runtime"))
            return "可能的原因：Java 版本与游戏或模组不兼容。\n建议操作：在 Java 设置中选择游戏和模组要求的 Java 版本。";
        string? modError = ExtractModError(text);
        if (modError is not null)
            return "可能的原因：模组版本冲突、缺少依赖或模组加载失败。\n建议操作：按下方加载器原文核对模组与前置依赖的名称、要求版本和当前版本，补齐依赖或更换兼容版本。\n\n加载器原始错误：\n" + modError;
        return "可能的原因：暂未识别到明确原因。\n建议操作：查看游戏日志中的错误信息，或导出日志交给他人分析。";
    }

    /// <summary>定位 Fabric、Forge、NeoForge 等加载器错误并保留原始多行上下文，不翻译或重写模组 ID 与版本范围；超长原文明确提示从 ZIP 查看。</summary>
    private static string? ExtractModError(string text)
    {
        string[] markers = ["Incompatible mods found", "Mod resolution encountered an incompatible mod set",
            "Missing or unsupported mandatory dependencies", "Missing mandatory dependencies", "Missing Mods:",
            "ModLoadingException", "ModLoadingIssue", "LoadingFailedException", "Mod resolution failed",
            "MixinApplyError", "MixinTransformerError", "DuplicateModsFoundException", "Duplicate mods found",
            "Failure message:", "requires version", "requires any version", "requires a version"];
        int start = markers.Select(marker => text.IndexOf(marker, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0).DefaultIfEmpty(-1).Min();
        if (start < 0) return null;
        start = text.LastIndexOf('\n', start) + 1;
        const int limit = 24000;
        string excerpt = text.Substring(start, Math.Min(text.Length - start, limit));
        return excerpt + (start + excerpt.Length < text.Length ? "\n[原文较长，完整内容请查看导出的游戏日志]" : "");
    }

    /// <summary>将已收集的脱敏快照和诊断写入 ZIP；先完成同目录临时文件，再替换用户选择的目标，失败不破坏已有文件。</summary>
    public void Export(string destination)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var log in Logs.Append(new KeyValuePair<string, string>("诊断.txt", Diagnosis + "\n" + Warning)))
                {
                    using var writer = new StreamWriter(archive.CreateEntry(log.Key).Open(), new UTF8Encoding(false));
                    writer.Write(log.Value);
                }
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
