using Microsoft.Identity.Client;

namespace NyxLauncher.Services;

/// <summary>为微软令牌请求提供独立且长期复用的诊断客户端，不与下载或游戏认证共享认证状态。</summary>
internal sealed class MicrosoftHttpClientFactory : IMsalHttpClientFactory
{
    private static readonly HttpClient Client = new(new NetworkDiagnosticHandler(new SocketsHttpHandler
    {
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }));

    /// <summary>返回进程级客户端供 MSAL 使用；请求取消仍由 MSAL 传递，不在调用后释放客户端。</summary>
    public HttpClient GetHttpClient() => Client;
}
