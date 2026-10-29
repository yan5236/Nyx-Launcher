using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NyxLauncher.Services;

public enum JavaChannel { Mojang, Temurin, Zulu }

/// <summary>用户选定的具体发行版本；Mojang 地址为文件清单，其余为便携 ZIP。</summary>
public sealed record JavaPackage(JavaChannel Channel, int Major, string Version, string Id, string Url,
    string? Hash, long Size, string? PackageId = null)
{
    public string Label => $"{Version} · Windows x64";
    public string Description => Channel == JavaChannel.Mojang ? "Mojang 游戏运行时" : (Size > 0 ? $"JDK · {Size / 1048576d:0} MiB" : "JDK · ZIP");
    public string FolderName => $"{Channel.ToString().ToLowerInvariant()}-{Major}-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id)))[..12].ToLowerInvariant()}";
}

/// <summary>从三个官方渠道加载 Windows x64 正式发行版本，分页读取并按版本倒序提供给安装弹窗。</summary>
public sealed class JavaCatalogService
{
    internal const string MojangIndex = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
    private readonly HttpClient _http;

    /// <summary>注入有超时的元数据客户端；生命周期由对话框或安装任务持有。</summary>
    public JavaCatalogService(HttpClient http) => _http = http;

    /// <summary>加载指定主版本与渠道的全部可用包；无版本返回空，失败或取消交由界面呈现，绝不伪造版本。</summary>
    public async Task<IReadOnlyList<JavaPackage>> FetchAsync(int major, JavaChannel channel, CancellationToken token)
    {
        if (major is not (8 or 17 or 21 or 25)) throw new ArgumentException("请选择支持的 Java 主版本。");
        if (!Enum.IsDefined(channel)) throw new ArgumentException("未知 Java 渠道。");
        var result = new List<JavaPackage>();
        if (channel == JavaChannel.Mojang)
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(MojangIndex, token));
            result.AddRange(Parse(doc.RootElement, major, channel));
        }
        else
        {
            for (int page = 0; ; page++)
            {
                token.ThrowIfCancellationRequested();
                string url = channel == JavaChannel.Temurin
                    ? $"https://api.adoptium.net/v3/assets/feature_releases/{major}/ga?architecture=x64&image_type=jdk&os=windows&jvm_impl=hotspot&vendor=eclipse&heap_size=normal&page={page}&page_size=100&sort_order=DESC"
                    : $"https://api.azul.com/metadata/v1/zulu/packages/?java_version={major}&os=windows&arch=x86&hw_bitness=64&archive_type=zip&java_package_type=jdk&javafx_bundled=false&release_status=ga&availability_types=CA&page={page + 1}&page_size=100";
                using var response = await _http.GetAsync(url, token);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) break;
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (doc.RootElement.GetArrayLength() == 0) break;
                var items = Parse(doc.RootElement, major, channel);
                if (items.Count > 0 && items.All(x => result.Any(previous => previous.Url == x.Url))) break;
                result.AddRange(items);
            }
        }
        return result.DistinctBy(x => x.Url).OrderByDescending(x => JavaRuntime.SortKey(x.Version), StringComparer.Ordinal).ToArray();
    }

    /// <summary>按渠道解析实际发行记录，过滤不匹配的主版本与架构；完整性哈希、协议和 ZIP 格式必须通过验证。</summary>
    internal static IReadOnlyList<JavaPackage> Parse(JsonElement root, int major, JavaChannel channel)
    {
        var result = new List<JavaPackage>();
        if (channel == JavaChannel.Mojang)
        {
            if (!root.TryGetProperty("windows-x64", out var platform)) return result;
            foreach (var component in platform.EnumerateObject().Where(x => x.Name != "minecraft-java-exe"))
                foreach (var entry in component.Value.EnumerateArray())
                {
                    string version = entry.GetProperty("version").GetProperty("name").GetString()!;
                    if (JavaRuntime.ParseMajor(version) != major) continue;
                    var manifest = entry.GetProperty("manifest");
                    string hash = RequireHash(manifest.GetProperty("sha1").GetString(), 40);
                    result.Add(new(channel, major, version, hash, RequireHttps(manifest.GetProperty("url").GetString()!), hash, manifest.GetProperty("size").GetInt64()));
                }
        }
        else if (channel == JavaChannel.Temurin)
        {
            foreach (var release in root.EnumerateArray())
            {
                var data = release.GetProperty("version_data");
                if (data.GetProperty("major").GetInt32() != major) continue;
                foreach (var binary in release.GetProperty("binaries").EnumerateArray())
                {
                    if (binary.GetProperty("architecture").GetString() != "x64" || binary.GetProperty("os").GetString() != "windows" ||
                        binary.GetProperty("image_type").GetString() != "jdk") continue;
                    var package = binary.GetProperty("package");
                    string url = RequireHttps(package.GetProperty("link").GetString()!);
                    if (!new Uri(url).AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(new(channel, major, data.GetProperty("openjdk_version").GetString()!, release.GetProperty("release_name").GetString()!,
                        url, RequireHash(package.GetProperty("checksum").GetString(), 64), package.GetProperty("size").GetInt64()));
                }
            }
        }
        else
        {
            foreach (var package in root.EnumerateArray())
            {
                int[] numbers = package.GetProperty("java_version").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (numbers.Length == 0 || numbers[0] != major) continue;
                string name = package.GetProperty("name").GetString()!;
                if (!name.EndsWith("-win_x64.zip", StringComparison.OrdinalIgnoreCase) || name.Contains("-crac-") || name.Contains("-fx-")) continue;
                string id = package.GetProperty("package_uuid").GetString()!;
                if (!Guid.TryParse(id, out _)) throw new JsonException("Zulu 包标识无效。");
                result.Add(new(channel, major, string.Join('.', numbers) + "+" + package.GetProperty("openjdk_build_number").GetInt32(), id,
                    RequireHttps(package.GetProperty("download_url").GetString()!), null, 0, id));
            }
        }
        return result;
    }

    /// <summary>Zulu 列表不带 SHA-256，下载前查询选中包的详情并核对地址、版本及架构，避免下载其他包。</summary>
    public async Task<JavaPackage> ResolveAsync(JavaPackage package, CancellationToken token)
    {
        if (package.Channel != JavaChannel.Zulu) return package;
        if (!Guid.TryParse(package.PackageId, out _)) throw new JsonException("Zulu 包标识无效。");
        using var doc = JsonDocument.Parse(await _http.GetStringAsync("https://api.azul.com/metadata/v1/zulu/packages/" + package.PackageId, token));
        var detail = doc.RootElement;
        if (detail.GetProperty("download_url").GetString() != package.Url || detail.GetProperty("java_version")[0].GetInt32() != package.Major ||
            detail.GetProperty("os").GetString() != "windows" || detail.GetProperty("hw_bitness").GetInt32() != 64 || detail.GetProperty("arch").GetString() != "x86")
            throw new IOException("Zulu 详情与所选版本不一致。");
        return package with { Hash = RequireHash(detail.GetProperty("sha256_hash").GetString(), 64), Size = detail.GetProperty("size").GetInt64() };
    }

    /// <summary>官方元数据仅允许无凭据 HTTPS 地址，防止降级和本地文件协议。</summary>
    internal static string RequireHttps(string url)
    {
        if (DownloadTransport.ValidateUrl(url).Scheme != "https") throw new IOException("Java 下载地址必须使用 HTTPS。");
        return url;
    }

    /// <summary>要求指定长度的十六进制哈希，不接受空校验值。</summary>
    internal static string RequireHash(string? hash, int length) => hash is not null && Regex.IsMatch(hash, $"\\A[0-9a-fA-F]{{{length}}}\\z")
        ? hash : throw new IOException("Java 下载清单缺少有效的完整性校验值。");
}
