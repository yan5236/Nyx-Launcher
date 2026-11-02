namespace NyxLauncher.Services;

/// <summary>导出树中的文件或目录；目录勾选状态由全部子项汇总，空目录保留独立状态。</summary>
public sealed class ModpackExportEntry(string relativePath, bool directory, bool selected)
{
    public string RelativePath { get; } = relativePath;
    public bool IsDirectory { get; } = directory;
    public List<ModpackExportEntry> Children { get; } = [];
    private bool _selected = selected;
    public bool? Selected => Children.Count == 0 ? _selected :
        Children.All(x => x.Selected == true) ? true : Children.All(x => x.Selected == false) ? false : null;
    public string Label => Path.GetFileName(RelativePath) + (IsDirectory ? "/" : "") +
        (ModpackExportFiles.Describe(RelativePath) is { Length: > 0 } description ? "  ·  " + description : "");

    /// <summary>勾选或取消整个子树；仅改变导出选择，不修改源文件。</summary>
    public void Select(bool selected)
    {
        _selected = selected;
        foreach (var child in Children) child.Select(selected);
    }
}

/// <summary>实际游戏目录的导出清单与常见文件标注；排除启动器、凭据、运行库及链接。</summary>
public static class ModpackExportFiles
{
    /// <summary>为常见文件和目录提供中文用途；未知文件保留原名，不猜测内容。</summary>
    public static string Describe(string relative) => relative.ToLowerInvariant() switch
    {
        "mods" => "模组", "config" => "模组配置", "defaultconfigs" => "默认配置",
        "saves" => "存档", "screenshots" => "截图", "resourcepacks" => "资源包",
        "shaderpacks" => "光影包", "schematics" => "投影 / 原理图", "litematica" => "投影配置",
        "options.txt" => "游戏设置", "optionsof.txt" => "OptiFine 设置", "optionsshaders.txt" => "光影设置",
        "servers.dat" or "servers.dat_old" => "服务器列表", "logs" => "游戏日志",
        "crash-reports" => "崩溃报告", "kubejs" => "KubeJS 脚本", "scripts" => "脚本",
        "journeymap" or "xaeroworldmap" or "xaerowaypoints" => "地图 / 路径点",
        _ => ""
    };

    /// <summary>拒绝运行库、认证和启动器状态；普通配置仍可选择，根目录客户端 JAR 不参与导出。</summary>
    public static bool Allowed(string relative)
    {
        string first = relative.Split('/')[0].ToLowerInvariant();
        return !first.StartsWith('.') && first is not ("versions" or "assets" or "libraries" or "natives" or
            "runtime" or "java" or "accounts.json" or "launcher_accounts.json" or "launcher_profiles.json" or
            "usercache.json" or "usernamecache.json" or "instance.cfg" or "mmc-pack.json" or "mcbbs.packmeta" or
            "modrinth.index.json") && (relative.Contains('/') || !first.EndsWith(".jar"));
    }

    /// <summary>在后台扫描可选文件树；不跟随链接，限制层级与条目数，访问失败由界面报告，不显示不完整清单。</summary>
    public static IReadOnlyList<ModpackExportEntry> Scan(string root, CancellationToken token)
    {
        GameInstallationService.EnsureNoLinks(root);
        int count = 0;
        return Read(root, "", 0);

        /// <summary>递归建立数据树并限制深度及总量；排除当前版本清单，默认仅勾选模组和配置。</summary>
        List<ModpackExportEntry> Read(string directory, string prefix, int depth)
        {
            if (depth > 64) throw new IOException("目录层级超过 64 层。");
            var result = new List<ModpackExportEntry>();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                string relative = prefix + Path.GetFileName(path);
                if (!Allowed(relative)) continue;
                if (relative.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) + ".json", StringComparison.OrdinalIgnoreCase)) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (++count > 100000) throw new IOException("可选文件超过 100000 项。");
                bool folder = (attributes & FileAttributes.Directory) != 0;
                var entry = new ModpackExportEntry(relative, folder, relative.Split('/')[0] is "mods" or "config" or "defaultconfigs");
                if (folder) entry.Children.AddRange(Read(path, relative + "/", depth + 1));
                result.Add(entry);
            }
            return result.OrderByDescending(x => x.IsDirectory).ToList();
        }
    }

    /// <summary>展开当前勾选的叶子文件；空目录也保留，由 ZIP 写入阶段创建目录项。</summary>
    public static IEnumerable<ModpackExportEntry> SelectedFiles(IEnumerable<ModpackExportEntry> entries) =>
        entries.SelectMany(x => x.Children.Count > 0 ? SelectedFiles(x.Children) : x.Selected == true ? [x] : []);
}
