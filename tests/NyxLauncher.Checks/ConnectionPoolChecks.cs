using System.Net;
using System.Net.Sockets;
using NyxLauncher.Services;

internal static class ConnectionPoolChecks
{
    /// <summary>通过真实回环 TCP 验证客户端释放后下一批仍复用连接；不依赖外网或本机代理配置。</summary>
    internal static async Task RunAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            for (int i = 0; i < 2; i++)
            {
                string? line;
                do { line = await reader.ReadLineAsync(deadline.Token); }
                while (!string.IsNullOrEmpty(line));
                if (line is null) throw new IOException("批次结束关闭了 TCP 连接。");
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK"u8.ToArray(), deadline.Token);
            }
        });
        for (int i = 0; i < 2; i++)
        {
            using var client = DownloadHttpClient.Create(TimeSpan.FromSeconds(5));
            if (await client.GetStringAsync($"http://127.0.0.1:{port}/", deadline.Token) != "OK")
                throw new Exception("连接池响应内容错误。");
        }
        await server;
        Console.WriteLine("跨批次真实 TCP 连接复用检查通过。");
    }
}
