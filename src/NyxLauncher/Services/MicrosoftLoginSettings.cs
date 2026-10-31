using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>微软公共客户端配置；构建时从本地 JSON 嵌入程序集，不包含客户端 secret 或用户凭据。</summary>
public sealed record MicrosoftLoginSettings(string ClientId, string RedirectUri)
{
    /// <summary>读取构建时嵌入的配置；资源缺失时报错，不回退旧客户端或读取用户目录中的历史配置。</summary>
    public static MicrosoftLoginSettings Load()
    {
        using var stream = typeof(MicrosoftLoginSettings).Assembly.GetManifestResourceStream("NyxLauncher.MicrosoftAuth.json")
            ?? throw new IOException("程序缺少微软登录配置，请填写 config/microsoft-auth.json 后重新构建。");
        return Read(stream);
    }

    /// <summary>从调用方持有的 UTF-8 JSON 流解析并校验配置；空内容、损坏 JSON 或无效字段报错，不关闭传入流。</summary>
    internal static MicrosoftLoginSettings Read(Stream stream)
    {
        var settings = JsonSerializer.Deserialize<MicrosoftLoginSettings>(stream) ?? throw new IOException("微软登录配置为空。");
        settings.Validate();
        return settings;
    }

    /// <summary>只接受公共客户端 GUID 和本机 HTTP 回调，不允许远端主机接收授权码或附带查询、凭据、片段。</summary>
    public void Validate()
    {
        if (!Guid.TryParse(ClientId, out var id) || id == Guid.Empty ||
            !Uri.TryCreate(RedirectUri, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.Host != "localhost" || uri.Port <= 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("微软登录配置需要有效客户端 ID 和 http://localhost 回调地址。");
    }
}
