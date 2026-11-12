using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NyxLauncher.Services;

/// <summary>经官方仓库地址、版本、大小和 SHA-256 校验的单文件更新元数据。</summary>
public sealed record LauncherRelease(string Version, string Notes, Uri Page, Uri Download, long Size, string Sha256);

/// <summary>只在用户点击时读取 GitHub 最新正式发布，不携带账户信息，不执行自动更新。</summary>
public sealed class LauncherUpdateService
{
    public const string AssetName = "NyxLauncher.exe";
    public const string LatestUrl = "https://api.github.com/repos/yan5236/Nyx-Launcher/releases/latest";

    /// <summary>查询最新正式版；404 表示还没有发布，网络、限流、损坏清单或不完整产物明确报错，可注入客户端做协议检查。</summary>
    public static async Task<LauncherRelease?> FetchAsync(CancellationToken token, HttpClient? client = null)
    {
        using var owned = client is null ? DownloadHttpClient.Create(TimeSpan.FromSeconds(20)) : null;
        var http = client ?? owned!;
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.UserAgent.ParseAdd(ResourceMirror.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return Parse(document.RootElement);
    }

    /// <summary>校验 GitHub 响应及产物归属；仅接受本仓库正式发布的固定 x64 EXE 和 GitHub 提供的 SHA-256，拒绝缺失或含糊的产物。</summary>
    internal static LauncherRelease Parse(JsonElement root)
    {
        string tag = root.GetProperty("tag_name").GetString() ?? throw new JsonException("发布缺少版本号。");
        var version = ReleaseVersion.Parse(tag);
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() || version.Preview.Length > 0)
            throw new JsonException("最新正式版接口返回了草稿或预发布版本。");
        string expectedPage = LauncherInfo.Repository + "/releases/tag/" + Uri.EscapeDataString(tag);
        var page = RequireUrl(root.GetProperty("html_url").GetString(), expectedPage);
        var matches = root.GetProperty("assets").EnumerateArray().Where(item => item.GetProperty("name").GetString() == AssetName).ToArray();
        if (matches.Length != 1) throw new JsonException("发布缺少唯一的 Windows x64 单文件，请在 GitHub 发布页确认产物。");
        var asset = matches[0];
        var download = RequireUrl(asset.GetProperty("browser_download_url").GetString(), LauncherInfo.Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + AssetName);
        long size = asset.GetProperty("size").GetInt64();
        string digest = asset.TryGetProperty("digest", out var node) ? node.GetString() ?? "" : "";
        if (size is <= 0 or > 2L * 1024 * 1024 * 1024 || !Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z"))
            throw new JsonException("更新产物缺少有效大小或 SHA-256，无法安全更新。");
        string notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : "";
        return new(tag.TrimStart('v', 'V'), string.IsNullOrWhiteSpace(notes) ? "此版本未提供更新说明，可查看 GitHub 发布页。" : notes, page, download, size, digest[7..]);
    }

    /// <summary>只允许指定仓库和路径的标准 HTTPS 地址；远端清单不能引导更新程序到其他站点或注入查询、凭据。</summary>
    private static Uri RequireUrl(string? value, string expected)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.AbsoluteUri != expected)
            throw new JsonException("更新地址不属于官方发布路径。");
        return uri;
    }
}
