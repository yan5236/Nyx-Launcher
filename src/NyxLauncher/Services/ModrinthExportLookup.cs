using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>经精确哈希匹配的官方资源下载信息。</summary>
public sealed record ModpackExportDownload(string Sha1, string Sha512, long Size, string Url);

public sealed partial class ModrinthCatalogService
{
    /// <summary>批量按 SHA-1 查找原文件，校验 CDN、大小与双哈希；未知哈希缺席，网络或响应错误向上传播，不能当成未收录。</summary>
    public async Task<IReadOnlyDictionary<string, ModpackExportDownload>> FindExportFilesAsync(IEnumerable<string> hashes, CancellationToken token)
    {
        var result = new Dictionary<string, ModpackExportDownload>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in hashes.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(100))
        {
            using var response = await GetAsync("version_files", token, JsonSerializer.Serialize(new { hashes = batch, algorithm = "sha1" }));
            foreach (var item in response.RootElement.EnumerateObject())
            {
                if (!batch.Contains(item.Name, StringComparer.OrdinalIgnoreCase)) throw new JsonException("平台返回了未请求的文件。");
                var file = item.Value.GetProperty("files").EnumerateArray().Single(x =>
                    string.Equals(x.GetProperty("hashes").GetProperty("sha1").GetString(), item.Name, StringComparison.OrdinalIgnoreCase));
                string sha512 = file.GetProperty("hashes").GetProperty("sha512").GetString() ?? "";
                string url = file.GetProperty("url").GetString() ?? "";
                long size = file.GetProperty("size").GetInt64();
                if (sha512.Length != 128 || !sha512.All(Uri.IsHexDigit) || size < 0 ||
                    !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                    uri.Host != "cdn.modrinth.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
                    throw new JsonException("平台文件校验信息或下载地址无效。");
                result.Add(item.Name, new(item.Name, sha512, size, url));
            }
        }
        return result;
    }
}
