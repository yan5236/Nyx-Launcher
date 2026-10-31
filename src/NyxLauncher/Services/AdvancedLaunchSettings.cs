using System.Text.Json;

namespace NyxLauncher.Services;

public enum GarbageCollector { Unspecified, G1, Z, Parallel, Serial }

public sealed record AdvancedLaunchSettings(string JvmArguments = "", string GameArguments = "",
    GarbageCollector Collector = GarbageCollector.Unspecified, bool DisableJavaWrapper = false,
    bool DisableLwjglAgent = false, bool HighPerformanceGpu = true);

/// <summary>独立保存高级启动配置；版本首次修改形成完整快照，全局保存不会覆盖已有版本配置。</summary>
public static class AdvancedLaunchSettingsStore
{
    public static string GlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NyxLauncher", "launch-advanced.json");

    /// <summary>返回当前作用域路径；版本配置随版本目录改名和删除。</summary>
    public static string PathFor(InstalledVersion? version) => version is null ? GlobalPath : Path.Combine(version.DirectoryPath, "nyx-launch.json");

    /// <summary>读取版本覆盖或全局默认值；损坏配置向调用方报错，不静默覆盖。</summary>
    public static AdvancedLaunchSettings Read(InstalledVersion? version = null, string? globalPath = null)
    {
        string path = version is null ? globalPath ?? GlobalPath : PathFor(version);
        if (!File.Exists(path)) return version is null ? new() : Read(null, globalPath);
        var value = JsonSerializer.Deserialize<AdvancedLaunchSettings>(File.ReadAllText(path)) ?? throw new IOException("高级启动设置为空。");
        Validate(value);
        return value;
    }

    /// <summary>校验枚举、参数长度和控制字符；参数解析错误在保存前报告。</summary>
    public static void Validate(AdvancedLaunchSettings value)
    {
        if (!Enum.IsDefined(value.Collector)) throw new ArgumentException("未知的垃圾回收器。");
        foreach (string text in new[] { value.JvmArguments, value.GameArguments })
        {
            if (text is null || text.Length > 16000 || text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
                throw new ArgumentException("启动参数过长或包含无效控制字符。");
            LaunchArguments.Parse(text);
        }
    }

    /// <summary>验证已有配置后原子替换；版本不存在或链接不安全时拒绝写入，失败保留原文件。</summary>
    public static void Save(AdvancedLaunchSettings value, InstalledVersion? version = null, string? globalPath = null)
    {
        Validate(value);
        Read(version, globalPath);
        string path = version is null ? globalPath ?? GlobalPath : PathFor(version);
        if (version is not null && !Directory.Exists(version.DirectoryPath)) throw new DirectoryNotFoundException("版本目录已不存在。");
        GameInstallationService.EnsureNoLinks(path);
        GameInstallationService.EnsureNoLinks(path + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>只删除本版本的高级覆盖，恢复继承最新全局值，不改变内存配置。</summary>
    public static void Reset(InstalledVersion version)
    {
        GameInstallationService.EnsureNoLinks(PathFor(version));
        File.Delete(PathFor(version));
    }
}
