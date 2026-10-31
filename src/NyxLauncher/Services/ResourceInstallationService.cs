namespace NyxLauncher.Services;

/// <summary>资源包、光影和数据包的客户端目录解析；复用版本隔离和下载传输，不解压资源文件。</summary>
public static class ResourceInstallationService
{
    /// <summary>重新扫描并验证客户端归属及游戏版本，返回实际运行目录；无效或已删除的版本抛出 I/O 异常。</summary>
    public static string ResolveRoot(string folder, InstalledVersion target, string game, IsolationMode isolation)
    {
        GameInstallationService.ValidateName(target.Name);
        if (!Path.IsPathFullyQualified(folder)) throw new IOException("请选择有效的游戏目录。");
        string expected = Path.Combine(Path.GetFullPath(folder), "versions", target.Name);
        if (!string.Equals(expected, Path.GetFullPath(target.DirectoryPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("目标版本不属于当前游戏目录。");
        GameInstallationService.EnsureNoLinks(expected);
        var current = InstalledVersionService.Scan(folder).SingleOrDefault(x => x.Name == target.Name);
        if (current is null || current.Error is not null || current.GameVersion != game)
            throw new IOException("目标版本已变化或不兼容，请重新选择。");
        string root = VersionIsolation.Resolve(folder, current, isolation);
        GameInstallationService.EnsureNoLinks(root);
        return root;
    }

    /// <summary>列出实际运行目录直属且包含 level.dat 的存档；跳过链接和不可访问项，不创建目录。</summary>
    public static IReadOnlyList<string> Worlds(string root)
    {
        string saves = Path.Combine(root, "saves");
        GameInstallationService.EnsureNoLinks(saves);
        if (!Directory.Exists(saves)) return [];
        var result = new List<string>();
        foreach (string world in Directory.EnumerateDirectories(saves))
        {
            try
            {
                GameInstallationService.EnsureNoLinks(Path.Combine(world, "level.dat"));
                if (File.Exists(Path.Combine(world, "level.dat"))) result.Add(world);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>校验资源兼容性及存档归属后返回安装目录；数据包只接受仍存在的直属存档，拒绝路径穿越和链接。</summary>
    public static string Resolve(string folder, InstalledVersion target, ModrinthModFile file, string game,
        IsolationMode isolation, string? world = null)
    {
        file.Validate();
        if (!file.IsResource || !file.GameVersions.Contains(game)) throw new ArgumentException("资源不支持所选版本。");
        string root = ResolveRoot(folder, target, game, isolation);
        string destination;
        if (file.Category == "datapack")
        {
            if (world is null || !Worlds(root).Contains(Path.GetFullPath(world), StringComparer.OrdinalIgnoreCase))
                throw new IOException("存档已不存在或不属于所选客户端，请重新选择。");
            destination = Path.Combine(world, "datapacks");
        }
        else destination = Path.Combine(root, file.Category == "shader" ? "shaderpacks" : "resourcepacks");
        GameInstallationService.EnsureNoLinks(Path.Combine(destination, file.FileName));
        return destination;
    }
}
