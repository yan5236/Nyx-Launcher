namespace NyxLauncher.Services;

/// <summary>MCIM 的官方地址映射与已登记的客户端标识；资源模型始终保留原始下载地址。</summary>
internal static class ResourceMirror
{
    public static readonly string UserAgent = "Nyx-Launcher/" +
        (typeof(ResourceMirror).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    /// <summary>仅转换标准 HTTPS 官方域名，保留查询与转义；拒绝凭据和自定义端口，mediafilez 按 MCIM 文档不替换。</summary>
    public static Uri? Map(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return null;
        string? prefix = uri.Host switch
        {
            "api.modrinth.com" => "/modrinth",
            "api.curseforge.com" => "/curseforge",
            "cdn.modrinth.com" or "edge.forgecdn.net" => "",
            _ => null
        };
        return prefix is null ? null : new Uri("https://mod.mcimirror.top" + prefix + "/" + uri.PathAndQuery.TrimStart('/'));
    }
}
