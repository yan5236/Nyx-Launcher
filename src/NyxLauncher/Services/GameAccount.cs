using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace NyxLauncher.Services;

/// <summary>保存离线、微软或外置游戏身份；认证缓存只允许存放当前 Windows 用户加密后的数据。</summary>
public sealed record GameAccount(string Name, string? ProfileId = null, string? MicrosoftAccountId = null,
    string? ClientId = null, string? ProtectedTokenCache = null,
    string? AuthServer = null, string? AuthServerName = null, string? ExternalUsername = null)
{
    [JsonIgnore] public bool IsExternal => AuthServer is not null;
    [JsonIgnore] public bool IsMicrosoft => ProfileId is not null && !IsExternal;
    [JsonIgnore] public string Uuid => ProfileId ?? GetUuid(Name);
    [JsonIgnore] public string Key => IsExternal ? "external:" + AuthServer + ":" + Uri.EscapeDataString(ExternalUsername!) + ":" + Uuid : IsMicrosoft ? "microsoft:" + Uuid : "offline:" + Name;
    [JsonIgnore] public string TypeLabel => IsExternal ? "外置账户 · " + AuthServerName : IsMicrosoft ? "微软账户" : "离线账户";
    [JsonIgnore] public string Avatar => "ms-appx:///Assets/" + (IsAlex ? "alex" : "steve") + "-avatar.png";
    [JsonIgnore] public bool IsAlex
    {
        get
        {
            byte[] bytes = Convert.FromHexString(Uuid);
            return ((bytes[3] ^ bytes[7] ^ bytes[11] ^ bytes[15]) & 1) != 0;
        }
    }

    /// <summary>校验完整身份结构；拒绝缺失或混合认证字段，防止损坏的在线身份被当作离线账户。</summary>
    public void Validate()
    {
        ValidateName(Name);
        if (IsExternal)
        {
            if (AuthServer != ExternalServerStore.Normalize(AuthServer!) || string.IsNullOrWhiteSpace(AuthServerName) ||
                string.IsNullOrWhiteSpace(ExternalUsername) || ProfileId is null || !Regex.IsMatch(ProfileId, @"\A[0-9a-f]{32}\z") ||
                MicrosoftAccountId is not null || ClientId is not null || string.IsNullOrWhiteSpace(ProtectedTokenCache))
                throw new ArgumentException("外置账户信息不完整，请重新登录。");
            try { _ = Convert.FromBase64String(ProtectedTokenCache); }
            catch (FormatException) { throw new ArgumentException("外置账户缓存格式无效。"); }
            return;
        }
        if (AuthServerName is not null || ExternalUsername is not null) throw new ArgumentException("账户包含无效外置认证字段。");
        if (!IsMicrosoft)
        {
            if (MicrosoftAccountId is not null || ClientId is not null || ProtectedTokenCache is not null)
                throw new ArgumentException("离线账户包含无效认证字段。");
            return;
        }
        if (!Regex.IsMatch(ProfileId!, @"\A[0-9a-f]{32}\z") || !Guid.TryParse(ClientId, out _) ||
            string.IsNullOrWhiteSpace(MicrosoftAccountId) || string.IsNullOrWhiteSpace(ProtectedTokenCache))
            throw new ArgumentException("微软账户信息不完整，请重新登录。");
        try { _ = Convert.FromBase64String(ProtectedTokenCache); }
        catch (FormatException) { throw new ArgumentException("微软账户缓存格式无效。"); }
    }

    /// <summary>诊断只返回显示名称和类型，避免记录自动输出认证缓存字段。</summary>
    public override string ToString() => Name + "（" + TypeLabel + "）";

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
