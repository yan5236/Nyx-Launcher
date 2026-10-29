using System.Net;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensibility;
using System.Net.Sockets;
using NyxLauncher.Services;

internal static class MicrosoftCallbackChecks
{
    /// <summary>用真实 MSAL 回调监听验证指定路径、PKCE、拒绝授权和取消；不打开浏览器，不获取任何用户令牌。</summary>
    public static async Task RunAsync()
    {
        var settings = new MicrosoftLoginSettings(MicrosoftLoginSettings.TestClientId, MicrosoftLoginSettings.TestRedirectUri);
        settings.Validate();
        foreach (string scenario in new[] { "denied", "wrong-state", "cancel" })
        {
            using var metadata = new HttpClient(new MetadataHandler());
            var app = PublicClientApplicationBuilder.Create(settings.ClientId).WithTenantId("consumers")
                .WithRedirectUri(settings.RedirectUri).WithInstanceDiscovery(false).WithHttpClientFactory(new Factory(metadata)).Build();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            bool opened = false;
            Task? callback = null;
            var browserUi = new MicrosoftLoopbackBrowser(uri =>
                {
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    if (query["client_id"] != settings.ClientId || query["redirect_uri"] != settings.RedirectUri ||
                        query["code_challenge_method"] != "S256" || string.IsNullOrWhiteSpace(query["code_challenge"]) ||
                        string.IsNullOrWhiteSpace(query["state"]) || !query["scope"]!.Contains("XboxLive.signin"))
                        throw new Exception("MSAL 授权参数、测试回调或 PKCE 不符合预期。");
                    opened = true;
                    if (scenario == "cancel") { deadline.CancelAfter(300); return Task.CompletedTask; }
                    callback = SendCallbackAsync(settings.RedirectUri, query["state"]!, scenario == "wrong-state", deadline.Token);
                    return Task.CompletedTask;
                });
            try
            {
                await app.AcquireTokenInteractive(["XboxLive.signin"]).WithCustomWebUi(browserUi)
                    .WithPrompt(Prompt.SelectAccount).ExecuteAsync(deadline.Token);
                throw new Exception("拒绝授权或取消不应得到登录会话。");
            }
            catch (MsalException ex) when (opened && (scenario is "denied" or "wrong-state" && ex.ErrorCode is "access_denied" or "authentication_canceled" ||
                scenario == "cancel" && ex.ErrorCode == "authentication_canceled")) { }
            catch (OperationCanceledException) when (opened && scenario == "cancel") { }
            if (callback is not null) await callback;
            using var listener = new TcpListener(IPAddress.Loopback, 29116);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start(); listener.Stop();
            Console.WriteLine($"通过 MSAL 本机回调：{scenario}，PKCE 参数正确且端口已释放。");
        }
    }

    /// <summary>验证错误 state、路径、Host 和重复参数均不能抢走监听，再提交匹配 state 的拒绝授权响应。</summary>
    private static async Task SendCallbackAsync(string redirect, string state, bool wrongState, CancellationToken token)
    {
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
        await Task.Yield();
        if (wrongState)
        {
            string encoded = Uri.EscapeDataString(state);
            foreach (string url in new[] { redirect + "?code=fake-code&state=incorrect-state", redirect + "/?code=fake-code&state=" + encoded,
                redirect + "?code=one&code=two&state=" + encoded, redirect + "?code=one&error=denied&state=" + encoded,
                redirect + "?code=one&state=" + encoded + "&state=" + encoded })
            {
                using var rejected = await browser.GetAsync(url, token);
                if (rejected.StatusCode != HttpStatusCode.BadRequest) throw new Exception("回调必须拒绝无效身份、路径或重复参数。");
            }
            using var wrongHost = new HttpRequestMessage(HttpMethod.Get, redirect + "?code=one&state=" + encoded);
            wrongHost.Headers.Host = "untrusted.example";
            using var hostResponse = await browser.SendAsync(wrongHost, token);
            if (hostResponse.StatusCode != HttpStatusCode.BadRequest) throw new Exception("回调必须拒绝其他 Host。");
        }
        using var response = await browser.GetAsync(redirect + "?error=access_denied&state=" + Uri.EscapeDataString(state), token);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>为 MSAL 提供仅含公开端点的固定发现文档，确保回调验收不依赖外网或应用白名单。</summary>
    private sealed class MetadataHandler : HttpMessageHandler
    {
        /// <summary>只允许发现请求；若测试意外走到令牌交换则立即失败，避免模拟成功掩盖应用资格问题。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!request.RequestUri!.AbsolutePath.EndsWith("openid-configuration")) throw new Exception("不应发起真实令牌交换。");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"authorization_endpoint":"https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize",
                 "token_endpoint":"https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                 "issuer":"https://login.microsoftonline.com/consumers/v2.0",
                 "jwks_uri":"https://login.microsoftonline.com/consumers/discovery/v2.0/keys",
                 "end_session_endpoint":"https://login.microsoftonline.com/consumers/oauth2/v2.0/logout"}
                """) });
        }
    }

    private sealed class Factory(HttpClient client) : IMsalHttpClientFactory
    {
        /// <summary>使用测试发现文档客户端；本机回调请求仍通过真实 HTTP 连接发送。</summary>
        public HttpClient GetHttpClient() => client;
    }
}
