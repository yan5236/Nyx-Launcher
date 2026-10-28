using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;

namespace NyxLauncher.Services;

/// <summary>将游戏资源及加载器处理器依赖接到启动器的统一下载引擎。</summary>
internal sealed class GameDownloadInstaller(DownloadTransport transport, string root) : IGameInstaller
{
    /// <summary>按路径去重，使用本批传输器的并行文件上限下载资源，校验后执行后处理；任一失败取消同批工作并保留错误原因。</summary>
    public async ValueTask Install(IEnumerable<GameFile> gameFiles, IProgress<InstallerProgressChangedEventArgs>? fileProgress,
        IProgress<ByteProgress>? byteProgress, CancellationToken cancellationToken)
    {
        var files = gameFiles.Where(x => !string.IsNullOrEmpty(x.Path))
            .DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        int completed = 0;
        Exception? failure = null;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try { await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = transport.MaxParallelDownloads, CancellationToken = pending.Token },
            async (file, token) =>
            {
                try
                {
                    ValidateDestination(root, file);
                    if (!string.IsNullOrEmpty(file.Url))
                        await transport.DownloadAsync(DownloadTransport.ValidateUrl(file.Url), file.Path!, file.Hash, file.Size,
                            true, false, null, token);
                    else if (!await DownloadTransport.ValidAsync(file.Path!, file.Hash, file.Size, token))
                        throw new IOException("安装依赖缺失且没有下载地址：" + file.Name);
                    await file.ExecuteUpdateTask(token);
                    fileProgress?.Report(new(files.Length, Interlocked.Increment(ref completed), file.Name, InstallerEventType.Done));
                }
                catch (Exception ex)
                {
                    if (!pending.IsCancellationRequested) Interlocked.CompareExchange(ref failure, new IOException("下载依赖失败：" + file.Name + "；" + ex.Message, ex), null);
                    pending.Cancel(); throw;
                }
            }); }
        catch when (failure is not null && !cancellationToken.IsCancellationRequested)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); throw; }
    }

    /// <summary>限制远程清单文件写入游戏根目录；只额外允许官方安装器在系统临时目录的一层随机目录中下载固定安装包名称。</summary>
    internal static void ValidateDestination(string root, GameFile file)
    {
        string target = Path.GetFullPath(file.Path!);
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        bool inGame = target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        bool installer = Path.GetFileName(target) is "installer.jar" or "installer.zip" &&
            string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(target)), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(file.Url, UriKind.Absolute, out var url) && url.Scheme == "https" &&
            url.Host is "maven.minecraftforge.net" or "maven.neoforged.net";
        if (!inGame && !installer) throw new IOException("安装清单的文件路径超出了允许目录。");
        GameInstallationService.EnsureNoLinks(target);
    }
}

/// <summary>游戏清单请求使用独立版本源设置；缓冲小型 JSON 后回退，避免给解析器交付半份清单。</summary>
internal sealed class InstallMetadataHandler(SourceStrategy strategy) : HttpMessageHandler
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>仅处理安装器读取清单的 GET 请求，限制为 32 MiB 并验证 JSON；两源失败保留错误原因。</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Uri original = request.RequestUri!;
        Uri? mirror = DownloadTransport.Mirror(original);
        // 读取单份元数据至有界内存，保证慢源回退依据完整有效 JSON 而非仅响应头。
        async Task<byte[]> Fetch(bool useMirror, CancellationToken cancellation)
        {
            using var response = await _client.GetAsync(useMirror ? mirror ?? original : original,
                HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            using var input = await response.Content.ReadAsStreamAsync(cancellation);
            using var output = new MemoryStream();
            byte[] buffer = new byte[32768];
            int count;
            while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
            {
                if (output.Length + count > 32 * 1024 * 1024) throw new IOException("安装清单过大。");
                output.Write(buffer, 0, count);
            }
            byte[] bytes = output.ToArray();
            using var json = System.Text.Json.JsonDocument.Parse(bytes);
            return bytes;
        }
        var data = mirror is null ? await Fetch(false, token) :
            await SourceFallback.FetchAsync(strategy, Fetch, TimeSpan.FromSeconds(3), token);
        return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(data), RequestMessage = request };
    }

    /// <summary>释放元数据请求的连接池；由安装服务随本次工作生命周期调用。</summary>
    protected override void Dispose(bool disposing) { if (disposing) _client.Dispose(); base.Dispose(disposing); }
}
