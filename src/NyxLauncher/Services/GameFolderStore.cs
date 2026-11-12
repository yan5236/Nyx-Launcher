using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>独立保存游戏目录集合及当前选择；移除仅修改配置，绝不删除游戏目录。</summary>
public sealed class GameFolderStore
{
    private sealed record Settings(string[] Folders, string? Selected, Dictionary<string, string>? SelectedVersions = null);
    private Dictionary<string, string> _selectedVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private bool _loadFailed;
    public IReadOnlyList<string> Folders { get; private set; } = Array.Empty<string>();
    public string? Selected { get; private set; }
    public string? LoadError { get; private set; }

    /// <summary>启动时创建程序目录下的 .minecraft；首次使用登记默认目录，其后保留用户的移除和选择结果。读取失败保留原文件并禁止覆盖。</summary>
    public GameFolderStore(string? path = null, string? launcherDirectory = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NyxLauncher", "game-folders.json");
        string defaultFolder = Path.Combine(launcherDirectory ?? LauncherInfo.DirectoryPath, ".minecraft");
        try { Directory.CreateDirectory(defaultFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LoadError = "无法创建默认游戏目录：" + ex.Message; }
        try
        {
            if (File.Exists(_path))
            {
                var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path))
                    ?? throw new JsonException("目录设置为空。");
                if (saved.Folders is null) throw new JsonException("缺少目录列表。");
                Folders = saved.Folders.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                Selected = saved.Selected is null ? null : Normalize(saved.Selected);
                if (Selected is not null && !Folders.Contains(Selected, StringComparer.OrdinalIgnoreCase))
                    throw new JsonException("当前目录不在列表中。");
                foreach (var entry in saved.SelectedVersions ?? [])
                    _selectedVersions[Normalize(entry.Key)] = entry.Value;
            }
            else
            {
                Folders = new[] { Normalize(defaultFolder) };
                Selected = Folders[0];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            Folders = Array.Empty<string>();
            Selected = null;
            _loadFailed = true;
            LoadError = "无法读取游戏目录设置，请修复该文件后重启：" + _path + "\n" + ex.Message;
        }
    }

    /// <summary>规范化绝对路径并去除尾部分隔符；拒绝空路径和相对路径，不隐式依赖工作目录。</summary>
    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择绝对路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>登记实际存在的目录并按 Windows 路径规则去重；不自动切换当前选择，写入失败不更新内存。</summary>
    public void Add(string path)
    {
        path = Normalize(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("所选文件夹不存在或不可访问。");
        Save(Folders.Append(path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), Selected);
    }

    /// <summary>保存已登记且可访问的目录作为当前目录；成功后调用方可立即关闭弹窗。</summary>
    public void Select(string path)
    {
        path = Normalize(path);
        if (!Folders.Contains(path, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("该目录未登记。");
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("该目录不存在或不可访问，请重新添加有效路径。");
        Save(Folders.ToArray(), path);
    }

    /// <summary>仅从配置移除路径；移除当前项时选中剩余首项，没有剩余项则清空选择，磁盘内容保持不变。</summary>
    public void Remove(string path)
    {
        path = Normalize(path);
        var remaining = Folders.Where(x => !string.Equals(x, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        Save(remaining, string.Equals(Selected, path, StringComparison.OrdinalIgnoreCase) ? remaining.FirstOrDefault() : Selected);
    }

    /// <summary>从当前目录的扫描结果恢复上次选择；旧配置、已删除或损坏的版本回退首个有效项，无有效项返回 null，不写入配置。</summary>
    public InstalledVersion? RestoreVersion(IReadOnlyList<InstalledVersion> versions)
    {
        string? name = Selected is not null ? _selectedVersions.GetValueOrDefault(Selected) : null;
        return versions.FirstOrDefault(x => x.Error is null && x.Name == name)
            ?? versions.FirstOrDefault(x => x.Error is null);
    }

    /// <summary>保存当前目录所选版本名；空名称或未选目录时拒绝，同值不重复写盘，写入失败保留原内存并向界面抛出异常。</summary>
    public void SelectVersion(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Selected is null) throw new InvalidOperationException("尚未选择游戏目录。");
        if (_selectedVersions.GetValueOrDefault(Selected) == name) return;
        var selections = new Dictionary<string, string>(_selectedVersions, StringComparer.OrdinalIgnoreCase) { [Selected] = name };
        Save(Folders.ToArray(), Selected, selections);
    }

    /// <summary>先原子替换同目录配置文件，再更新内存状态；损坏配置不覆盖，I/O 异常交给界面明确提示。</summary>
    private void Save(string[] folders, string? selected, Dictionary<string, string>? selections = null)
    {
        if (_loadFailed) throw new IOException(LoadError);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        selections = (selections ?? _selectedVersions).Where(x => folders.Contains(x.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(folders, selected, selections)));
        File.Move(temporary, _path, true);
        Folders = folders;
        Selected = selected;
        _selectedVersions = selections;
    }
}
