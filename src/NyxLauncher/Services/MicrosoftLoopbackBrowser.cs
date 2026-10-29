using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;
using Microsoft.Identity.Client.Extensibility;

namespace NyxLauncher.Services;

/// <summary>仅使用系统浏览器的回环回调适配器；避开 HTTP.sys 权限依赖，保留无尾斜杠的精确注册路径。</summary>
internal sealed class MicrosoftLoopbackBrowser(Func<Uri, Task>? openBrowser = null, IProgress<Uri?>? authorization = null) : ICustomWebUi
{
    /// <summary>先绑定 IPv4 回环端口，再将授权地址交给界面打开或直接启动浏览器；界面接管时启动失败不影响监听，结束发布空地址并释放端口。</summary>
    public async Task<Uri> AcquireAuthorizationCodeAsync(Uri authorizationUri, Uri redirectUri, CancellationToken token)
    {
        if (redirectUri.Scheme != "http" || redirectUri.Host != "localhost" || redirectUri.Query.Length != 0 ||
            redirectUri.Fragment.Length != 0 || redirectUri.UserInfo.Length != 0)
            throw new InvalidOperationException("微软登录需要本机回环回调。");
        string expectedState = HttpUtility.ParseQueryString(authorizationUri.Query)["state"]
            ?? throw new InvalidOperationException("微软授权请求缺少 state。");
        if (authorizationUri.Scheme != "https" || authorizationUri.Host != "login.microsoftonline.com")
            throw new InvalidOperationException("微软授权地址不受支持。");
        using var listener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
        listener.Server.ExclusiveAddressUse = true;
        try { listener.Start(); }
        catch (SocketException) { throw new InvalidOperationException($"无法监听本机登录端口 {redirectUri.Port}，请关闭占用该端口的应用后重试。"); }
        token.ThrowIfCancellationRequested();
        try
        {
            if (authorization is not null) authorization.Report(authorizationUri);
            else if (openBrowser is not null) await openBrowser(authorizationUri);
            else
            {
                // 只把 MSAL 生成的 HTTPS 授权地址交给系统浏览器，不承载或读取用户密码页面。
                using var process = Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri) { UseShellExecute = true });
            }
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
                connection.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var stream = client.GetStream();
                    string? header = await ReadHeaderAsync(stream, connection.Token);
                    Uri? callback = ValidateCallback(header, redirectUri, expectedState);
                    byte[] content = Encoding.UTF8.GetBytes(callback is null ? "无效的登录回调，请返回原登录页面。" : "已收到登录响应，请返回启动器查看结果。此页面可以关闭。");
                    string status = callback is null ? "400 Bad Request" : "200 OK";
                    byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {content.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, connection.Token);
                    await stream.WriteAsync(content, connection.Token);
                    if (callback is not null) { token.ThrowIfCancellationRequested(); return callback; }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (IOException) { token.ThrowIfCancellationRequested(); }
            }
        }
        finally { authorization?.Report(null); }
    }

    /// <summary>最多读取 8 KiB 请求头，达到头尾即停止；拒绝超大或提前断开的请求，不读取正文及其他本机资源。</summary>
    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] buffer = new byte[8192];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) return null;
            length += read;
            string text = Encoding.ASCII.GetString(buffer, 0, length);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
        }
        return null;
    }

    /// <summary>仅接受精确回调上的 GET 和单值 OAuth 参数；错误 state 继续等待，不能终止用户正在进行的登录。</summary>
    private static Uri? ValidateCallback(string? header, Uri redirect, string state)
    {
        if (header is null) return null;
        string[] lines = header.Split("\r\n");
        string[] request = lines[0].Split(' ');
        if (request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1" || !request[1].StartsWith('/')) return null;
        var hosts = lines.Skip(1).Where(x => x.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hosts.Length != 1 || !string.Equals(hosts[0][5..].Trim(), redirect.Authority, StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(redirect.GetLeftPart(UriPartial.Authority) + request[1], UriKind.Absolute, out var callback) ||
            callback.AbsolutePath != redirect.AbsolutePath || callback.Fragment.Length != 0) return null;
        var query = HttpUtility.ParseQueryString(callback.Query);
        if (query.GetValues("state") is not { Length: 1 } states || states[0] != state) return null;
        bool hasCode = query.GetValues("code") is { Length: 1 } codes && !string.IsNullOrEmpty(codes[0]);
        bool hasError = query.GetValues("error") is { Length: 1 } errors && !string.IsNullOrEmpty(errors[0]);
        if (hasCode == hasError || query.GetValues("code")?.Length > 1 || query.GetValues("error")?.Length > 1) return null;
        return callback;
    }
}
