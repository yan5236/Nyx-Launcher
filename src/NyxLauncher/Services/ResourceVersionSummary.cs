namespace NyxLauncher.Services;

/// <summary>把资源支持版本缩成 Minecraft 系列摘要；完整补丁号和特殊版本仍由卡片悬停展示。</summary>
internal static class ResourceVersionSummary
{
    /// <summary>合并同系列补丁号与连续正式版系列；覆盖到最新正式版时显示加号，旧范围保留上界，断档单列，优先显示最近三组。</summary>
    public static string Format(IReadOnlyList<string> versions, IReadOnlyList<string>? releases)
    {
        var supported = versions.Select(Series).OfType<Version>().Distinct().Order().ToArray();
        if (supported.Length == 0) return versions.Count == 0 ? "未标注版本" : "非正式版";
        var known = (releases ?? []).Select(Series).OfType<Version>().Distinct().Order().ToArray();
        var order = known.Concat(supported).Distinct().Order().ToArray();
        var groups = new List<string>();
        int start = 0;
        for (int i = 1; i <= supported.Length; i++)
        {
            if (i < supported.Length && (known.Length > 0
                ? Array.IndexOf(order, supported[i]) == Array.IndexOf(order, supported[i - 1]) + 1
                : supported[i].Major == supported[i - 1].Major && supported[i].Minor == supported[i - 1].Minor + 1)) continue;
            var first = supported[start];
            var last = supported[i - 1];
            groups.Add(first == last ? first.ToString() : known.Length > 0 && last == known[^1]
                ? first + "+" : first + "–" + last);
            start = i;
        }
        return string.Join("、", groups.AsEnumerable().Reverse().Take(3)) + (groups.Count > 3 ? "…" : "");
    }

    /// <summary>只将标准数字正式版本归并为两段系列；忽略快照、预发布、远古及人工维护的愚人节 ID。</summary>
    private static Version? Series(string id) => !AprilFoolsVersions.Ids.Contains(id) &&
        Version.TryParse(id, out var version) && version.Revision < 0 ? new Version(version.Major, version.Minor) : null;
}
