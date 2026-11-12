using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>仅在用户明确同意后保存协议修订号；缺失、损坏或旧修订均重新提示，不代替用户同意。</summary>
public sealed class AgreementStore
{
    public const string Revision = "2026-11-04";
    private readonly string _path;
    private sealed record Receipt(string Revision, DateTimeOffset AcceptedAt);
    public bool IsAccepted { get; private set; }
    public string? LoadError { get; private set; }

    /// <summary>从用户目录或测试路径读取本地同意记录；不联网，读取失败保留文件并要求再次确认。</summary>
    public AgreementStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NyxLauncher", "agreement.json");
        try
        {
            if (File.Exists(_path)) IsAccepted = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(_path))?.Revision == Revision;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { LoadError = "无法读取原协议记录，请重新阅读并确认：" + ex.Message; }
    }

    /// <summary>用户明确同意时原子保存修订号和本机确认时间；失败返回可显示错误，内存不标记成功。</summary>
    public string? Accept()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(new Receipt(Revision, DateTimeOffset.UtcNow)));
            File.Move(_path + ".tmp", _path, true);
            IsAccepted = true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return "无法保存协议确认：" + ex.Message; }
    }
}
