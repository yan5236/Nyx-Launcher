using System.Text.Json;

namespace NyxLauncher.Services;

public enum SourceStrategy { MirrorFirst, OfficialWithFallback, OfficialFirst }

/// <summary>下载与隔离偏好；并行文件数、线程数和限速在新一批下载任务启动时应用。</summary>
public sealed record DownloadSettings(
    SourceStrategy FileSource = SourceStrategy.OfficialWithFallback,
    SourceStrategy VersionSource = SourceStrategy.OfficialWithFallback,
    int MaxConnections = 16,
    int SpeedLimitMiB = 0,
    IsolationMode Isolation = IsolationMode.All,
    int MaxParallelDownloads = 16);

/// <summary>独立保存下载偏好，不读取或修改游戏目录。</summary>
public sealed class DownloadSettingsStore
{
    private readonly string _path;
    public DownloadSettings Current { get; private set; } = new();
    public string? LoadError { get; private set; }

    /// <summary>读取本地 JSON；文件缺失使用默认值，损坏或不可读时保留文件并报告错误。</summary>
    public DownloadSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NyxLauncher", "download-settings.json");
        try
        {
            if (File.Exists(_path))
                Current = Validate(JsonSerializer.Deserialize<DownloadSettings>(File.ReadAllText(_path))
                    ?? throw new JsonException("设置为空。"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            LoadError = "无法读取下载设置，暂用默认值：" + ex.Message;
        }
    }

    /// <summary>校验枚举和滑块范围；非法设置抛出异常，避免静默写入无效配置。</summary>
    private static DownloadSettings Validate(DownloadSettings settings)
    {
        if (!Enum.IsDefined(settings.FileSource) || !Enum.IsDefined(settings.VersionSource) ||
            settings.MaxConnections is < 1 or > 64 || settings.MaxParallelDownloads is < 1 or > 64 ||
            settings.SpeedLimitMiB is < 0 or > 100 || !Enum.IsDefined(settings.Isolation))
            throw new ArgumentException("下载设置超出有效范围。");
        return settings;
    }

    /// <summary>通过同目录临时文件替换配置；写入失败时保持内存中的原设置并返回可显示错误。</summary>
    public string? Save(DownloadSettings settings)
    {
        try
        {
            Validate(settings);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(settings));
            File.Move(_path + ".tmp", _path, true);
            Current = settings;
            LoadError = null;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "设置未保存：" + ex.Message;
        }
    }
}
