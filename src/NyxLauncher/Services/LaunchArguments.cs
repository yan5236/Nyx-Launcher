using System.Text;
using CmlLib.Core.ProcessBuilder;

namespace NyxLauncher.Services;

/// <summary>按 Windows 双引号与反斜杠规则处理高级参数，避免通用 shell 解析器吞掉 Windows 路径。</summary>
internal static class LaunchArguments
{
    /// <summary>将独立参数编码为 Windows 命令行，双写引号前及结尾反斜杠，保留空值与中文路径。</summary>
    internal static string Join(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));

    /// <summary>始终使用双引号包围单个参数，并按 Windows 规则转义反斜杠与内部引号。</summary>
    private static string Quote(string argument)
    {
        var value = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            value.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            value.Append(c);
            slashes = 0;
        }
        return value.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>拆分用户参数，保留空参数、引号内空格与普通反斜杠；引号未闭合时拒绝保存或启动。</summary>
    internal static string[] Parse(string text)
    {
        var result = new List<string>();
        var value = new StringBuilder();
        bool quoted = false, started = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (started) { result.Add(value.ToString()); value.Clear(); started = false; }
                continue;
            }
            started = true;
            if (c == '\\')
            {
                int count = 1;
                while (i + 1 < text.Length && text[i + 1] == '\\') { count++; i++; }
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    value.Append('\\', count / 2);
                    if (count % 2 == 0) quoted = !quoted; else value.Append('"');
                    i++;
                }
                else value.Append('\\', count);
            }
            else if (c == '"') quoted = !quoted;
            else value.Append(c);
        }
        if (quoted) throw new ArgumentException("启动参数中的双引号未闭合。");
        if (started) result.Add(value.ToString());
        return result.ToArray();
    }

    /// <summary>将高级参数传给库，并在服务器启动时以选中的直连目标覆盖手写的目标参数；显式 GC 与手写 GC 冲突时报错。</summary>
    internal static void Apply(MLaunchOption option, AdvancedLaunchSettings settings, int javaMajor, ServerEndpoint? server = null)
    {
        AdvancedLaunchSettingsStore.Validate(settings);
        var custom = Parse(settings.JvmArguments);
        if (settings.Collector != GarbageCollector.Unspecified && custom.Any(x => x.StartsWith("-XX:+Use", StringComparison.Ordinal) && x.EndsWith("GC", StringComparison.Ordinal)))
            throw new ArgumentException("请只在 GC 选择器或 Java 虚拟机参数中指定垃圾回收器，避免冲突。");
        if (settings.Collector == GarbageCollector.Z && javaMajor < 15)
            throw new ArgumentException("ZGC 需要 Java 15 或更新版本，请更换 Java 或垃圾回收器。");
        string? gc = settings.Collector switch
        {
            GarbageCollector.G1 => "-XX:+UseG1GC", GarbageCollector.Z => "-XX:+UseZGC",
            GarbageCollector.Parallel => "-XX:+UseParallelGC", GarbageCollector.Serial => "-XX:+UseSerialGC", _ => null
        };
        var arguments = new List<string> { "-Dlog4j2.formatMsgNoLookups=true" };
        if (gc is not null) arguments.Add(gc);
        arguments.AddRange(custom);
        option.ExtraJvmArguments = new[] { new MArgument(arguments) };
        var game = Parse(settings.GameArguments);
        if (server is not null)
        {
            option.ServerIp = server.Host.Contains(':') ? $"[{server.Host}]" : server.Host;
            option.ServerPort = server.Port;
            game = WithoutConnectionOptions(game);
        }
        option.ExtraGameArguments = [new MArgument(game)];
    }

    /// <summary>删除直连目标相关选项及其值，保留原有参数顺序和其他选项。</summary>
    private static string[] WithoutConnectionOptions(string[] arguments)
    {
        var remaining = new List<string>();
        for (int i = 0; i < arguments.Length; i++)
        {
            string argument = arguments[i];
            int equals = argument.IndexOf('=');
            string key = equals < 0 ? argument : argument[..equals];
            if (key is "--server" or "--port" or "--quickPlayMultiplayer" or "--quickPlaySingleplayer" or "--quickPlayRealms")
            {
                if (equals < 0 && i + 1 < arguments.Length && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal)) i++;
                continue;
            }
            remaining.Add(argument);
        }
        return remaining.ToArray();
    }
}
