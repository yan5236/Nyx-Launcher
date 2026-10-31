using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace NyxLauncher.Services;

/// <summary>按实际 Java 版本接入经过校验的兼容组件，并设置本次 Java 的 Windows 图形偏好。</summary>
internal static class LaunchCompatibility
{
    /// <summary>按 Java 与 LWJGL 版本接入组件，给模块化 Forge 主类开放反射入口；禁用开关优先，保留原命令行表示方式。</summary>
    internal static void Apply(ProcessStartInfo info, AdvancedLaunchSettings settings, int javaMajor, string mainClass, bool affectedLwjgl)
    {
        bool stringArguments = info.ArgumentList.Count == 0;
        if (stringArguments)
        {
            var arguments = LaunchArguments.Parse(info.Arguments);
            info.Arguments = "";
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
        }
        if (!settings.DisableLwjglAgent && javaMajor >= 25 && affectedLwjgl)
            info.ArgumentList.Insert(0, "-javaagent:" + Component("lwjgl-unsafe-agent-2.0.jar", "6c07d3508dc090a12076876bdc227a19ce195a41c9e2c6535c3f82213b1b2015"));
        if (!settings.DisableJavaWrapper && javaMajor is >= 8 and < 19)
        {
            int index = info.ArgumentList.IndexOf(mainClass);
            if (index < 0) throw new IOException("找不到游戏主类，无法接入 Java Launch Wrapper。");
            info.ArgumentList.Insert(index, Component("java-launch-wrapper-1.4.4.jar", "88eb3ee58854e916437831697ef6a2fd493da9bc4ffb018f620cd88f090952aa"));
            info.ArgumentList.Insert(index, "-jar");
            if (javaMajor >= 9 && mainClass == "cpw.mods.bootstraplauncher.BootstrapLauncher")
                info.ArgumentList.Insert(0, "--add-exports=cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED");
        }
        if (stringArguments)
        {
            string arguments = LaunchArguments.Join(info.ArgumentList);
            info.ArgumentList.Clear();
            info.Arguments = arguments;
        }
    }

    /// <summary>校验随应用发布的固定构件；缺失或被修改时终止启动，不执行未经校验的 JAR。</summary>
    private static string Component(string name, string hash)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Launch", name);
        if (!File.Exists(path) || Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) != hash)
            throw new IOException($"启动组件 {name} 缺失或校验失败，请修复启动器安装。");
        return path;
    }

    /// <summary>只替换 GPU 偏好字段，保留 HDR 等其他字段；关闭选项写入系统默认，不强迫使用集成显卡。</summary>
    internal static string GpuPreference(string? previous, bool enabled) =>
        string.Join(';', (previous ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.TrimStart().StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
            .Append("GpuPreference=" + (enabled ? "2" : "0"))) + ";";

    /// <summary>在启动前为实际 Java 可执行文件写入当前用户图形偏好；拒绝非绝对路径，注册表失败向启动流程报告。</summary>
    internal static void ConfigureGpu(string executable, bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new IOException("Java 可执行文件路径无效，无法设置显卡偏好。");
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences", true);
        string path = Path.GetFullPath(executable);
        string next = GpuPreference(key.GetValue(path) as string, enabled);
        key.SetValue(path, next, RegistryValueKind.String);
    }
}
