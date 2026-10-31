namespace NyxLauncher.Services;

/// <summary>模组安装的兼容性及目录边界；传输和任务生命周期由现有下载模块负责。</summary>
public static class ModInstallationService
{
    /// <summary>按精确游戏版本及加载器筛选本地版本；损坏清单和原版均不可作为模组安装目标。</summary>
    public static bool IsCompatible(InstalledVersion version, string game, string loader) =>
        version.Error is null && version.GameVersion == game &&
        version.Loader.Split(':')[0].Trim().Equals(loader, StringComparison.OrdinalIgnoreCase);

    /// <summary>重新扫描目标并验证真实路径与兼容性，按现有隔离设置计算 mods；拒绝链接路径，失败不创建目录或写入文件。</summary>
    public static string ResolveMods(string folder, InstalledVersion target, ModrinthModFile file,
        string game, string loader, IsolationMode isolation)
    {
        file.Validate();
        if (!file.GameVersions.Contains(game) || !file.Loaders.Contains(loader))
            throw new ArgumentException("模组文件不支持所选 Minecraft 版本或加载器。");
        GameInstallationService.ValidateName(target.Name);
        if (!Path.IsPathFullyQualified(folder)) throw new IOException("请选择有效的游戏目录。");
        string expected = Path.Combine(Path.GetFullPath(folder), "versions", target.Name);
        if (!string.Equals(expected, Path.GetFullPath(target.DirectoryPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("目标版本不属于当前游戏目录。");
        GameInstallationService.EnsureNoLinks(expected);
        var current = InstalledVersionService.Scan(folder).SingleOrDefault(x => x.Name == target.Name);
        if (current is null || !IsCompatible(current, game, loader))
            throw new IOException("目标版本已变化或不兼容，请重新选择。");
        string mods = Path.Combine(VersionIsolation.Resolve(folder, current, isolation), "mods");
        GameInstallationService.EnsureNoLinks(Path.Combine(mods, file.FileName));
        return mods;
    }

    /// <summary>复用统一传输的暂存、长度及 SHA-1 校验和原子发布；完整同名文件直接复用，其他已有文件明确拒绝覆盖。</summary>
    internal static async Task InstallAsync(ModrinthModFile file, string mods, DownloadTransport transport,
        Action<long, long>? progress, CancellationToken token)
    {
        file.Validate();
        string path = Path.Combine(mods, file.FileName);
        GameInstallationService.EnsureNoLinks(path);
        if (await DownloadTransport.ValidAsync(path, file.Sha1, file.Size, token)) return;
        await transport.DownloadAsync(file.Url, path, file.Sha1, file.Size, false, true, progress, token);
    }
}
