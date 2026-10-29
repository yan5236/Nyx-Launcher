using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace NyxLauncher.Services;

/// <summary>离线身份仅保存名称；UUID 和传统双默认皮肤均由名称稳定推导。</summary>
public sealed record OfflineAccount(string Name)
{
    [JsonIgnore] public string Uuid => GetUuid(Name);
    [JsonIgnore] public string TypeLabel => "离线账户";
    [JsonIgnore] public string Avatar => "ms-appx:///Assets/" + (IsAlex ? "alex" : "steve") + "-avatar.png";
    [JsonIgnore] public bool IsAlex
    {
        get
        {
            byte[] bytes = Convert.FromHexString(Uuid);
            return ((bytes[3] ^ bytes[7] ^ bytes[11] ^ bytes[15]) & 1) != 0;
        }
    }

    /// <summary>校验 Java 玩家名；不自动改变大小写或去掉空格，避免悄悄改变对应 UUID 和存档身份。</summary>
    public static void ValidateName(string name)
    {
        if (name is null || !Regex.IsMatch(name, @"\A[A-Za-z0-9_]{1,16}\z"))
            throw new ArgumentException("账户名称需为 1–16 个英文字母、数字或下划线。");
    }

    /// <summary>按 Java UUID.nameUUIDFromBytes 的 OfflinePlayer 规则生成 RFC 4122 v3 UUID，返回不带连字符的小写十六进制。</summary>
    public static string GetUuid(string name)
    {
        ValidateName(name);
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 15) | 48);
        hash[8] = (byte)((hash[8] & 63) | 128);
        return Convert.ToHexStringLower(hash);
    }
}
