using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>启动时固定选择快照，避免扫描或切换偏好改变正在启动的游戏。</summary>
public sealed record JavaSelection(IReadOnlyList<JavaRuntime> Runtimes, string? SelectedHome, bool AutoScanAttempted = false);

/// <summary>持久化 Java 列表和自动/手动选择；移除仅修改配置，绝不卸载 Java。</summary>
public sealed class JavaRuntimeStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private JavaSelection _current = new([], null);
    public string? LoadError { get; private set; }
    public event EventHandler? Changed;
    public JavaSelection Current { get { lock (_gate) return _current; } }

    /// <summary>读取用户 Java 配置；损坏时保留原文件并展示读取错误，不修改任何安装目录。</summary>
    public JavaRuntimeStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NyxLauncher", "java-settings.json");
        try
        {
            if (File.Exists(_path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_path));
                var saved = document.Deserialize<JavaSelection>() ?? throw new JsonException("配置为空。");
                if (saved.Runtimes is null || saved.Runtimes.Any(x => x is null || string.IsNullOrWhiteSpace(x.Home) ||
                    !Path.IsPathFullyQualified(x.Home) || string.IsNullOrWhiteSpace(x.Version) || x.Major <= 0 || string.IsNullOrWhiteSpace(x.Architecture)))
                    throw new JsonException("Java 路径无效。");
                _current = saved with
                {
                    Runtimes = saved.Runtimes.DistinctBy(x => x.Home, StringComparer.OrdinalIgnoreCase).ToArray(),
                    AutoScanAttempted = saved.AutoScanAttempted || !document.RootElement.TryGetProperty(nameof(JavaSelection.AutoScanAttempted), out _)
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { LoadError = "无法读取 Java 配置：" + ex.Message; }
    }

    /// <summary>首次自动扫描前原子保存已尝试状态；取消、失败或重启均不重复自动扫描。旧配置视为已初始化，读取异常时不覆盖配置；写入失败抛出 I/O 异常且不改变状态。</summary>
    public bool TryBeginAutomaticScan()
    {
        lock (_gate)
        {
            if (_current.AutoScanAttempted || LoadError is not null) return false;
            Save(_current with { AutoScanAttempted = true });
            return true;
        }
    }

    /// <summary>合并已扫描、导入或安装的记录并去重，保留用户选择；原子保存失败则内存状态不变。</summary>
    public void Add(IEnumerable<JavaRuntime> runtimes)
    {
        lock (_gate)
            Save(_current with { Runtimes = _current.Runtimes.Concat(runtimes).GroupBy(x => x.Home, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Last()).OrderByDescending(x => JavaRuntime.SortKey(x.Version), StringComparer.Ordinal).ToArray() });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>只移除目录登记；若移除当前选择则恢复自动选择，下一次扫描允许重新添加同一路径。</summary>
    public void Remove(string home)
    {
        lock (_gate) Save(_current with
        {
            Runtimes = _current.Runtimes.Where(x => !x.Home.Equals(home, StringComparison.OrdinalIgnoreCase)).ToArray(),
            SelectedHome = string.Equals(_current.SelectedHome, home, StringComparison.OrdinalIgnoreCase) ? null : _current.SelectedHome
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>保存自动选择或列表中现存且有效的 Java；拒绝未登记路径和失效安装。</summary>
    public void Select(string? home)
    {
        lock (_gate)
        {
            if (home is not null && !_current.Runtimes.Any(x => x.Home.Equals(home, StringComparison.OrdinalIgnoreCase) && x.Available))
                throw new IOException("该 Java 已失效，请重新扫描或导入。");
            Save(_current with { SelectedHome = home });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>同目录临时文件原子替换配置，更新内存只发生在写入成功后；调用方负责显示 I/O 错误。</summary>
    private void Save(JavaSelection selection)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(selection));
            File.Move(temporary, _path, true);
            _current = selection; LoadError = null;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>重新检查候选的实际版本，自动选择相同主版本的最新 x64 Java；手动版本失效或不兼容时明确报错，拒绝任意升级大版本。</summary>
    public static JavaRuntime Resolve(JavaSelection selection, int requiredMajor, IEnumerable<JavaRuntime> bundled)
    {
        if (selection.SelectedHome is { } home)
        {
            var chosen = JavaRuntime.Inspect(home) ?? throw new IOException("手动选择的 Java 已失效，请在设置 → Java 中重新选择。");
            if (chosen.Major != requiredMajor || chosen.Architecture != "x64")
                throw new IOException($"此游戏需要 Java {requiredMajor} x64，当前手动选择为 {chosen.Title}。请切换到自动选择或兼容版本。");
            return chosen;
        }
        return selection.Runtimes.Concat(bundled).Select(x => JavaRuntime.Inspect(x.Home)).OfType<JavaRuntime>()
            .Where(x => x.Major == requiredMajor && x.Architecture == "x64")
            .OrderByDescending(x => JavaRuntime.SortKey(x.Version), StringComparer.Ordinal).FirstOrDefault()
            ?? throw new IOException($"缺少 Java {requiredMajor} x64，请在设置 → Java 中扫描、导入或安装。");
    }
}
