using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NyxLauncher.Services;

/// <summary>加载器列表的来源格式适配与排序；远程数据不用于执行代码或构造本地路径。</summary>
public static class LoaderVersionParser
{
    /// <summary>校验 BMCLAPI Forge 清单的游戏 ID、版本和发布日期；保留历史 branch 坐标，坏条目抛出 JSON 异常。</summary>
    public static IReadOnlyList<LoaderVersion> ParseForgeMirror(string gameVersion, byte[] data)
    {
        using var document = JsonDocument.Parse(data);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Forge 响应缺少版本列表。");
        var versions = new List<LoaderVersion>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("mcversion", out var game) || game.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(version.GetString()) ||
                !item.TryGetProperty("modified", out var modified) || modified.ValueKind != JsonValueKind.String ||
                !modified.TryGetDateTimeOffset(out var date))
                throw new JsonException("Forge 条目缺少版本、游戏 ID 或发布日期。");
            if (game.GetString() != gameVersion) throw new JsonException("Forge 清单返回了其他游戏版本。");
            string branch = "";
            if (item.TryGetProperty("branch", out var value) && value.ValueKind != JsonValueKind.Null)
            {
                if (value.ValueKind != JsonValueKind.String) throw new JsonException("Forge 分支格式无效。");
                if (!string.IsNullOrWhiteSpace(value.GetString())) branch = "-" + value.GetString();
            }
            string name = version.GetString()!;
            versions.Add(new(LoaderKind.Forge, gameVersion + "-" + name + branch, name + branch,
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        return Sort(versions);
    }

    /// <summary>从 Forge 官方版本行补齐日期；仅按已校验的 Maven 版本匹配，页面结构变化或日期缺失会触发源回退。</summary>
    public static IReadOnlyList<LoaderVersion> AddForgeDates(IReadOnlyList<LoaderVersion> versions, byte[] html)
    {
        // shortcut: 当前官方 Maven 元数据不含日期；若下载页结构改变，更新匹配规则，期间由 BMCLAPI 回退。
        const string pattern = """<td\s+class="download-version"\s*>\s*(?<version>[^\s<]+)[\s\S]*?</td>\s*<td\s+class="download-time"[^>]*>\s*(?<date>\d{4}-\d{2}-\d{2})\s*</td>""";
        var dates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(Encoding.UTF8.GetString(html), pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            string date = match.Groups["date"].Value;
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new JsonException("Forge 发布日期无效。");
            dates[match.Groups["version"].Value] = date;
        }
        return versions.Select(version => version with
        {
            Detail = dates.TryGetValue(version.Version, out var date) ? date :
                throw new JsonException($"Forge {version.Version} 缺少发布日期。")
        }).ToArray();
    }

    /// <summary>按版本号数字段自然降序排列并以完整坐标去重，避免 0.9 被排在 0.20 前；不强行把稳定版置于新测试版之前。</summary>
    public static IReadOnlyList<LoaderVersion> Sort(IEnumerable<LoaderVersion> versions) => versions
        .DistinctBy(x => x.Id, StringComparer.Ordinal)
        .OrderByDescending(x => x.Version, CultureInfo.InvariantCulture.CompareInfo.GetStringComparer(
            CompareOptions.IgnoreCase | CompareOptions.NumericOrdering)).ToArray();
}
