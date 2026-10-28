using System.Net;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace NyxLauncher.Services;

public enum LoaderKind { Forge, Fabric, NeoForge }

/// <summary>读取真实加载器清单，支持官方与 BMCLAPI 源；不下载或安装加载器。</summary>
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
    private readonly TimeSpan _fallbackDelay;

    /// <summary>复用 HTTP 连接；允许测试注入客户端、总超时与慢源延迟，默认单源最多 15 秒、官方缓慢 3 秒后请求镜像。</summary>
    public LoaderAvailabilityService(HttpClient? client = null, TimeSpan? timeout = null, TimeSpan? fallbackDelay = null)
    {
        _client = client ?? SharedClient;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _fallbackDelay = fallbackDelay ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>按原始游戏 ID 查询官方清单；返回是否可添加。网络、格式和超时失败抛出 HTTP 异常，主动取消保持取消异常。</summary>
    public async Task<bool> CheckAsync(LoaderKind loader, string gameVersion, CancellationToken token) =>
        (await FetchMetadataAsync(loader, gameVersion, false, token)).Count > 0;

    /// <summary>按版本列表源策略取得完整版本与副标题；失败回退另一源，慢源并发时取消落败请求，主动取消向调用方传播。</summary>
    public Task<IReadOnlyList<LoaderVersion>> FetchAsync(LoaderKind loader, string gameVersion,
        SourceStrategy strategy, CancellationToken token) => SourceFallback.FetchAsync(strategy,
            (mirror, cancellation) => FetchSourceAsync(loader, gameVersion, mirror, cancellation), _fallbackDelay, token);

    /// <summary>在单源总时限内读取清单；Forge 官方清单另从发布页补齐日期，缺失日期视为源失败，不伪造发布日期。</summary>
    private async Task<IReadOnlyList<LoaderVersion>> FetchSourceAsync(LoaderKind loader, string gameVersion,
        bool mirror, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_timeout);
        try
        {
            if (mirror && loader == LoaderKind.Forge)
                return LoaderVersionParser.ParseForgeMirror(gameVersion, await FetchBytesAsync(
                    "https://bmclapi2.bangbang93.com/forge/minecraft/" + Uri.EscapeDataString(gameVersion), timeout.Token));
            var versions = await FetchMetadataAsync(loader, gameVersion, mirror, timeout.Token);
            if (loader != LoaderKind.Forge || versions.Count == 0) return versions;
            byte[] html = await FetchBytesAsync("https://files.minecraftforge.net/net/minecraftforge/forge/index_" +
                Uri.EscapeDataString(gameVersion) + ".html", timeout.Token);
            return LoaderVersionParser.AddForgeDates(versions, html);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new HttpRequestException($"{loader}：请求超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or XmlException or RegexMatchTimeoutException)
        {
            throw new HttpRequestException($"{loader} {(mirror ? "BMCLAPI 镜像源" : "官方源")}：{ex.Message}", ex);
        }
    }

    /// <summary>解析指定游戏的元数据，返回精确匹配的版本；Fabric 先校验支持清单，所有请求共用取消令牌与总超时。</summary>
    private async Task<IReadOnlyList<LoaderVersion>> FetchMetadataAsync(LoaderKind loader, string gameVersion, bool mirror, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        string url = loader switch
        {
            LoaderKind.Forge => ForgeUrl,
            LoaderKind.Fabric => FabricUrl + Uri.EscapeDataString(gameVersion),
            LoaderKind.NeoForge => gameVersion == "1.20.1" ? LegacyNeoForgeUrl : NeoForgeUrl,
            _ => throw new ArgumentOutOfRangeException(nameof(loader))
        };
        if (mirror) url = MirrorUrl(url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_timeout);
        try
        {
            if (loader == LoaderKind.Fabric)
            {
                // 不支持的游戏 ID 在 loader 接口返回 400，先查官方支持清单以区分不支持与请求失败。
                using var games = JsonDocument.Parse(await FetchBytesAsync(mirror ? MirrorUrl(FabricGamesUrl) : FabricGamesUrl, timeout.Token));
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
                if (!supported) return Array.Empty<LoaderVersion>();
            }
            byte[] data = await FetchBytesAsync(url, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return ParseVersions(loader, gameVersion, data);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new HttpRequestException($"{loader}：请求超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or XmlException)
        {
            throw new HttpRequestException($"{loader}：{ex.Message}", ex);
        }
    }

    /// <summary>读取最多 8 MiB 的源数据；沿用调用方总超时和取消令牌，HTTP 错误不当作空列表。</summary>
    private async Task<byte[]> FetchBytesAsync(string url, CancellationToken token)
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
        => ParseVersions(loader, gameVersion, data).Count > 0;

    /// <summary>解析官方或透传镜像元数据，按版本自然顺序降序去重；Forge 日期由发布页补齐，Fabric 稳定性必须来自 loader.stable。</summary>
    public static IReadOnlyList<LoaderVersion> ParseVersions(LoaderKind loader, string gameVersion, byte[] data)
    {
        if (loader == LoaderKind.Fabric)
        {
            using var json = JsonDocument.Parse(data);
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("响应缺少加载器列表。");
            var available = new List<LoaderVersion>();
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("loader", out var build) || build.ValueKind != JsonValueKind.Object ||
                    !build.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(version.GetString()) ||
                    !build.TryGetProperty("stable", out var stable) || stable.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !item.TryGetProperty("intermediary", out var intermediary) || intermediary.ValueKind != JsonValueKind.Object ||
                    !intermediary.TryGetProperty("version", out var game) || game.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(game.GetString()))
                    throw new JsonException("加载器条目缺少有效版本。");
                // 新版去混淆游戏共用 0.0.0 映射；支持范围由游戏清单和带 ID 的 loader 请求限定。
                if (game.GetString() == gameVersion || game.GetString() == "0.0.0")
                    available.Add(new(loader, version.GetString()!, version.GetString()!, stable.GetBoolean() ? "稳定版" : "测试版"));
            }
            return LoaderVersionParser.Sort(available);
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
        var matches = artifact == "forge"
            ? versions.Where(x => x.StartsWith(gameVersion + "-", StringComparison.Ordinal) &&
                x.Length > gameVersion.Length + 1 && char.IsAsciiDigit(x[gameVersion.Length + 1]))
            : versions.Where(x => GetNeoForgeGameVersion(x) == gameVersion);
        return LoaderVersionParser.Sort(matches.Select(id => new LoaderVersion(loader, id,
            artifact == "forge" ? id[(gameVersion.Length + 1)..] : id,
            loader == LoaderKind.Forge ? "" : id.Contains('-') && artifact != "forge" ? "测试版" : "稳定版")));
    }

    /// <summary>仅将已知官方元数据路径映射到 BMCLAPI 文档中的透传端点，不改写任意远程 URL。</summary>
    private static string MirrorUrl(string url) => url
        .Replace("https://meta.fabricmc.net/", "https://bmclapi2.bangbang93.com/fabric-meta/", StringComparison.Ordinal)
        .Replace("https://maven.neoforged.net/releases/", "https://bmclapi2.bangbang93.com/maven/", StringComparison.Ordinal);

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
