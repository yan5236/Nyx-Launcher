using System.Globalization;
using System.Net;
using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>已校验的 Minecraft Java 服务器主机及 TCP 端口。</summary>
public sealed record ServerEndpoint
{
    public string Host { get; }
    public int Port { get; }

    /// <summary>保存解析后的裸主机名、有效端口。</summary>
    private ServerEndpoint(string host, int port) =>
        (Host, Port) = (host, port);

    /// <summary>接受域名、IPv4 或 IPv6 和可选端口，规范化国际域名。无效输入抛出参数异常。</summary>
    public static ServerEndpoint Parse(string address)
    {
        string input = address.Trim();
        string host = input;
        string? portText = null;
        int firstColon = input.IndexOf(':');
        if (input.StartsWith('['))
        {
            int end = input.IndexOf(']');
            if (end < 0 || (end + 1 < input.Length && input[end + 1] != ':')) throw InvalidAddress();
            host = input[1..end];
            if (!host.Contains(':')) throw InvalidAddress();
            if (end + 1 < input.Length) portText = input[(end + 2)..];
        }
        else if (firstColon >= 0 && firstColon == input.LastIndexOf(':'))
        {
            host = input[..firstColon];
            portText = input[(firstColon + 1)..];
        }

        int port = 25565;
        if (portText is not null && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
            port is < 1 or > 65535)) throw InvalidAddress();
        if (IPAddress.TryParse(host, out var ip)) host = ip.ToString();
        else
        {
            try { host = new IdnMapping().GetAscii(host); }
            catch (ArgumentException) { throw InvalidAddress(); }
            if (host.Length > 253 || (host.EndsWith('.') ? host[..^1] : host).Split('.').Any(label => label.Length is < 1 or > 63 ||
                label.StartsWith('-') || label.EndsWith('-') || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
                throw InvalidAddress();
        }
        return new(host, port);
    }

    /// <summary>创建用于输入弹窗的地址格式错误。</summary>
    private static ArgumentException InvalidAddress() => new("请输入有效的服务器地址，可附加端口，例如 example.org:25565。");
}

/// <summary>已安装实例及其所属游戏目录；卡片和启动都按完整实例路径区分同名版本。</summary>
public sealed record ServerVersion(string Folder, InstalledVersion Version)
{
    /// <summary>后台扫描指定游戏目录的可启动实例；跳过无效清单，读取错误与取消交给调用方处理。</summary>
    internal static Task<ServerVersion[]> ScanFolderAsync(string folder, CancellationToken token = default) =>
        Task.Run(() => InstalledVersionService.Scan(folder, token).Where(version => version.Error is null)
            .Select(version => new ServerVersion(folder, version)).ToArray(), token);

    /// <summary>同名版本以完整实例路径区分。</summary>
    public override string ToString() => $"{Version.AccessibleName} · {Folder}";
}

internal sealed record ServerEntry(string Id, string Name, string Address, string? VersionPath = null, bool Featured = false);

/// <summary>保存自定义服务器和精选服务器的本机偏好，精选地址始终来自内置清单。</summary>
internal sealed class ServerStore
{
    private sealed record Preference(string Name, string? VersionPath);
    private sealed record Settings(ServerEntry[] Custom, Dictionary<string, Preference> Featured);
    private readonly string _path;
    private readonly ServerEntry[] _catalog;
    private Settings _settings;

    /// <summary>读取内置清单和本机设置；读取失败向调用方报告，避免用空列表覆盖损坏文件。</summary>
    internal ServerStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NyxLauncher", "servers.json");
        using var catalog = typeof(ServerStore).Assembly.GetManifestResourceStream("NyxLauncher.FeaturedServers.json")
            ?? throw new IOException("缺少精选服务器清单。");
        _catalog = JsonSerializer.Deserialize<ServerEntry[]>(catalog) ?? throw new JsonException("精选服务器清单为空。");
        try
        {
            _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path)) ?? throw new JsonException("服务器设置为空。");
            if (_settings.Custom is null || _settings.Featured is null ||
                _settings.Custom.Any(entry => entry is null || entry.Featured || string.IsNullOrWhiteSpace(entry.Id) ||
                    string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Address)) ||
                _settings.Custom.Select(entry => entry.Id).Distinct().Count() != _settings.Custom.Length ||
                _settings.Featured.Values.Any(value => value is null || string.IsNullOrWhiteSpace(value.Name)))
                throw new JsonException("服务器设置格式无效。");
        }
        catch (FileNotFoundException) { _settings = new([], []); }
        catch (DirectoryNotFoundException) { _settings = new([], []); }
    }

    /// <summary>返回自定义列表，或将精选名称和默认版本偏好合并到固定地址上。</summary>
    internal IEnumerable<ServerEntry> Entries(bool featured) => featured
        ? _catalog.Select(entry => _settings.Featured.TryGetValue(entry.Id, out var value)
            ? entry with { Featured = true, Name = value.Name, VersionPath = value.VersionPath }
            : entry with { Featured = true })
        : _settings.Custom;

    /// <summary>原子保存新增或修改；精选项只记录名称和默认版本，写盘失败不会更改内存。</summary>
    internal void Save(ServerEntry entry)
    {
        if (entry.Featured)
        {
            var preferences = new Dictionary<string, Preference>(_settings.Featured)
            {
                [entry.Id] = new(entry.Name, entry.VersionPath)
            };
            Commit(_settings with { Featured = preferences });
            return;
        }
        _ = ServerEndpoint.Parse(entry.Address);
        var current = _settings.Custom;
        Commit(_settings with
        {
            Custom = current.Any(item => item.Id == entry.Id)
                ? current.Select(item => item.Id == entry.Id ? entry : item).ToArray()
                : [.. current, entry]
        });
    }

    /// <summary>移除指定自定义卡片。调用方误传精选项时保留清单及偏好。</summary>
    internal void Delete(ServerEntry entry)
    {
        if (entry.Featured) return;
        Commit(_settings with { Custom = _settings.Custom.Where(item => item.Id != entry.Id).ToArray() });
    }

    /// <summary>同目录写入临时文件后原子替换设置；替换成功才提交内存快照。</summary>
    private void Commit(Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(settings));
        File.Move(_path + ".tmp", _path, true);
        _settings = settings;
    }
}
