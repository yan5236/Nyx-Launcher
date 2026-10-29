using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensibility;
using CmlLib.Core.Auth;

namespace NyxLauncher.Services;

/// <summary>通过系统浏览器和 MSAL 完成 OAuth；独立账户缓存经 DPAPI 加密后与账户元数据一并原子保存。</summary>
public static class MicrosoftLoginService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string[] Scopes = ["XboxLive.signin"];

    /// <summary>新增或刷新微软账户并返回正版会话；交互时可由 browserAuthorization 接收并打开授权链接，空值通知链接失效；失败或取消不修改账户文件。</summary>
    public static async Task<(GameAccount Account, MSession Session)> AuthenticateAsync(GameAccount? previous,
        bool interactive, IProgress<string>? progress, CancellationToken token, IProgress<Uri?>? browserAuthorization = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        await Gate.WaitAsync(deadline.Token);
        byte[]? cache = null;
        try
        {
            var settings = MicrosoftLoginSettings.Load();
            if (!interactive && (previous is null || !previous.IsMicrosoft || previous.ClientId != settings.ClientId))
                throw new InvalidOperationException("登录配置已改变，请在账户页重新登录微软账户。");
            var app = PublicClientApplicationBuilder.Create(settings.ClientId).WithTenantId("consumers")
                .WithRedirectUri(settings.RedirectUri).Build();
            if (!interactive && previous is not null) cache = Unprotect(previous.ProtectedTokenCache!);
            bool loaded = false;
            app.UserTokenCache.SetBeforeAccess(args =>
            {
                if (!loaded && cache is not null) args.TokenCache.DeserializeMsalV3(cache);
                loaded = true;
            });
            app.UserTokenCache.SetAfterAccess(args =>
            {
                if (cache is not null) CryptographicOperations.ZeroMemory(cache);
                cache = args.TokenCache.SerializeMsalV3();
            });
            progress?.Report(interactive ? "请在系统浏览器中完成微软登录…" : "正在刷新微软登录…");
            AuthenticationResult result;
            if (interactive)
                result = await app.AcquireTokenInteractive(Scopes).WithCustomWebUi(new MicrosoftLoopbackBrowser(authorization: browserAuthorization))
                    .WithPrompt(Prompt.SelectAccount).ExecuteAsync(deadline.Token);
            else
            {
                var cached = await app.GetAccountAsync(previous!.MicrosoftAccountId);
                if (cached is null) throw new InvalidOperationException("登录缓存已失效，请在账户页重新登录。");
                result = await app.AcquireTokenSilent(Scopes, cached).ExecuteAsync(deadline.Token);
            }
            deadline.Token.ThrowIfCancellationRequested();
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var session = await MinecraftAuthentication.AuthenticateAsync(http, result.AccessToken, progress, deadline.Token);
            if (previous is not null && previous.Uuid != session.UUID)
                throw new InvalidOperationException("本次登录的 Minecraft 账户与原账户不同，请使用添加账户入口。");
            var account = new GameAccount(session.Username!, session.UUID, result.Account.HomeAccountId.Identifier,
                settings.ClientId, Protect(cache ?? throw new InvalidOperationException("微软登录未返回可保存的缓存。")));
            account.Validate();
            deadline.Token.ThrowIfCancellationRequested();
            return (account, session);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("微软登录超时，请重试。"); }
        catch (MsalUiRequiredException)
        { throw new InvalidOperationException("微软登录已过期或需要重新授权，请在账户页点击重新登录。"); }
        catch (MsalException ex)
        {
            // 不显示服务端原始正文；其中可能含授权码、账户信息或令牌。
            string code = Regex.IsMatch(ex.ErrorCode ?? "", @"\A[A-Za-z0-9_.-]{1,80}\z") ? ex.ErrorCode! : "unknown_error";
            string aad = Regex.Match(ex.Message, @"AADSTS\d{3,12}").Value;
            throw new InvalidOperationException(ex.ErrorCode is "authentication_canceled" or "access_denied"
                ? "微软登录已取消或未授权。"
                : $"微软授权失败（{code}{(aad.Length == 0 ? "" : " / " + aad)}）。请检查网络、客户端 ID、回调注册及本机回调端口；测试 ID 不保证可用。");
        }
        finally { if (cache is not null) CryptographicOperations.ZeroMemory(cache); Gate.Release(); }
    }

    /// <summary>使用 Windows 当前用户 DPAPI 加密 MSAL 缓存；只返回密文，不保存密码或明文令牌。</summary>
    internal static string Protect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("微软账户缓存需要 Windows 用户加密。");
        try { return Convert.ToBase64String(ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    /// <summary>只为当前 Windows 用户解密缓存；损坏或迁移到其他用户时明确要求重新登录。</summary>
    internal static byte[] Unprotect(string data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("微软账户缓存需要 Windows 用户加密。");
        try { return ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser); }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { throw new InvalidOperationException("无法解密微软登录缓存，请在账户页重新登录。"); }
    }
}
