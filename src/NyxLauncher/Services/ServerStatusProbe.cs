using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NyxLauncher.Services;

internal sealed record ServerStatus(IReadOnlyList<ServerTextSpan> Description, string Version, string Players, long? Latency, byte[]? Icon);

/// <summary>通过 Minecraft Java 状态握手查询服务器，无需认证，也不发送账户信息。</summary>
internal static class ServerStatusProbe
{
    private const ushort SrvType = 33;
    private const int MaxPacket = 128 * 1024;

    /// <summary>使用 SRV 目标或原地址连接服务器，读取状态 JSON 并测量 Ping/Pong 往返时间；连接和读取共用独立于 SRV 查询的超时，调用方取消及协议错误向上传递。</summary>
    internal static async Task<ServerStatus> QueryAsync(ServerEndpoint endpoint, CancellationToken token)
    {
        var destination = (endpoint.Host, endpoint.Port);
        if (OperatingSystem.IsWindows() && endpoint.Port == 25565 && !IPAddress.TryParse(endpoint.Host, out _))
            destination = await ResolveDestinationAsync(endpoint, Task.Run(() => ResolveSrv(endpoint), token), token).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var ct = timeout.Token;

        using var socket = new TcpClient();
        await socket.ConnectAsync(destination.Host, destination.Port, ct).ConfigureAwait(false);
        using var stream = socket.GetStream();
        using var handshake = new MemoryStream();
        byte[] host = Encoding.UTF8.GetBytes(endpoint.Host);
        WriteVarInt(handshake, 0);
        WriteVarInt(handshake, -1);
        WriteVarInt(handshake, host.Length);
        handshake.Write(host);
        handshake.WriteByte((byte)(endpoint.Port >> 8));
        handshake.WriteByte((byte)endpoint.Port);
        WriteVarInt(handshake, 1);
        await WritePacketAsync(stream, handshake.ToArray(), ct).ConfigureAwait(false);
        await WritePacketAsync(stream, [0], ct).ConfigureAwait(false);
        var status = ParseStatus(await ReadPacketAsync(stream, ct).ConfigureAwait(false));
        using var pingTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pingTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        byte[] ping = new byte[9];
        ping[0] = 1;
        BinaryPrimitives.WriteInt64BigEndian(ping.AsSpan(1), Stopwatch.GetTimestamp());
        long start = Stopwatch.GetTimestamp();
        try
        {
            await WritePacketAsync(stream, ping, pingTimeout.Token).ConfigureAwait(false);
            byte[] pong = await ReadPacketAsync(stream, pingTimeout.Token).ConfigureAwait(false);
            if (pong.AsSpan().SequenceEqual(ping)) status = status with { Latency = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds };
        }
        catch (Exception ex) when (!token.IsCancellationRequested && (ex is IOException or OperationCanceledException or SocketException))
        { /* 状态已确认，服务器没有返回可测量的 Pong。 */ }
        return status;
    }

    /// <summary>最多等待 SRV 查询两秒，返回查询提供的连接目标；等待超时则返回原域名和端口，调用方取消时终止整次探测。</summary>
    internal static async Task<(string Host, int Port)> ResolveDestinationAsync(ServerEndpoint endpoint,
        Task<(string Host, int Port)> lookup, CancellationToken token)
    {
        try
        {
            return await lookup.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            return (endpoint.Host, endpoint.Port);
        }
    }

    /// <summary>通过 Windows DNS 解析 Minecraft SRV，选择优先级最低且权重最高的目标；查询失败或无记录时返回原域名和端口。</summary>
    private static (string Host, int Port) ResolveSrv(ServerEndpoint endpoint)
    {
        int result = DnsQuery($"_minecraft._tcp.{endpoint.Host.TrimEnd('.')}.", SrvType, 0, IntPtr.Zero, out var records, IntPtr.Zero);
        try
        {
            string? host = null;
            ushort port = 0, priority = ushort.MaxValue, weight = 0;
            for (var cursor = result == 0 ? records : IntPtr.Zero; cursor != IntPtr.Zero;)
            {
                var record = Marshal.PtrToStructure<DnsHeader>(cursor);
                if (record.Type == SrvType)
                {
                    var srv = Marshal.PtrToStructure<DnsSrv>(IntPtr.Add(cursor, Marshal.SizeOf<DnsHeader>()));
                    var target = Marshal.PtrToStringUni(srv.Target);
                    if (!string.IsNullOrEmpty(target) && target != "." && srv.Port > 0 &&
                        (host is null || srv.Priority < priority || (srv.Priority == priority && srv.Weight > weight)))
                        (host, port, priority, weight) = (target, srv.Port, srv.Priority, srv.Weight);
                }
                cursor = record.Next;
            }
            return host is null ? (endpoint.Host, endpoint.Port) : (host, port);
        }
        finally { if (records != IntPtr.Zero) DnsFree(records, 1); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsHeader
    {
        public IntPtr Next, Name;
        public ushort Type, Length;
        public uint Flags, Ttl, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsSrv
    {
        public IntPtr Target;
        public ushort Priority, Weight, Port;
    }

    /// <summary>调用系统 DNS 解析并返回须释放的记录链表。</summary>
    [DllImport("dnsapi.dll", EntryPoint = "DnsQuery_W", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr records, IntPtr reserved);

    /// <summary>释放 DnsQuery 创建的完整记录链表。</summary>
    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsFree(IntPtr records, int freeType);

    /// <summary>校验状态包类型与长度，提取有限长度的显示数据，忽略非 64×64 PNG 图标。</summary>
    private static ServerStatus ParseStatus(byte[] packet)
    {
        using var data = new MemoryStream(packet);
        int id = ReadVarInt(data);
        int length = ReadVarInt(data);
        if (id != 0 || length <= 0 || length != data.Length - data.Position) throw new IOException("服务器状态包无效。");
        using var json = JsonDocument.Parse(packet.AsMemory((int)data.Position, length));
        var root = json.RootElement;
        var players = Field(root, "players");
        string current = Field(players, "online").ToString(), max = Field(players, "max").ToString();
        string count = int.TryParse(current, out int online) && int.TryParse(max, out int limit) && online >= 0 && limit >= 0
            ? $"{online}/{limit}" : "";
        var icon = Field(root, "favicon");
        return new(ServerMotd.Parse(Field(root, "description")), Clean(Flatten(Field(Field(root, "version"), "name")), 100),
            count, null, icon.ValueKind == JsonValueKind.String ? DecodeIcon(icon.GetString()!) : null);
    }

    /// <summary>读取可选的 JSON 成员，非对象及缺失成员返回未定义。</summary>
    private static JsonElement Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;

    /// <summary>提取 MOTD 字符串和文本组件中的内容，不执行交互事件。</summary>
    private static string Flatten(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Array => string.Concat(value.EnumerateArray().Select(Flatten)),
        JsonValueKind.Object => Flatten(Field(value, "text")) + Flatten(Field(value, "extra")),
        _ => ""
    };

    /// <summary>移除游戏颜色码及控制字符，并限制卡片显示文本长度。</summary>
    private static string Clean(string text, int limit)
    {
        text = Regex.Replace(text, "§[0-9a-fk-orx]", "", RegexOptions.IgnoreCase);
        text = string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c)).Trim();
        return text[..Math.Min(limit, text.Length)];
    }

    /// <summary>仅接受大小受限且 PNG 文件头声明为 64×64 的图标，其他内容使用默认图标。</summary>
    private static byte[]? DecodeIcon(string value)
    {
        const string prefix = "data:image/png;base64,";
        if (value.Length > 48 * 1024 || !value.StartsWith(prefix, StringComparison.Ordinal)) return null;
        byte[] png = new byte[value.Length];
        if (!Convert.TryFromBase64Chars(value.AsSpan(prefix.Length), png, out int size) || size < 33) return null;
        ReadOnlySpan<byte> header = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 64, 0, 0, 0, 64];
        return png.AsSpan().StartsWith(header) ? png[..size] : null;
    }

    /// <summary>添加 VarInt 长度前缀，将完整请求异步写入 TCP 流。</summary>
    private static async Task WritePacketAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await stream.WriteAsync(packet.ToArray(), token).ConfigureAwait(false);
    }

    /// <summary>在分配内存前限制网络包长度，再读取完整数据，取消及截断由底层流报告。</summary>
    private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken token)
    {
        byte[] one = new byte[1];
        for (int i = 0, length = 0; i < 5; i++)
        {
            await stream.ReadExactlyAsync(one, token).ConfigureAwait(false);
            length |= (one[0] & 127) << (7 * i);
            if (one[0] >= 128) continue;
            if (i == 4 && one[0] > 15 || length is < 1 or > MaxPacket) break;
            byte[] packet = new byte[length];
            await stream.ReadExactlyAsync(packet, token).ConfigureAwait(false);
            return packet;
        }
        throw new IOException("服务器响应长度无效。");
    }

    /// <summary>在已读取的网络包中解码 VarInt，提前结束或溢出时报错。</summary>
    private static int ReadVarInt(Stream stream)
    {
        for (int i = 0, value = 0; i < 5; i++)
        {
            int next = stream.ReadByte();
            if (next < 0 || i == 4 && next > 15) break;
            value |= (next & 127) << (7 * i);
            if (next < 128) return value;
        }
        throw new IOException("服务器响应数据无效。");
    }

    /// <summary>按 Minecraft VarInt 编码整数，负值也按无符号右移写出。</summary>
    private static void WriteVarInt(Stream stream, int value)
    {
        uint remaining = (uint)value;
        while (remaining >= 128)
        {
            stream.WriteByte((byte)(remaining | 128));
            remaining >>= 7;
        }
        stream.WriteByte((byte)remaining);
    }
}
