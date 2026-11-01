using System.Diagnostics;

namespace NyxLauncher.Services;

/// <summary>记录网络请求的目标主机、耗时、状态及安全异常链，禁止记录路径、查询、请求头和正文。</summary>
internal sealed class NetworkDiagnosticHandler(HttpMessageHandler inner, bool ownsInner = true) : DelegatingHandler(inner)
{
    private static long _successes;
    private static readonly Lazy<GameLaunchLog> Log = new(() => new GameLaunchLog(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NyxLauncher")));

    /// <summary>记录前 200 次成功请求及所有失败的关联 ID，避免资源下载淹没日志；失败保留原异常及取消语义，不做隐式直连或认证重试。</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        var watch = Stopwatch.StartNew();
        string target = $"{request.Method} {request.RequestUri?.Scheme}://{request.RequestUri?.IdnHost}:{request.RequestUri?.Port}";
        try
        {
            var response = await base.SendAsync(request, token);
            if (!response.IsSuccessStatusCode || Interlocked.Increment(ref _successes) <= 200) Log.Value.Write($"{id} {target} status={(int)response.StatusCode} elapsedMs={watch.ElapsedMilliseconds}");
            return response;
        }
        catch (Exception error)
        {
            Log.Value.Write($"{id} {target} failed elapsedMs={watch.ElapsedMilliseconds} cancelled={token.IsCancellationRequested}\n{NetworkFailureDetails.Format(error)}");
            throw;
        }
    }

    /// <summary>仅在拥有底层处理器时释放它；共享下载池由进程生命周期持有。</summary>
    protected override void Dispose(bool disposing) { if (ownsInner) base.Dispose(disposing); }
}
