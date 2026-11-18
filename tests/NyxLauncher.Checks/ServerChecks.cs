using System.Net;
using System.Net.Sockets;
using System.Text;
using NyxLauncher.Services;

internal static class ServerChecks
{
    /// <summary>验证地址边界、配置重启恢复、精选约束及 SRV 等待回退，然后通过回环服务器验证状态握手及 Ping/Pong。</summary>
    internal static async Task RunAsync()
    {
        var ipv6 = ServerEndpoint.Parse("[::1]:25570");
        Check(ipv6.Host == "::1" && ipv6.Port == 25570 && ServerEndpoint.Parse("::1").Port == 25565, "IPv6 与端口");
        Check(ServerEndpoint.Parse("示例.cn").Host == "xn--fsq092h.cn", "国际域名");
        foreach (string invalid in new[] { "", "foo bar", "https://example.org", "foo:0", "foo:65536", "[example.org]", "host.." })
        {
            try { _ = ServerEndpoint.Parse(invalid); }
            catch (ArgumentException) { continue; }
            throw new Exception("无效服务器地址被接受：" + invalid);
        }

        string root = Path.Combine(Path.GetTempPath(), "nyx-servers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "servers.json");
            var store = new ServerStore(path);
            var custom = new ServerEntry("custom", "自定义", "localhost:25566", Path.Combine(root, "test"));
            store.Save(custom);
            var featured = store.Entries(true).First();
            store.Save(featured with { Name = "新名称", Address = "localhost", VersionPath = custom.VersionPath });
            store.Delete(featured);
            store = new ServerStore(path);
            Check(store.Entries(false).Single() == custom, "自定义重启后恢复地址和默认版本");
            Check(store.Entries(true).First() == featured with { Name = "新名称", VersionPath = custom.VersionPath }, "精选只保存名称和版本");
            store.Delete(custom);
            Check(!new ServerStore(path).Entries(false).Any() && new ServerStore(path).Entries(true).Count() == 4, "删除仅影响自定义项");
            await CheckLaunchArgumentsAsync(root, ipv6);
        }
        finally { Directory.Delete(root, true); }

        await CheckSrvFallbackAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ReplyAsync(listener, timeout.Token);
        var query = ServerStatusProbe.QueryAsync(ServerEndpoint.Parse($"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"), timeout.Token);
        await server;
        var status = await query;
        Check(status is { Version: "1.21", Players: "2/20", Latency: >= 0 } &&
            status.Description.SequenceEqual(new ServerTextSpan[] { new("测试", 0x55FF55), new(" 服务器", null) }), "彩色 MOTD、状态和 Ping/Pong 往返");
        using var motd = System.Text.Json.JsonDocument.Parse("""
            {"text":"标题","color":"gold","extra":[{"text":"\n副标题"},{"text":" RGB","color":"#12ABCd"},"§x§1§2§3§a§b§c彩§r§l普通"]}
            """);
        Check(ServerMotd.Parse(motd.RootElement).SequenceEqual(new ServerTextSpan[]
        {
            new("标题", 0xFFAA00), new("\n副标题", 0xFFAA00), new(" RGB", 0x12ABCD), new("彩", 0x123ABC), new("普通", null)
        }), "MOTD 保留换行、颜色继承、十六进制和重置");
        Check(BackgroundContrast.ReadableText(0xAA00AA, 0xFFFFFFFF) == 0xFFAA00AA &&
            BackgroundContrast.ReadableText(0xFFFFFF, 0xFFFFFFFF) < 0xFF808080 &&
            BackgroundContrast.ReadableText(0x000000, 0xFF202020) >= 0xFF808080, "浅色和深色卡片上的低对比彩字可读");
        Console.WriteLine("服务器配置、精选保护、直连参数、SRV 回退和回环状态探测检查通过。");
    }

    /// <summary>使用可控的 DNS 任务验证及时返回的 SRV 目标、超时后的原地址回退和调用方取消，无需访问 DNS。</summary>
    private static async Task CheckSrvFallbackAsync()
    {
        var endpoint = ServerEndpoint.Parse("server.example");
        var target = ("proxy.example", 25566);
        Check(await ServerStatusProbe.ResolveDestinationAsync(endpoint, Task.FromResult(target), default) == target,
            "及时返回的 SRV 目标保留自定义端口");

        var pending = new TaskCompletionSource<(string, int)>();
        using var cancel = new CancellationTokenSource();
        var canceled = ServerStatusProbe.ResolveDestinationAsync(endpoint, pending.Task, cancel.Token);
        cancel.Cancel();
        try { await canceled; }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancel.Token) { }
        Check(canceled.IsCanceled, "调用方取消不会回退到原地址");

        Check(await ServerStatusProbe.ResolveDestinationAsync(endpoint, pending.Task, default)
            .WaitAsync(TimeSpan.FromSeconds(5)) == (endpoint.Host, endpoint.Port), "慢 SRV 在连接前回退到原地址");
    }

    /// <summary>用合成本地清单调用真实进程构造，确认旧版直连与新版 Quick Play 均使用卡片地址，不运行游戏。</summary>
    private static async Task CheckLaunchArgumentsAsync(string root, ServerEndpoint endpoint)
    {
        var runtime = JavaChecks.CreateJava(Path.Combine(root, "java"), "21.0.1");
        string versionPath = Path.Combine(root, "versions", "server-check");
        Directory.CreateDirectory(versionPath);
        string manifest = Path.Combine(versionPath, "server-check.json");
        const string standard = """
            {"id":"server-check","type":"release","mainClass":"test.Main","libraries":[],
             "javaVersion":{"component":"java-runtime-delta","majorVersion":21},
             "arguments":{"jvm":["-cp","${classpath}"],"game":[GAME_ARGUMENTS]}}
            """;
        const string quickPlay = """
            ,{"rules":[{"action":"allow","features":{"is_quick_play_multiplayer":true}}],
            "value":["--quickPlayMultiplayer","${quickPlayMultiplayer}"]}
            """;
        var advanced = new AdvancedLaunchSettings(GameArguments: "--width 900 --server other.example --port=11111 --quickPlaySingleplayer test-world");
        foreach (bool modern in new[] { false, true })
        {
            await File.WriteAllTextAsync(manifest, standard.Replace("GAME_ARGUMENTS", """ "--username","${auth_player_name}" """ + (modern ? quickPlay : "")));
            var version = InstalledVersionService.Scan(root).Single(item => item.Error is null);
            using var process = await GameLaunchService.BuildAsync(root, version, IsolationMode.All, new("ServerTest"),
                javaPath: runtime.Executable, advanced: advanced, server: endpoint);
            var args = LaunchArguments.Parse(process.StartInfo.Arguments);
            string[] connection = modern ? ["--quickPlayMultiplayer", "[::1]:25570"] : ["--server", "[::1]", "--port", "25570"];
            int index = Array.IndexOf(args, connection[0]);
            Check(index >= 0 && args.Skip(index).Take(connection.Length).SequenceEqual(connection) &&
                args.Contains("900") && !args.Contains("other.example") && !args.Contains("test-world"), modern ? "Quick Play 直连" : "旧版服务器直连");
        }
    }

    /// <summary>模拟 Java 服务器读取握手和状态请求，返回状态 JSON，然后原样回显 Ping 数据。</summary>
    private static async Task ReplyAsync(TcpListener listener, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        using var stream = client.GetStream();
        var handshake = await ReadAsync(stream, token);
        Check(handshake.AsSpan(0, 7).SequenceEqual(new byte[] { 0, 255, 255, 255, 255, 15, 9 }) &&
            Encoding.UTF8.GetString(handshake.AsSpan(7, 9)) == "127.0.0.1" && handshake[^1] == 1 &&
            handshake[^3] * 256 + handshake[^2] == ((IPEndPoint)listener.LocalEndpoint).Port, "握手类型、地址和端口");
        Check((await ReadAsync(stream, token)).SequenceEqual(new byte[] { 0 }), "状态请求");
        var json = Encoding.UTF8.GetBytes("""{"description":{"text":"§a测试","extra":[{"text":" 服务器"}]},"version":{"name":"1.21","protocol":767},"players":{"online":2,"max":20}}""");
        Check(json.Length is >= 128 and < 256, "测试状态采用双字节长度");
        await stream.WriteAsync(new byte[] { (byte)(128 | ((json.Length + 3) & 127)), 1, 0, (byte)(128 | (json.Length & 127)), 1 }
            .Concat(json).ToArray(), token);
        byte[] ping = await ReadAsync(stream, token);
        Check(ping.Length == 9 && ping[0] == 1, "延迟探测请求");
        await stream.WriteAsync(new byte[] { 9 }.Concat(ping).ToArray(), token);
    }

    /// <summary>读取测试客户端短请求，其长度前缀应为单字节。</summary>
    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] size = new byte[1];
        await stream.ReadExactlyAsync(size, token);
        Check(size[0] is > 0 and < 128, "测试请求长度");
        byte[] data = new byte[size[0]];
        await stream.ReadExactlyAsync(data, token);
        return data;
    }

    /// <summary>业务断言失败时抛出带场景说明的异常，令检查进程返回非零退出码。</summary>
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("失败：" + name);
    }
}
