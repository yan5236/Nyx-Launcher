using System.Runtime.InteropServices;
using System.Text.Json;

namespace NyxLauncher.Services;

public sealed record MemorySettings(bool Automatic = true, int ManualMiB = 4096);
public sealed record PhysicalMemory(int TotalMiB, int AvailableMiB);

/// <summary>保存全局内存偏好及版本覆盖；版本配置随版本目录改名、删除，不依赖隔离策略。</summary>
public static class MemorySettingsStore
{
    public static string GlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NyxLauncher", "launch-memory.json");

    /// <summary>返回版本专属配置路径；传入 null 时返回全局路径。</summary>
    public static string PathFor(InstalledVersion? version) => version is null ? GlobalPath : Path.Combine(version.DirectoryPath, "nyx-memory.json");

    /// <summary>读取并校验配置，缺失的版本配置继承全局；损坏与访问失败向界面或启动流程报告，不覆盖原文件。</summary>
    public static MemorySettings Read(InstalledVersion? version = null, string? globalPath = null)
    {
        string path = version is null ? globalPath ?? GlobalPath : PathFor(version);
        if (!File.Exists(path)) return version is null ? new() : Read(null, globalPath);
        var settings = JsonSerializer.Deserialize<MemorySettings>(File.ReadAllText(path)) ?? throw new IOException("内存设置为空。");
        Validate(settings);
        return settings;
    }

    /// <summary>拒绝无效手动内存范围；允许超过当前空闲内存，物理内存上限在使用时计算。</summary>
    private static void Validate(MemorySettings settings)
    {
        if (settings.ManualMiB is < 512 or > 1048576) throw new ArgumentException("内存分配必须在 512 MiB 到 1 TiB 之间。");
    }

    /// <summary>先验证现有配置可读，再以临时文件原子替换；失败保留原文件并向调用方抛出异常。</summary>
    public static void Save(MemorySettings settings, InstalledVersion? version = null, string? globalPath = null)
    {
        Validate(settings);
        Read(version, globalPath);
        string path = version is null ? globalPath ?? GlobalPath : PathFor(version);
        if (version is not null)
        {
            if (!Directory.Exists(version.DirectoryPath)) throw new DirectoryNotFoundException("版本目录已不存在。");
            GameInstallationService.EnsureNoLinks(path);
            GameInstallationService.EnsureNoLinks(path + ".tmp");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings));
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>删除当前版本覆盖以恢复继承，绝不更改全局或其他版本；文件系统错误由界面显示。</summary>
    public static void Reset(InstalledVersion version)
    {
        GameInstallationService.EnsureNoLinks(PathFor(version));
        File.Delete(PathFor(version));
    }
}

/// <summary>读取实时物理内存并依据版本和实际模组目录估算 Java 堆上限。</summary>
public static class MemoryAllocation
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended;
    }

    /// <summary>调用 Windows 获取物理内存快照；失败时抛出异常，避免显示虚构容量。</summary>
    public static PhysicalMemory Snapshot()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new((int)(status.TotalPhysical / 1048576), (int)(status.AvailablePhysical / 1048576));
    }

    /// <summary>读取系统内存状态，返回成功标志并保留原生错误码。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    /// <summary>统计实际运行目录的启用 jar 模组；非模组版本返回零，目录读取失败不隐瞒。</summary>
    public static int CountMods(string root, InstalledVersion? version, IsolationMode isolation)
    {
        if (version is null || string.IsNullOrEmpty(version.Loader)) return 0;
        string mods = Path.Combine(VersionIsolation.Resolve(root, version, isolation), "mods");
        return Directory.Exists(mods) ? Directory.EnumerateFiles(mods, "*.jar").Count() : 0;
    }

    /// <summary>手动值只受物理总量限制；自动值按游戏年代、加载器和模组数估算，并为系统及堆外内存预留空间，返回 256 MiB 步进的堆上限。</summary>
    public static int Resolve(MemorySettings settings, PhysicalMemory memory, InstalledVersion? version, int mods)
    {
        int maximum = Math.Max(512, Math.Min(1048576, memory.TotalMiB) / 256 * 256);
        if (!settings.Automatic) return Math.Clamp(settings.ManualMiB, 512, maximum);
        int baseline = Version.TryParse(version?.GameVersion, out var game) && game.Major == 1 && game.Minor < 17 ? 2048 : 3072;
        int demand = baseline + (string.IsNullOrEmpty(version?.Loader) ? 0 : 1024) + Math.Min(Math.Max(mods, 0), 1024) * 32;
        int budget = Math.Min(memory.AvailableMiB - Math.Max(1024, memory.TotalMiB / 10), memory.TotalMiB * 3 / 4);
        return Math.Clamp(Math.Min(demand, budget) / 256 * 256, 512, maximum);
    }
}
