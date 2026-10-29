using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NyxLauncher.Services;

/// <summary>本地 Java 的安装目录和实际版本；发现阶段只读元数据，不执行扫描到的程序。</summary>
public sealed record JavaRuntime(string Home, string Version, int Major, string Architecture, string Vendor)
{
    public string Executable => Path.Combine(Home, "bin", "java.exe");
    public string Title => $"Java {Major} · {Version} · {Architecture}";
    public bool Available => File.Exists(Executable);
    public string Detail => Vendor + (Available ? "" : " · 路径已失效") + "\n" + Home;

    /// <summary>解析现代版本、旧式 1.8 和 8u 版本号；无法识别时返回零。</summary>
    public static int ParseMajor(string version)
    {
        var match = Regex.Match(version, @"^(?:1\.)?(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out int major) ? major : 0;
    }

    /// <summary>统一 Java 8 写法并补齐省略的零段，最后比较构建号；21+35 应排在 21.0.1+1 之后，证书包后缀不当作版本。</summary>
    internal static string SortKey(string version)
    {
        string normalized = Regex.Replace(Regex.Replace(version, @"^1\.", ""), @"^8u(\d+)", "8.0.$1");
        string main = Regex.Match(normalized, @"^\d+(?:[._]\d+)*").Value;
        var numbers = Regex.Matches(main, @"\d+").Select(x => x.Value).ToArray();
        string build = Regex.Match(normalized, @"(?:\+|-b)(\d+)").Groups[1].Value;
        return string.Join('.', Enumerable.Range(0, 6).Select(i => (i < numbers.Length ? numbers[i] : "0").PadLeft(10, '0')))
            + "." + build.PadLeft(10, '0');
    }

    /// <summary>读取所选 Java 根目录或 bin 目录，校验 PE 架构并从 release 或版本资源获取版本；无效或不可读返回空。</summary>
    public static JavaRuntime? Inspect(string folder)
    {
        try
        {
            string home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (Path.GetFileName(home).Equals("bin", StringComparison.OrdinalIgnoreCase)) home = Path.GetDirectoryName(home)!;
            string executable = Path.Combine(home, "bin", "java.exe");
            if (!File.Exists(executable)) return null;
            using var stream = File.OpenRead(executable);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) return null;
            stream.Position = 60;
            int offset = reader.ReadInt32();
            if (offset < 64 || offset > stream.Length - 6) return null;
            stream.Position = offset;
            if (reader.ReadUInt32() != 0x4550) return null;
            string architecture = reader.ReadUInt16() switch { 0x8664 => "x64", 0x14c => "x86", 0xaa64 => "ARM64", _ => "未知" };
            string release = Path.Combine(home, "release");
            var values = File.Exists(release) ? File.ReadLines(release).Where(x => x.Contains('='))
                .Select(x => x.Split('=', 2)).GroupBy(x => x[0]).ToDictionary(x => x.Key, x => x.Last()[1].Trim().Trim('"')) : [];
            string version = values.GetValueOrDefault("JAVA_VERSION") ?? FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "";
            int major = ParseMajor(version);
            return major > 0 ? new(home, version, major, architecture, values.GetValueOrDefault("IMPLEMENTOR") ?? "本地 Java") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { return null; }
    }
}

public sealed record JavaScanResult(IReadOnlyList<JavaRuntime> Runtimes, int SkippedDirectories);

/// <summary>后台浅搜索常见位置和盘符；每个入口最多三层目录，不跟随联接，不需要管理员权限。</summary>
public static class JavaDiscovery
{
    /// <summary>返回所有就绪盘符；单个驱动器不可访问时继续枚举其他盘符。</summary>
    public static string[] DriveRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
            try { if (drive.IsReady) roots.Add(drive.RootDirectory.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return roots.ToArray();
    }

    /// <summary>优先搜索已登记路径、环境变量和常见安装目录，再覆盖所有就绪盘符；深层已知目录作为独立浅搜索入口。</summary>
    public static string[] SearchRoots(IEnumerable<string> knownHomes)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = knownHomes.Concat(new[]
        {
            Environment.GetEnvironmentVariable("JAVA_HOME") ?? "", Environment.GetEnvironmentVariable("JDK_HOME") ?? "",
            Path.Combine(profile, ".jdks"), Path.Combine(profile, ".jvm"), Path.Combine(profile, ".sdkman", "candidates", "java"),
            Path.Combine(local, "Programs"), Path.Combine(roaming, ".minecraft", "runtime"),
            Path.Combine(AppContext.BaseDirectory, "Java"), Path.Combine(AppContext.BaseDirectory, ".minecraft", "runtime")
        }).Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (string programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            roots = roots.Concat(new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft" }.Select(x => Path.Combine(programFiles, x)));
        return roots.Concat(DriveRoots()).Select(x => x.Trim().Trim('"')).Where(x => Path.IsPathFullyQualified(x) && Directory.Exists(x))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>每个入口只检查自身及三层子目录的 Java，不继续深入；重叠入口保留较大搜索预算，取消抛出，权限和链接局部跳过。</summary>
    public static JavaScanResult Scan(IEnumerable<string> roots, IProgress<string>? progress, CancellationToken token)
    {
        var found = new Dictionary<string, JavaRuntime>(StringComparer.OrdinalIgnoreCase);
        var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Directory, int Remaining)>(roots.Select(x => (x, 3)));
        int skipped = 0;
        long last = 0;
        while (pending.TryDequeue(out var next))
        {
            token.ThrowIfCancellationRequested();
            string directory = Path.GetFullPath(next.Directory);
            if (visited.TryGetValue(directory, out int previous) && previous >= next.Remaining) continue;
            visited[directory] = next.Remaining;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                string name = Path.GetFileName(directory);
                if (name.Equals(".nyx-install", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".staging", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "Java", StringComparison.OrdinalIgnoreCase)) continue;
                if (Environment.TickCount64 - last > 200)
                { progress?.Report($"已找到 {found.Count} 个 Java · {directory}"); last = Environment.TickCount64; }
                if (JavaRuntime.Inspect(directory) is { } java)
                    found[java.Home] = java;
                if (next.Remaining == 0) continue;
                foreach (string child in Directory.EnumerateDirectories(directory))
                { token.ThrowIfCancellationRequested(); pending.Enqueue((child, next.Remaining - 1)); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(found.Values.OrderByDescending(x => JavaRuntime.SortKey(x.Version), StringComparer.Ordinal).ToArray(), skipped);
    }
}
