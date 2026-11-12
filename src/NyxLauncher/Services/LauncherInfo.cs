using System.Reflection;

namespace NyxLauncher.Services;

/// <summary>集中维护官方入口、构建版本和持久数据根目录；资源仍从 AppContext.BaseDirectory 读取。</summary>
public static class LauncherInfo
{
    public const string Repository = "https://github.com/yan5236/Nyx-Launcher";
    public const string AgreementUrl = Repository + "/blob/main/USER_AGREEMENT.md";
    public const string PrivacyUrl = Repository + "/blob/main/PRIVACY.md";
    public static string Version { get; } = typeof(LauncherInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
    public static string ExecutablePath { get; } = Environment.ProcessPath ?? throw new IOException("无法确定启动器路径。");
    public static string DirectoryPath { get; } = Path.GetDirectoryName(ExecutablePath)!;
    public static bool CanSelfUpdate { get; } = typeof(LauncherInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(item => item.Key == "SingleFile" && item.Value == "true");
}
