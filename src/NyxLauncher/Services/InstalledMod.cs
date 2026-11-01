using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>实际 mods 文件与可选平台摘要；未识别文件仍可启停、定位和删除。</summary>
public sealed record InstalledMod(string Path, string Sha1)
{
    public ResourceProject? Project { get; init; }
    public string? VersionId { get; init; }
    public DateTimeOffset Published { get; init; }
    public ModrinthModFile? Update { get; init; }
    public string UpdateLabel => "更新至 " + (Update?.VersionNumber ?? "");
    public string UpdateVisibility => Update is null ? "Collapsed" : "Visible";
    public bool Disabled => Path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Title => Project?.Title ?? FileName;
    public string TitleDecorations => Disabled ? "Strikethrough" : "None";
    public double ContentOpacity => Disabled ? 0.45 : 1;
    public string DisabledLineVisibility => Disabled ? "Visible" : "Collapsed";
    public string Description => Project?.Description ?? "";
    public string State => Disabled ? "已禁用" : "已启用";
    public string ToggleLabel => Disabled ? "启用" : "禁用";
    public string ToggleGlyph => Disabled ? "\uE73E" : "\uECE4";
}

public sealed partial class ModrinthCatalogService
{
    /// <summary>按当前游戏、加载器和正式版渠道批量查询最新兼容发布；不加载各项目历史列表，响应仍经本地兼容、时间和哈希校验。</summary>
    public async Task<IReadOnlyList<InstalledMod>> FindModUpdatesAsync(IReadOnlyList<InstalledMod> mods, InstalledVersion version, CancellationToken token)
    {
        string loader = version.Loader.Split(':')[0].Trim().ToLowerInvariant();
        if (!LoaderIds.Contains(loader)) return mods;
        var updates = new Dictionary<string, ModrinthModFile?>();
        foreach (var batch in mods.Where(x => x.Project is not null).DistinctBy(x => x.Sha1).Chunk(100))
        {
            using var response = await GetAsync("version_files/update", token, JsonSerializer.Serialize(new
            {
                hashes = batch.Select(x => x.Sha1), algorithm = "sha1", loaders = new[] { loader },
                game_versions = new[] { version.GameVersion }, version_types = new[] { "release" }
            }));
            foreach (var item in response.RootElement.EnumerateObject())
            {
                var mod = batch.SingleOrDefault(x => x.Sha1 == item.Name) ?? throw new JsonException("平台返回了未请求的更新。");
                var files = ParseModFiles([item.Value], mod.Project!.Id);
                updates[item.Name] = ModManagementService.SelectUpdate(mod, version, files);
            }
        }
        return mods.Select(mod => mod with { Update = updates.GetValueOrDefault(mod.Sha1) }).ToArray();
    }

    /// <summary>每批最多查询 100 个本地哈希，再批量取得名称、简介和图标；不请求发布列表、依赖、图库或正文详情，未知哈希保持本地项。</summary>
    public async Task<IReadOnlyList<InstalledMod>> IdentifyModsAsync(IReadOnlyList<InstalledMod> mods, CancellationToken token)
    {
        var result = new List<InstalledMod>();
        foreach (var batch in mods.Chunk(100))
        {
            using var versions = await GetAsync("version_files", token,
                JsonSerializer.Serialize(new { hashes = batch.Select(x => x.Sha1).Distinct(), algorithm = "sha1" }));
            var matches = new Dictionary<string, (string Project, string Version, DateTimeOffset Published)>();
            foreach (var item in versions.RootElement.EnumerateObject())
            {
                if (!batch.Any(x => x.Sha1 == item.Name)) throw new JsonException("平台返回了未请求的文件。");
                string project = item.Value.GetProperty("project_id").GetString() ?? "";
                string version = item.Value.GetProperty("id").GetString() ?? "";
                if (!ValidId(project) || !ValidId(version)) throw new JsonException("模组编号无效。");
                matches.Add(item.Name, (project, version, item.Value.GetProperty("date_published").GetDateTimeOffset()));
            }
            var summaries = new Dictionary<string, ResourceProject>();
            if (matches.Count > 0)
            {
                var ids = matches.Values.Select(x => x.Project).Distinct().ToArray();
                using var projects = await GetAsync("projects?ids=" + Uri.EscapeDataString(JsonSerializer.Serialize(ids)), token);
                foreach (var item in projects.RootElement.EnumerateArray())
                {
                    string id = item.GetProperty("id").GetString() ?? "";
                    string title = item.GetProperty("title").GetString() ?? "";
                    if (!ids.Contains(id) || string.IsNullOrWhiteSpace(title)) throw new JsonException("模组摘要无效。");
                    string icon = item.GetProperty("icon_url").GetString() ?? "";
                    if (!Uri.TryCreate(icon, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                        uri.Host != "cdn.modrinth.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0) icon = "";
                    summaries[id] = new(id, title, item.GetProperty("description").GetString() ?? "", icon, 0, default, [], []);
                }
            }
            result.AddRange(batch.Select(mod => matches.TryGetValue(mod.Sha1, out var match) && summaries.TryGetValue(match.Project, out var project)
                ? mod with { Project = project, VersionId = match.Version, Published = match.Published } : mod));
        }
        return result;
    }
}
