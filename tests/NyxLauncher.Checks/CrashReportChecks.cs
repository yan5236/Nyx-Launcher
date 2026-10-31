using System.IO.Compression;
using NyxLauncher.Services;

internal static class CrashReportChecks
{
    /// <summary>验证典型诊断、未知回退、旧报告排除、游戏日志优先、脱敏与 ZIP 快照内容；仅操作临时目录。</summary>
    public static void Run()
    {
        Check(GameCrashReport.Diagnose("OutOfMemoryError: Java heap space").Contains("提高"), "堆内存建议");
        Check(GameCrashReport.Diagnose("Native memory allocation (malloc) failed").Contains("降低"), "系统内存建议");
        Check(GameCrashReport.Diagnose("GLFW error 65542 OpenGL").Contains("显卡"), "显卡诊断");
        Check(GameCrashReport.Diagnose("UnsupportedClassVersionError").Contains("Java 版本"), "Java 诊断");
        Check(GameCrashReport.Diagnose("MixinApplyError").Contains("模组"), "模组诊断");
        const string fabric = "Incompatible mods found!\nMod 'Example' (example) 2.0 requires version 0.100.0 or later of fabric-api, which is missing!";
        Check(GameCrashReport.Diagnose(fabric).Contains(fabric), "保留 Fabric 原始名称和依赖版本");
        const string forge = "Missing or unsupported mandatory dependencies:\nMod ID: 'architectury', Requested by: 'example', Expected range: '[13.0,)', Actual version: '[MISSING]'";
        Check(GameCrashReport.Diagnose(forge).Contains(forge), "保留 Forge 和 NeoForge 依赖原文");
        Check(GameCrashReport.Diagnose("exit -1").Contains("暂未识别"), "不按退出码猜测");
        Check(!GameCrashReport.Sanitize("--accessToken secret Authorization: Bearer private").Contains("secret") &&
            !GameCrashReport.Sanitize("Authorization: Bearer private").Contains("private"), "认证脱敏");
        string root = Path.Combine(Path.GetTempPath(), "nyx-crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            Directory.CreateDirectory(Path.Combine(root, "crash-reports"));
            string latest = Path.Combine(root, "logs", "latest.log");
            File.WriteAllText(latest, "OutOfMemoryError: Java heap space --accessToken secret");
            string old = Path.Combine(root, "crash-reports", "crash-old.txt");
            File.WriteAllText(old, "GLFW error 65542");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
            File.WriteAllText(Path.Combine(root, "accounts.json"), "DO NOT EXPORT");
            var report = GameCrashReport.Collect(root, null, DateTime.UtcNow.AddMinutes(-1));
            Check(report.PrimaryPath == latest && report.Logs.Count == 1, "只读本轮游戏日志");
            File.WriteAllText(latest, "later run");
            string zip = Path.Combine(root, "logs.zip");
            report.Export(zip);
            using var archive = ZipFile.OpenRead(zip);
            Check(archive.Entries.Count == 2 && archive.GetEntry("logs/latest.log") is not null, "ZIP 内容白名单");
            using var reader = new StreamReader(archive.GetEntry("logs/latest.log")!.Open());
            string text = reader.ReadToEnd();
            Check(text.Contains("OutOfMemoryError") && !text.Contains("secret") && !text.Contains("later run"), "导出脱敏快照");
            Check(GameCrashReport.Collect(null, null, DateTime.UtcNow).Logs.Count == 0, "无日志启动失败");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("崩溃诊断与日志导出：14 项通过");
    }

    /// <summary>断言具体业务结果，失败即中止并标明检查项。</summary>
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("崩溃检查失败：" + name);
    }
}
