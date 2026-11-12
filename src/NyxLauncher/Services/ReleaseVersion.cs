using System.Text.RegularExpressions;

namespace NyxLauncher.Services;

/// <summary>发布版本的 SemVer 比较；支持 v 前缀、预发布标识和不参与排序的构建元数据。</summary>
internal sealed record ReleaseVersion(Version Number, string[] Preview) : IComparable<ReleaseVersion>
{
    /// <summary>解析三段非负版本号；拒绝前导零、空标识及超过程序集版本范围的数字，损坏发布清单抛出格式异常。</summary>
    internal static ReleaseVersion Parse(string text)
    {
        if (text.Length > 128) throw new FormatException("发布版本号过长。");
        var match = Regex.Match(text, @"\A[vV]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z");
        if (!match.Success || !Version.TryParse(string.Join('.', match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value), out var number)
            || number.Major > 65534 || number.Minor > 65534 || number.Build > 65534)
            throw new FormatException("发布版本号须为 v主版本.次版本.修订号，可附带预发布标识。");
        string[] preview = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (preview.Any(part => part.All(char.IsAsciiDigit) && part.Length > 1 && part[0] == '0'))
            throw new FormatException("预发布数字标识不能含前导零。");
        return new(number, preview);
    }

    /// <summary>按 SemVer 比较主版本及预发布标识；正式版高于同号预发布，数字标识按数值且低于文本标识。</summary>
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int result = Number.CompareTo(other.Number);
        if (result != 0) return result;
        if (Preview.Length == 0 || other.Preview.Length == 0)
            return Preview.Length == other.Preview.Length ? 0 : Preview.Length == 0 ? 1 : -1;
        for (int i = 0; i < Math.Min(Preview.Length, other.Preview.Length); i++)
        {
            string left = Preview[i], right = other.Preview[i];
            bool leftNumber = left.All(char.IsAsciiDigit), rightNumber = right.All(char.IsAsciiDigit);
            result = leftNumber && rightNumber ? left.Length.CompareTo(right.Length) : leftNumber != rightNumber ? leftNumber ? -1 : 1 : 0;
            if (result == 0) result = string.CompareOrdinal(left, right);
            if (result != 0) return result;
        }
        return Preview.Length.CompareTo(other.Preview.Length);
    }
}
