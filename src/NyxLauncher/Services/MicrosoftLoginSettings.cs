using System.Text.Json;
using System.Text.Json.Serialization;

namespace NyxLauncher.Services;

/// <summary>微软公共客户端配置；测试应用只供 Debug 验证，正式应用通过本机配置文件提供。</summary>
public sealed record MicrosoftLoginSettings(string ClientId, string RedirectUri)
{
    public const string TestClientId = "288ec5dd-6736-4d4b-9b96-30e083a8cad2";
    public const string TestRedirectUri = "http://localhost:29116/authentication-response";
    [JsonIgnore] public bool IsTest => string.Equals(ClientId, TestClientId, StringComparison.OrdinalIgnoreCase);
    public static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NyxLauncher", "microsoft-auth.json");

    /// <summary>加载本机显式配置；缺省 Debug 使用用户指定测试应用，损坏配置报错而不偷偷回退。</summary>
    public static MicrosoftLoginSettings Load()
    {
        var settings = File.Exists(ConfigPath)
            ? JsonSerializer.Deserialize<MicrosoftLoginSettings>(File.ReadAllText(ConfigPath)) ?? throw new IOException("微软登录配置为空。")
            : new(TestClientId, TestRedirectUri);
        settings.Validate();
#if !DEBUG
        if (settings.IsTest) throw new InvalidOperationException("测试客户端仅限 Debug 使用，请在 microsoft-auth.json 填写获批的正式客户端 ID。");
#endif
        return settings;
    }

    /// <summary>只接受公共客户端 GUID 和本机 HTTP 回调，不允许远端主机接收授权码或附带查询、凭据、片段。</summary>
    public void Validate()
    {
        if (!Guid.TryParse(ClientId, out var id) || id == Guid.Empty ||
            !Uri.TryCreate(RedirectUri, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.Host != "localhost" || uri.Port <= 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("微软登录配置需要有效客户端 ID 和 http://localhost 回调地址。");
        if (IsTest && RedirectUri != TestRedirectUri)
            throw new ArgumentException("测试客户端回调必须为 " + TestRedirectUri);
    }
}
