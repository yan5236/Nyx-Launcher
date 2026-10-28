using System.Net;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace NyxLauncher.Services;

public enum LoaderKind { Forge, Fabric, NeoForge }

/// <summary>读取加载器官方元数据，仅探测指定游戏版本是否有可用构建，不下载或安装加载器。</summary>
public sealed class LoaderAvailabilityService
{
    public const string ForgeUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
    public const string FabricUrl = "https://meta.fabricmc.net/v2/versions/loader/";
    public const string FabricGamesUrl = "https://meta.fabricmc.net/v2/versions/game";
    public const string NeoForgeUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
    public const string LegacyNeoForgeUrl = "https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml";
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;

    /// <summary>复用 HTTP 连接；允许测试注入客户端与超时，默认每个探测最多等待 15 秒。</summary>
    public LoaderAvailabilityService(HttpClient? client = null, TimeSpan? timeout = null)
    {
        _client = client ?? SharedClient;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>按原始游戏 ID 查询官方清单；返回是否可添加。网络、格式和超时失败抛出 HTTP 异常，主动取消保持取消异常。</summary>
    public async Task<bool> CheckAsync(LoaderKind loader, string gameVersion, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        string url = loader switch
        {
            LoaderKind.Forge => ForgeUrl,
            LoaderKind.Fabric => FabricUrl + Uri.EscapeDataString(gameVersion),
            LoaderKind.NeoForge => gameVersion == "1.20.1" ? LegacyNeoForgeUrl : NeoForgeUrl,
            _ => throw new ArgumentOutOfRangeException(nameof(loader))
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_timeout);
        try
        {
            if (loader == LoaderKind.Fabric)
            {
                // 不支持的游戏 ID 在 loader 接口返回 400，先查官方支持清单以区分不支持与请求失败。
                using var games = JsonDocument.Parse(await FetchAsync(FabricGamesUrl, timeout.Token));
                if (games.RootElement.ValueKind != JsonValueKind.Array || games.RootElement.GetArrayLength() == 0)
                    throw new JsonException("响应缺少 Fabric 游戏版本列表。");
                bool supported = false;
                foreach (var game in games.RootElement.EnumerateArray())
                {
                    if (game.ValueKind != JsonValueKind.Object || !game.TryGetProperty("version", out var id) ||
                        id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                        throw new JsonException("Fabric 游戏版本条目无效。");
                    supported |= id.GetString() == gameVersion;
                }
                timeout.Token.ThrowIfCancellationRequested();
                if (!supported) return false;
            }
            byte[] data = await FetchAsync(url, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return Parse(loader, gameVersion, data);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new HttpRequestException($"{loader} 官方源：请求超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or XmlException)
        {
            throw new HttpRequestException($"{loader} 官方源：{ex.Message}", ex);
        }
    }

    /// <summary>读取最多 8 MiB 的官方元数据；沿用调用方总超时和取消令牌，HTTP 错误不当作空列表。</summary>
    private async Task<byte[]> FetchAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("NyxLauncher/0.1");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > 8 * 1024 * 1024) throw new IOException("加载器清单超过大小限制。");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>校验官方 JSON 或 Maven XML；Fabric 数据须来自带游戏 ID 的 loader 请求，兼容新版通用映射 0.0.0，格式错误抛出解析异常。</summary>
    public static bool Parse(LoaderKind loader, string gameVersion, byte[] data)
    {
        if (loader == LoaderKind.Fabric)
        {
            using var json = JsonDocument.Parse(data);
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("响应缺少加载器列表。");
            bool available = false;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("loader", out var build) || build.ValueKind != JsonValueKind.Object ||
                    !build.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(version.GetString()) ||
                    !item.TryGetProperty("intermediary", out var intermediary) || intermediary.ValueKind != JsonValueKind.Object ||
                    !intermediary.TryGetProperty("version", out var game) || game.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(game.GetString()))
                    throw new JsonException("加载器条目缺少有效版本。");
                // 新版去混淆游戏共用 0.0.0 映射；支持范围由游戏清单和带 ID 的 loader 请求限定。
                available |= game.GetString() == gameVersion || game.GetString() == "0.0.0";
            }
            return available;
        }
        if (loader is not (LoaderKind.Forge or LoaderKind.NeoForge)) throw new ArgumentOutOfRangeException(nameof(loader));
        using var input = new MemoryStream(data);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader).Root;
        string group = loader == LoaderKind.Forge ? "net.minecraftforge" : "net.neoforged";
        string artifact = loader == LoaderKind.Forge || gameVersion == "1.20.1" ? "forge" : "neoforge";
        var versions = root?.Element("versioning")?.Element("versions")?.Elements("version").Select(x => x.Value).ToArray();
        if (root?.Name != "metadata" || root.Element("groupId")?.Value != group ||
            root.Element("artifactId")?.Value != artifact || versions is null || versions.Length == 0 ||
            versions.Any(string.IsNullOrWhiteSpace))
            throw new XmlException("响应缺少有效的加载器 Maven 清单。");
        return artifact == "forge"
            ? versions.Any(x => x.StartsWith(gameVersion + "-", StringComparison.Ordinal) &&
                x.Length > gameVersion.Length + 1 && char.IsAsciiDigit(x[gameVersion.Length + 1]))
            : versions.Any(x => GetNeoForgeGameVersion(x) == gameVersion);
    }

    /// <summary>将 NeoForge 构建号还原为游戏 ID；兼容 20/21.x、26.x 及 +snapshot/pre/rc 元数据，避免把快照构建算入正式版。</summary>
    private static string? GetNeoForgeGameVersion(string version)
    {
        var metadata = version.Split('+', 2);
        var parts = metadata[0].Split('-', 2)[0].Split('.');
        if (parts.Length is not (3 or 4) || parts.Any(x => !int.TryParse(x, out _))) return null;
        string game = parts.Length == 3
            ? "1." + parts[0] + (parts[1] == "0" ? "" : "." + parts[1])
            : parts[0] + "." + parts[1] + (parts[2] == "0" ? "" : "." + parts[2]);
        return metadata.Length == 2 ? game + "-" + metadata[1] : game;
    }
}
