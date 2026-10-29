using System.Diagnostics;
using System.Security.Cryptography;

namespace NyxLauncher.Services;

/// <summary>从官方发布源取得固定且校验 SHA-256 的注入器，保持现有加载器 JVM 参数。</summary>
public static class AuthlibInjector
{
    private const string Hash = "9c7f4343e6c82034958ffb48c14a2cb0c85928be7283103ce17da00c6d5a7b10";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>校验缓存或下载官方 1.2.8，再将 javaagent 和预取元数据放在 Java 主类之前；失败终止启动。</summary>
    public static async Task ConfigureAsync(Process process, string folder, string server, CancellationToken token)
    {
        string path = Path.Combine(Path.GetFullPath(folder), ".nyx-launch", "authlib-injector-1.2.8.jar");
        GameInstallationService.EnsureNoLinks(path);
        await Gate.WaitAsync(token);
        try
        {
            if (!File.Exists(path) || Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, token))) != Hash)
            {
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using var transport = new DownloadTransport(new DownloadSettingsStore().Current);
                    await transport.DownloadAsync(new Uri("https://authlib-injector.yushi.moe/artifact/56/authlib-injector-1.2.8.jar"), temp, null, 0, false, false, null, token);
                    if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(temp, token))) != Hash)
                        throw new IOException("authlib-injector 校验失败。");
                    File.Move(temp, path, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        finally { Gate.Release(); }
        using var login = new ExternalLoginService();
        var metadata = await login.DiscoverAsync(server, token);
        if (metadata.Server.Url != server) throw new InvalidOperationException("认证地址已改变，请重新添加服务器并登录。");
        PrependArguments(process, path, server, metadata.Metadata);
    }

    /// <summary>使用独立参数或 Windows 引号保护带空格路径；不覆盖加载器已有参数。</summary>
    internal static void PrependArguments(Process process, string path, string server, byte[] metadata)
    {
        string agent = "-javaagent:" + path + "=" + ExternalServerStore.Normalize(server);
        string prefetched = "-Dauthlibinjector.yggdrasil.prefetched=" + Convert.ToBase64String(metadata);
        if (process.StartInfo.ArgumentList.Count > 0)
        { process.StartInfo.ArgumentList.Insert(0, prefetched); process.StartInfo.ArgumentList.Insert(0, agent); }
        else process.StartInfo.Arguments = "\"" + agent + "\" " + prefetched + " " + process.StartInfo.Arguments;
    }
}
