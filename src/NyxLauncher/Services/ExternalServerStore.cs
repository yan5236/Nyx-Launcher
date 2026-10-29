using System.Text.Json;

namespace NyxLauncher.Services;

public sealed record ExternalServer(string Name, string Url)
{
    /// <summary>列表显示服务器名称；完整地址由界面单独显示以区分同名站点。</summary>
    public override string ToString() => Name;
}

/// <summary>保存认证服务器目录；默认提供 LittleSkin，损坏文件禁止覆盖。</summary>
public sealed class ExternalServerStore
{
    public static readonly ExternalServer LittleSkin = new("LittleSkin", "https://littleskin.cn/api/yggdrasil/");
    private readonly string _path;
    public IReadOnlyList<ExternalServer> Servers { get; private set; } = [LittleSkin];
    public string? LoadError { get; private set; }

    /// <summary>读取并校验服务器地址；无配置时直接提供默认站点。</summary>
    public ExternalServerStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NyxLauncher", "auth-servers.json");
        try
        {
            if (!File.Exists(_path)) return;
            var servers = JsonSerializer.Deserialize<ExternalServer[]>(File.ReadAllText(_path)) ?? throw new IOException("服务器列表为空。");
            Servers = servers.Select(s => s is null || string.IsNullOrWhiteSpace(s.Name) ? throw new ArgumentException("服务器名称为空。") : new ExternalServer(s.Name, Normalize(s.Url))).DistinctBy(s => s.Url).ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
        { LoadError = "无法读取认证服务器配置：" + ex.Message; }
    }

    /// <summary>标准化手输或拖拽地址；只接收 HTTPS，不允许凭据、查询、片段或控制字符进入认证及 JVM 参数。</summary>
    public static string Normalize(string input)
    {
        string value = input.Trim();
        const string prefix = "authlib-injector:yggdrasil-server:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) value = Uri.UnescapeDataString(value[prefix.Length..]);
        if (!value.Contains("://")) value = "https://" + value;
        if (value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("请输入不带凭据、查询或片段的 HTTPS 认证服务器地址。");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    /// <summary>按规范地址去重并原子保存；磁盘失败时不更新列表。</summary>
    public ExternalServer Add(ExternalServer server)
    {
        if (LoadError is not null) throw new IOException(LoadError);
        server = server with { Url = Normalize(server.Url) };
        var existing = Servers.FirstOrDefault(s => s.Url == server.Url);
        if (existing is not null) return existing;
        Save(Servers.Append(server).ToArray());
        return server;
    }

    /// <summary>替换目录项并检查地址冲突；不会改变已保存账户的认证地址或令牌。</summary>
    public void Update(ExternalServer original, ExternalServer replacement)
    {
        replacement = replacement with { Url = Normalize(replacement.Url) };
        if (!Servers.Contains(original)) throw new ArgumentException("认证服务器已不存在。");
        if (Servers.Any(s => s != original && s.Url == replacement.Url)) throw new ArgumentException("该认证地址已存在。");
        Save(Servers.Select(s => s == original ? replacement : s).ToArray());
    }

    /// <summary>删除目录项，允许删除默认站点；已有账户和游戏文件保持原状。</summary>
    public void Remove(ExternalServer server)
    {
        if (!Servers.Contains(server)) throw new ArgumentException("认证服务器已不存在。");
        Save(Servers.Where(s => s != server).ToArray());
    }

    /// <summary>以同目录临时文件原子更新列表，损坏配置或写入失败均保留原状态。</summary>
    private void Save(ExternalServer[] updated)
    {
        if (LoadError is not null) throw new IOException(LoadError);
        if (updated.Any(s => string.IsNullOrWhiteSpace(s.Name))) throw new ArgumentException("请输入认证服务器名称。");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(updated)); File.Move(temp, _path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        Servers = updated;
    }
}
