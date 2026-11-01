using System.Globalization;
using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>Modrinth 的运行用模组文件；保留完整兼容范围及上游校验信息，不包含源码和开发附件。</summary>
public sealed record ModrinthModFile(string ProjectId, string VersionId, string FileName,
    DateTimeOffset Published, string Channel, IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders, Uri Url, string Sha1, long Size)
{
    public string VersionNumber { get; init; } = "";
    public bool IsModpack { get; init; }
    public string Category { get; init; } = "mod";
    public bool IsResource => Category is "resourcepack" or "shader" or "datapack";
    public IReadOnlyList<ModDependency> Dependencies { get; init; } = [];
    public string ChannelLabel => Channel switch { "release" => "正式版", "beta" => "测试版", _ => "预览版" };
    public string Badge => Channel switch { "release" => "R", "beta" => "B", _ => "A" };
    public string Detail => Published.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " · " + ChannelLabel;

    /// <summary>在清单解析及创建任务时共同验证文件契约；兼容官方旧链接的重复起始斜杠及大小 0（未知），仍要求有效 SHA-1、单级 JAR、ZIP 或 MRPACK 名和项目官方 CDN 路径。</summary>
    internal void Validate()
    {
        GameInstallationService.ValidateName(FileName);
        if (string.IsNullOrEmpty(ProjectId) || !ProjectId.All(char.IsAsciiLetterOrDigit) ||
            string.IsNullOrEmpty(VersionId) || !VersionId.All(char.IsAsciiLetterOrDigit) ||
            !FileName.EndsWith(IsModpack ? ".mrpack" : IsResource ? ".zip" : ".jar", StringComparison.OrdinalIgnoreCase) ||
            Channel is not ("release" or "beta" or "alpha") || Size < 0 ||
            Sha1 is null || Sha1.Length != 40 || !Sha1.All(Uri.IsHexDigit) ||
            GameVersions.Count == 0 || GameVersions.Any(string.IsNullOrWhiteSpace) ||
            Loaders.Count == 0 || Loaders.Any(string.IsNullOrWhiteSpace) ||
            !Url.IsAbsoluteUri || Url.Scheme != "https" || Url.Host != "cdn.modrinth.com" ||
            !Url.IsDefaultPort || Url.UserInfo.Length > 0 ||
            !Url.AbsolutePath.TrimStart('/').StartsWith($"data/{ProjectId}/versions/", StringComparison.Ordinal))
            throw new JsonException("资源文件元数据无效。");
    }
}

/// <summary>资源目录的模组发布查询；复用目录服务的连接、超时、响应大小限制和错误提示。</summary>
public sealed partial class ModrinthCatalogService
{
    /// <summary>获取项目全部发布，按资源类别选择 ZIP、MRPACK 或运行 JAR，保留兼容范围和必需前置，按更新日期倒序返回；不以搜索摘要代替文件信息，主动取消向上传播。</summary>
    public async Task<IReadOnlyList<ModrinthModFile>> FetchModFilesAsync(string projectId, CancellationToken token, bool modpack = false, string category = "mod")
    {
        if (string.IsNullOrEmpty(projectId) || !projectId.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("模组项目编号无效。", nameof(projectId));
        using var document = await GetAsync($"project/{projectId}/version?include_changelog=false", token);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new HttpRequestException("资源版本列表暂时不可用，请重试。");
        return ParseModFiles(document.RootElement.EnumerateArray(), projectId, modpack, category);
    }

    /// <summary>共用发布文件解析与校验，供项目历史列表和批量更新响应使用；只接受所属项目的运行文件，按发布时间倒序保留首选文件。</summary>
    private static IReadOnlyList<ModrinthModFile> ParseModFiles(IEnumerable<JsonElement> versions, string projectId, bool modpack = false, string category = "mod")
    {
        try
        {
            bool resource = category is "resourcepack" or "shader" or "datapack";
            var result = new List<ModrinthModFile>();
            foreach (var version in versions)
            {
                if (version.GetProperty("project_id").GetString() != projectId) throw new JsonException();
                string[] games = version.GetProperty("game_versions").EnumerateArray().Select(x => x.GetString()!).Distinct().ToArray();
                string[] loaders = version.GetProperty("loaders").EnumerateArray().Select(x => x.GetString()!)
                    .Where(x => resource || LoaderIds.Contains(x)).Distinct().ToArray();
                if (loaders.Length == 0) continue;
                var dependencies = ParseDependencies(version);
                foreach (var file in version.GetProperty("files").EnumerateArray().OrderByDescending(x => x.GetProperty("primary").GetBoolean()))
                {
                    string name = file.GetProperty("filename").GetString() ?? throw new JsonException();
                    if (!name.EndsWith(modpack ? ".mrpack" : resource ? ".zip" : ".jar", StringComparison.OrdinalIgnoreCase)) continue;
                    if (file.TryGetProperty("file_type", out var type) && type.ValueKind != JsonValueKind.Null &&
                        type.GetString() is not ("" or "unknown")) continue;
                    var item = new ModrinthModFile(projectId, version.GetProperty("id").GetString()!, name,
                        version.GetProperty("date_published").GetDateTimeOffset(), version.GetProperty("version_type").GetString()!,
                        games, loaders, new Uri(file.GetProperty("url").GetString()!),
                        file.GetProperty("hashes").GetProperty("sha1").GetString()!, file.GetProperty("size").GetInt64())
                    { Dependencies = dependencies, IsModpack = modpack, Category = category,
                        VersionNumber = version.TryGetProperty("version_number", out var number) ? number.GetString() ?? name : name };
                    item.Validate();
                    result.Add(item);
                }
            }
            return result.OrderByDescending(x => x.Published).DistinctBy(x => (x.VersionId, x.FileName)).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or
            FormatException or ArgumentException or OverflowException)
        { throw new HttpRequestException("资源版本列表暂时不可用，请重试。", ex); }
    }
}
