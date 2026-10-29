using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>缓存启动所需的 Java 元数据，并补齐底层库未传递取消令牌的网络调用。</summary>
internal sealed class LaunchMetadataHandler(string root, SourceStrategy source, CancellationToken lifetime) : HttpMessageHandler
{
    private readonly HttpClient _client = new(new InstallMetadataHandler(source));

    /// <summary>优先读取上次成功取得的有界 JSON；首次下载原子保存，损坏缓存重新获取，整次启动取消时中止所有请求。</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        var token = pending.Token;
        token.ThrowIfCancellationRequested();
        string cache = Path.Combine(root, ".nyx-launch", "metadata", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.RequestUri!.AbsoluteUri))) + ".json");
        GameInstallationService.EnsureNoLinks(cache);
        byte[]? data = null;
        if (File.Exists(cache) && new FileInfo(cache).Length <= 32 * 1024 * 1024)
        {
            try { data = await File.ReadAllBytesAsync(cache, token); using var json = JsonDocument.Parse(data); }
            catch (JsonException) { data = null; }
        }
        if (data is null)
        {
            data = await _client.GetByteArrayAsync(request.RequestUri, token);
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            string temp = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temp, data, token); File.Move(temp, cache, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data), RequestMessage = request };
    }

    /// <summary>释放本次启动的 HTTP 管道，已保存的元数据供下一次离线启动复用。</summary>
    protected override void Dispose(bool disposing) { if (disposing) _client.Dispose(); base.Dispose(disposing); }
}
