namespace NyxLauncher.Services;

/// <summary>来自真实清单的加载器版本；保留完整坐标 ID，展示版本号、发布日期或稳定性及对应图标。</summary>
public sealed record LoaderVersion(LoaderKind Loader, string Id, string Version, string Detail)
{
    public string Icon => $"ms-appx:///Assets/{Loader.ToString().ToLowerInvariant()}.png";
    public string AccessibleName => $"{Loader} {Version}，{Detail}";
}
