using System.Globalization;
using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>Modrinth 的运行用模组文件；保留完整兼容范围及上游校验信息，不包含源码和开发附件。</summary>
public sealed record ModrinthModFile(string ProjectId, string VersionId, string FileName,
    DateTimeOffset Published, string Channel, IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders, Uri Url, string Sha1, long Size)
{
    public IReadOnlyList<ModDependency> Dependencies { get; init; } = [];
    public string ChannelLabel => Channel switch { "release" => "正式版", "beta" => "测试版", _ => "预览版" };
    public string Badge => Channel switch { "release" => "R", "beta" => "B", _ => "A" };
    public string Detail => Published.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " · " + ChannelLabel;

    /// <summary>在清单解析及创建任务时共同验证文件契约；兼容官方旧链接的重复起始斜杠及大小 0（未知），仍要求有效 SHA-1、单级 JAR 名和项目官方 CDN 路径。</summary>
    internal void Validate()
    {
        GameInstallationService.ValidateName(FileName);
        if (string.IsNullOrEmpty(ProjectId) || !ProjectId.All(char.IsAsciiLetterOrDigit) ||
            string.IsNullOrEmpty(VersionId) || !VersionId.All(char.IsAsciiLetterOrDigit) ||
            !FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            Channel is not ("release" or "beta" or "alpha") || Size < 0 ||
            Sha1 is null || Sha1.Length != 40 || !Sha1.All(Uri.IsHexDigit) ||
            GameVersions.Count == 0 || GameVersions.Any(string.IsNullOrWhiteSpace) ||
            Loaders.Count == 0 || Loaders.Any(string.IsNullOrWhiteSpace) ||
            !Url.IsAbsoluteUri || Url.Scheme != "https" || Url.Host != "cdn.modrinth.com" ||
            !Url.IsDefaultPort || Url.UserInfo.Length > 0 ||
            !Url.AbsolutePath.TrimStart('/').StartsWith($"data/{ProjectId}/versions/", StringComparison.Ordinal))
            throw new JsonException("模组文件元数据无效。");
    }
}

/// <summary>资源目录的模组发布查询；复用目录服务的连接、超时、响应大小限制和错误提示。</summary>
public sealed partial class ModrinthCatalogService
{
    /// <summary>获取项目全部发布，保留每个运行 JAR 的兼容范围和必需前置，按更新日期倒序返回；不以搜索摘要代替文件信息，主动取消向上传播。</summary>
    public async Task<IReadOnlyList<ModrinthModFile>> FetchModFilesAsync(string projectId, CancellationToken token)
    {
        if (string.IsNullOrEmpty(projectId) || !projectId.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("模组项目编号无效。", nameof(projectId));
        using var document = await GetAsync($"project/{projectId}/version?include_changelog=false", token);
        try
        {
            var result = new List<ModrinthModFile>();
            foreach (var version in document.RootElement.EnumerateArray())
            {
                if (version.GetProperty("project_id").GetString() != projectId) throw new JsonException();
                string[] games = version.GetProperty("game_versions").EnumerateArray().Select(x => x.GetString()!).Distinct().ToArray();
                string[] loaders = version.GetProperty("loaders").EnumerateArray().Select(x => x.GetString()!)
                    .Where(LoaderIds.Contains).Distinct().ToArray();
                if (loaders.Length == 0) continue;
                var dependencies = ParseDependencies(version);
                foreach (var file in version.GetProperty("files").EnumerateArray().OrderByDescending(x => x.GetProperty("primary").GetBoolean()))
                {
                    string name = file.GetProperty("filename").GetString() ?? throw new JsonException();
                    if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                    if (file.TryGetProperty("file_type", out var type) && type.ValueKind != JsonValueKind.Null &&
                        type.GetString() is not ("" or "unknown")) continue;
                    var item = new ModrinthModFile(projectId, version.GetProperty("id").GetString()!, name,
                        version.GetProperty("date_published").GetDateTimeOffset(), version.GetProperty("version_type").GetString()!,
                        games, loaders, new Uri(file.GetProperty("url").GetString()!),
                        file.GetProperty("hashes").GetProperty("sha1").GetString()!, file.GetProperty("size").GetInt64())
                    { Dependencies = dependencies };
                    item.Validate();
                    result.Add(item);
                }
            }
            return result.OrderByDescending(x => x.Published).DistinctBy(x => (x.VersionId, x.FileName)).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or
            FormatException or ArgumentException or OverflowException)
        { throw new HttpRequestException("模组版本列表暂时不可用，请重试。", ex); }
    }
}
