using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace NyxLauncher.Services;

/// <summary>读取本机 Minecraft Java 的局域网通告，供房主查看陶瓦正在扫描的世界。</summary>
internal sealed class MinecraftLanRooms : IDisposable
{
    private readonly UdpClient _listener = new(AddressFamily.InterNetwork);
    private readonly HashSet<IPAddress> _localAddresses;
    private readonly Dictionary<ushort, (string Name, long Seen)> _rooms = [];

    /// <summary>订阅 Minecraft 的 IPv4 局域网组播，允许陶瓦同时监听同一端口。绑定失败时释放套接字。</summary>
    public MinecraftLanRooms()
    {
        try
        {
            _localAddresses = NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(entry => entry.Address).ToHashSet();
            _listener.ExclusiveAddressUse = false;
            _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, 4445));
            _listener.JoinMulticastGroup(IPAddress.Parse("224.0.2.60"));
        }
        catch
        {
            _listener.Dispose();
            throw;
        }
    }

    /// <summary>读取已经到达的通告，仅显示本机发送者，按端口去重并移除超过六秒没有广播的世界。</summary>
    public string[] Read()
    {
        IPEndPoint sender = new(IPAddress.Any, 0);
        long now = Stopwatch.GetTimestamp();
        for (int i = 0; i < 64 && _listener.Available > 0; i++)
        {
            byte[] packet = _listener.Receive(ref sender);
            if (!IPAddress.IsLoopback(sender.Address) && !_localAddresses.Contains(sender.Address)) continue;
            if (TryReadAnnouncement(Encoding.UTF8.GetString(packet), out string name, out ushort port))
                _rooms[port] = (name, now);
        }

        foreach (ushort port in _rooms.Where(pair => Stopwatch.GetElapsedTime(pair.Value.Seen, now).TotalSeconds > 6)
            .Select(pair => pair.Key).ToArray())
            _rooms.Remove(port);
        return _rooms.OrderBy(pair => pair.Key).Select(pair => $"{pair.Value.Name} · 端口 {pair.Key}").ToArray();
    }

    /// <summary>验证世界名称与端口字段，并排除陶瓦自己的大厅通告。返回是否为有效的 Minecraft 局域网世界。</summary>
    internal static bool TryReadAnnouncement(string text, out string name, out ushort port)
    {
        const string nameStart = "[MOTD]", nameEnd = "[/MOTD]", portStart = "[AD]", portEnd = "[/AD]";
        name = "";
        port = 0;
        int end = text.IndexOf(nameEnd + portStart, StringComparison.Ordinal);
        if (!text.StartsWith(nameStart, StringComparison.Ordinal) || end <= nameStart.Length ||
            !text.EndsWith(portEnd, StringComparison.Ordinal)) return false;
        name = text[nameStart.Length..end];
        return name != "§6§l双击进入陶瓦联机大厅（请保持陶瓦运行）" &&
            ushort.TryParse(text.AsSpan((end + nameEnd.Length + portStart.Length)..^portEnd.Length),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) && port != 0;
    }

    /// <summary>关闭局域网组播监听，释放共享 UDP 端口。</summary>
    public void Dispose() => _listener.Dispose();
}
