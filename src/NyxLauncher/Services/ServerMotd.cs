using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NyxLauncher.Services;

internal sealed record ServerTextSpan(string Text, uint? Color);

/// <summary>把服务器返回的 MOTD 转成供卡片绘制的纯文本颜色片段。</summary>
internal static partial class ServerMotd
{
    private const string LegacyCodes = "0123456789abcdef";
    private static readonly string[] Names =
    [
        "black", "dark_blue", "dark_green", "dark_aqua", "dark_red", "dark_purple", "gold", "gray",
        "dark_gray", "blue", "green", "aqua", "red", "light_purple", "yellow", "white"
    ];
    private static readonly uint[] Colors =
    [
        0x000000, 0x0000AA, 0x00AA00, 0x00AAAA, 0xAA0000, 0xAA00AA, 0xFFAA00, 0xAAAAAA,
        0x555555, 0x5555FF, 0x55FF55, 0x55FFFF, 0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF
    ];

    /// <summary>读取 MOTD 字符串、数组和嵌套文本组件，保留颜色、换行及显示顺序，最多保留 400 个 UTF-16 单元。</summary>
    internal static IReadOnlyList<ServerTextSpan> Parse(JsonElement description)
    {
        var spans = new List<ServerTextSpan>();
        int remaining = 400;
        AppendComponent(description, null, spans, ref remaining);
        return spans;
    }

    /// <summary>同时识别传统单字符格式码与插件使用的六组 RGB 颜色码，优先消费完整 RGB 序列。</summary>
    [GeneratedRegex("§x(?:§[0-9a-f]){6}|§[0-9a-fk-orx]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FormatCodes();

    /// <summary>按 JSON 文本树顺序读取 MOTD，子组件继承父颜色，忽略服务器附带的点击和悬停行为。</summary>
    private static void AppendComponent(JsonElement component, uint? color, List<ServerTextSpan> spans, ref int remaining)
    {
        if (remaining <= 0) return;
        switch (component.ValueKind)
        {
            case JsonValueKind.String:
                AppendText(component.GetString()!, color, spans, ref remaining);
                break;
            case JsonValueKind.Object:
                color = ComponentColor(component, color);
                if (component.TryGetProperty("text", out var text))
                    AppendComponent(text, color, spans, ref remaining);
                else if (component.TryGetProperty("fallback", out var fallback))
                    AppendComponent(fallback, color, spans, ref remaining);
                if (component.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
                    foreach (var child in extra.EnumerateArray()) AppendComponent(child, color, spans, ref remaining);
                break;
            case JsonValueKind.Array:
                bool first = true;
                foreach (var child in component.EnumerateArray())
                {
                    AppendComponent(child, color, spans, ref remaining);
                    if (first && child.ValueKind == JsonValueKind.Object) color = ComponentColor(child, color);
                    first = false;
                }
                break;
        }
    }

    /// <summary>识别 JSON color 字段的 Minecraft 颜色名、六位 RGB 或 reset；无效字段保留继承色。</summary>
    private static uint? ComponentColor(JsonElement component, uint? inherited)
    {
        if (!component.TryGetProperty("color", out var value) || value.ValueKind != JsonValueKind.String) return inherited;
        string color = value.GetString()!;
        if (color == "reset") return null;
        if (color.Length == 7 && color[0] == '#' &&
            uint.TryParse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb)) return rgb;
        int index = Array.IndexOf(Names, color);
        return index >= 0 ? Colors[index] : inherited;
    }

    /// <summary>切分服务器字符串中的传统颜色码，保留非颜色样式码前后的文本，reset 回退到卡片默认色。</summary>
    private static void AppendText(string text, uint? color, List<ServerTextSpan> spans, ref int remaining)
    {
        int offset = 0;
        foreach (var code in FormatCodes().EnumerateMatches(text))
        {
            AddSpan(text.AsSpan(offset, code.Index - offset), color, spans, ref remaining);
            if (remaining <= 0) return;
            char key = char.ToLowerInvariant(text[code.Index + 1]);
            int index = LegacyCodes.IndexOf(key);
            if (code.Length == 14)
            {
                uint rgb = 0;
                for (int i = code.Index + 3; i < code.Index + code.Length; i += 2)
                    rgb = (rgb << 4) | (uint)LegacyCodes.IndexOf(char.ToLowerInvariant(text[i]));
                color = rgb;
            }
            else if (index >= 0) color = Colors[index];
            else if (key == 'r') color = null;
            offset = code.Index + code.Length;
        }
        AddSpan(text.AsSpan(offset), color, spans, ref remaining);
    }

    /// <summary>保留换行，将其他显示控制字符转成空格，并避免长度限制截断代理对。</summary>
    private static void AddSpan(ReadOnlySpan<char> text, uint? color, List<ServerTextSpan> spans, ref int remaining)
    {
        int length = Math.Min(text.Length, remaining);
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
        if (length == 0) return;
        string safe = string.Concat(text[..length].ToString().Replace("\r\n", "\n").Select(c =>
            c is '\r' or '\n' ? '\n' : char.IsControl(c) ? ' ' : c));
        spans.Add(new(safe, color));
        remaining -= length;
    }
}
