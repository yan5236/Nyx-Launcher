namespace NyxLauncher.Services;

public enum IsolationMode { Disabled, Modded, NonRelease, ModdedOrNonRelease, All }

/// <summary>统一计算实际运行目录；共享 assets/libraries，只隔离存档、模组和游戏设置。</summary>
public static class VersionIsolation
{
    /// <summary>依据真实加载器和版本类型返回游戏根目录或该版本目录；无效元数据拒绝启动，不迁移或删除原有存档；带整合包标记的版本始终使用独立目录。</summary>
    public static string Resolve(string root, InstalledVersion version, IsolationMode mode)
    {
        if (version.Error is not null) throw new InvalidOperationException(version.Error);
        bool modded = version.Loader.Length > 0;
        bool nonRelease = version.Kind != "正式版";
        bool isolated = mode switch
        {
            IsolationMode.Disabled => false,
            IsolationMode.Modded => modded,
            IsolationMode.NonRelease => nonRelease,
            IsolationMode.ModdedOrNonRelease => modded || nonRelease,
            IsolationMode.All => true,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        return isolated || File.Exists(Path.Combine(version.DirectoryPath, ModpackInstallationService.IsolationMarker))
            ? version.DirectoryPath : Path.GetFullPath(root);
    }
}
