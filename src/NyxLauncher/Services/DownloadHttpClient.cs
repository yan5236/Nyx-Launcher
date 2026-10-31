namespace NyxLauncher.Services;

/// <summary>公共文件与清单复用进程级连接池，避免批次结束反复关闭代理连接；不用于账户认证。</summary>
internal static class DownloadHttpClient
{
    private static readonly SocketsHttpHandler Handler = new()
    {
        UseCookies = false,
        MaxConnectionsPerServer = 64,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
    };

    /// <summary>创建独立超时和取消作用域的客户端；释放客户端不销毁共享池，继续使用系统代理与默认 TLS 校验。</summary>
    internal static HttpClient Create(TimeSpan timeout) => new(Handler, disposeHandler: false) { Timeout = timeout };
}
