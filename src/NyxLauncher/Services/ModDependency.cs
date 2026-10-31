using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>发布声明的必需前置；平台允许只给版本编号，由查询服务补全所属项目。</summary>
public sealed record ModDependency(string? ProjectId, string? VersionId);

public sealed partial class ModrinthCatalogService
{
    /// <summary>读取发布中的必需依赖，排除可选、冲突和已内嵌项目；无平台引用的外部文件不列入卡片，非法编号抛出 JsonException 交给清单解析统一报错。</summary>
    private static IReadOnlyList<ModDependency> ParseDependencies(JsonElement version)
    {
        if (!version.TryGetProperty("dependencies", out var entries)) return [];
        var result = new List<ModDependency>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.GetProperty("dependency_type").GetString() != "required") continue;
            string? project = entry.GetProperty("project_id").GetString();
            string? id = entry.GetProperty("version_id").GetString();
            if (project is null && id is null) continue;
            if ((project is not null && !ValidId(project)) || (id is not null && !ValidId(id)))
                throw new JsonException("前置资源编号无效。");
            result.Add(new(project, id));
        }
        return result.Distinct().ToArray();
    }

    /// <summary>合并调用方已按游戏和加载器筛选的文件依赖；补全仅版本引用后批量读取项目卡片，缺失项目或坏响应报中文错误，取消向上传播。</summary>
    public async Task<IReadOnlyList<ResourceProject>> FetchRequiredProjectsAsync(
        IEnumerable<ModrinthModFile> files, CancellationToken token)
    {
        var dependencies = files.SelectMany(x => x.Dependencies).Distinct().ToArray();
        if (dependencies.Length == 0) return [];
        try
        {
            if (dependencies.Any(x => (x.ProjectId is not null && !ValidId(x.ProjectId)) ||
                (x.VersionId is not null && !ValidId(x.VersionId)))) throw new JsonException();
            var ids = dependencies.Where(x => x.ProjectId is not null).Select(x => x.ProjectId!).ToHashSet();
            string[] versions = dependencies.Where(x => x.ProjectId is null && x.VersionId is not null)
                .Select(x => x.VersionId!).Distinct().ToArray();
            if (versions.Length > 0)
            {
                using var document = await GetAsync("versions?ids=" + Uri.EscapeDataString(JsonSerializer.Serialize(versions)), token);
                var returned = new HashSet<string>();
                foreach (var version in document.RootElement.EnumerateArray())
                {
                    string id = version.GetProperty("id").GetString() ?? "";
                    string project = version.GetProperty("project_id").GetString() ?? "";
                    if (!versions.Contains(id) || !ValidId(project)) throw new JsonException();
                    returned.Add(id);
                    ids.Add(project);
                }
                if (returned.Count != versions.Length) throw new JsonException();
            }
            if (ids.Count == 0) return [];
            using var projects = await GetAsync("projects?ids=" + Uri.EscapeDataString(JsonSerializer.Serialize(ids)), token);
            var result = new List<ResourceProject>();
            foreach (var project in projects.RootElement.EnumerateArray())
            {
                string id = project.GetProperty("id").GetString() ?? "";
                string title = project.GetProperty("title").GetString() ?? "";
                long downloads = project.GetProperty("downloads").GetInt64();
                if (!ids.Contains(id) || string.IsNullOrWhiteSpace(title) || downloads < 0) throw new JsonException();
                string icon = project.GetProperty("icon_url").GetString() ?? "";
                if (!Uri.TryCreate(icon, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                    uri.Host != "cdn.modrinth.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0) icon = "";
                result.Add(new(id, title, project.GetProperty("description").GetString() ?? "", icon,
                    downloads, project.GetProperty("updated").GetDateTimeOffset(), [], [],
                    project.GetProperty("project_type").GetString() ?? "mod"));
            }
            if (result.Select(x => x.Id).Distinct().Count() != ids.Count) throw new JsonException();
            return result.DistinctBy(x => x.Id).OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new HttpRequestException("前置资源暂时不可用，请重试。", ex); }
    }

    /// <summary>限制平台编号为非空 base62 字符，禁止把依赖中的路径或查询内容带进请求。</summary>
    private static bool ValidId(string id) => id.Length > 0 && id.All(char.IsAsciiLetterOrDigit);
}
