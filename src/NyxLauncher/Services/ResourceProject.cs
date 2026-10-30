using System.Globalization;

namespace NyxLauncher.Services;

/// <summary>资源搜索结果及卡片显示数据；非模组分类不会携带加载器。</summary>
public sealed record ResourceProject(string Id, string Title, string Description, string IconUrl,
    long Downloads, DateTimeOffset Updated, IReadOnlyList<string> Versions, IReadOnlyList<string> Loaders, string Category = "mod")
{
    public string DownloadText => Downloads >= 100000000 ? (Downloads / 100000000d).ToString("0.#", CultureInfo.InvariantCulture) + "亿"
        : Downloads >= 10000 ? (Downloads / 10000d).ToString("0.#", CultureInfo.InvariantCulture) + "万"
        : Downloads.ToString("N0", CultureInfo.InvariantCulture);
    public string UpdatedText => Updated.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public IReadOnlyList<string>? ReleaseVersions { get; init; }
    public string VersionsText => ResourceVersionSummary.Format(Versions, ReleaseVersions);
    public string VersionsDetail => string.Join("、", Versions.Reverse());
    public string LoadersText => string.Join(" / ", Loaders.Select(LoaderName));
    public bool HasLoaders => Loaders.Count > 0;
    public IReadOnlyList<string> LoaderIcons => Loaders.Select(loader => loader switch
    {
        "fabric" => "ms-appx:///Assets/fabric.png",
        "forge" => "ms-appx:///Assets/forge.png",
        "neoforge" => "ms-appx:///Assets/neoforge.png",
        "quilt" => "ms-appx:///Assets/quilt.svg",
        _ => "ms-appx:///Assets/loader.svg"
    }).ToArray();

    /// <summary>把 API 加载器标识转换为正式产品名；不翻译品牌名，未知项保留原值。</summary>
    public static string LoaderName(string loader) => loader switch
    {
        "fabric" => "Fabric", "forge" => "Forge", "neoforge" => "NeoForge", "quilt" => "Quilt",
        "liteloader" => "LiteLoader", "rift" => "Rift", _ => loader
    };
}

/// <summary>单页资源及匹配总数，用于显式翻页而不截断搜索范围。</summary>
public sealed record ResourceSearchPage(IReadOnlyList<ResourceProject> Projects, int Total);

/// <summary>远程 Minecraft 版本及中文显示标签；传给平台的值仍为原始版本号。</summary>
public sealed record ResourceGameVersion(string Id, string Label);

/// <summary>平台资源类型及中文标签，保留项目分类以避免跨资源混用。</summary>
public sealed record ResourceCategory(string Id, string Label, string ProjectType);
