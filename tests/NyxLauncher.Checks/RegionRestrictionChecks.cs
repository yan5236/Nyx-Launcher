using System.Net;
using NyxLauncher.Services;

internal static class RegionRestrictionChecks
{
    private static int _checks;

    /// <summary>验证首次空账户判定、删除后保留历史，以及国内、境外和失败来源的探测规则。</summary>
    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-region-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "accounts.json");
            var fresh = new AccountStore(path);
            Check(fresh.Accounts.Count == 0 && !fresh.HasAccountHistory, "首次启动无账户历史");
            Check(!AccountRegionPolicy.RequiresMicrosoftOnly(fresh, false), "大陆网络不限制账户类型");
            Check(AccountRegionPolicy.RequiresMicrosoftOnly(fresh, true), "首次空账户在境外仅允许微软账户");

            fresh.Add("Steve");
            Check(fresh.Accounts.Count == 1 && fresh.HasAccountHistory, "添加账户记录历史");
            Check(!AccountRegionPolicy.RequiresMicrosoftOnly(fresh, true), "有账户时不限制");

            fresh.Remove(fresh.Accounts[0]);
            Check(fresh.Accounts.Count == 0 && fresh.HasAccountHistory, "删除全部账户保留历史");
            Check(!AccountRegionPolicy.RequiresMicrosoftOnly(fresh, true), "删除后的空账户不限制");
            Check(new AccountStore(path).HasAccountHistory, "重启后仍识别账户历史");

            string emptyPath = Path.Combine(root, "empty.json");
            File.WriteAllText(emptyPath, """{"Accounts":[],"Selected":null}""");
            var empty = new AccountStore(emptyPath);
            Check(empty.LoadError is null && empty.Accounts.Count == 0 && empty.HasAccountHistory, "空配置文件视为已有账户历史");

            string brokenPath = Path.Combine(root, "broken.json");
            File.WriteAllText(brokenPath, "{broken");
            var broken = new AccountStore(brokenPath);
            Check(broken.LoadError is not null && broken.HasAccountHistory, "损坏配置仍保留账户历史");
        }
        finally { Directory.Delete(root, true); }

        await CheckDetectionAsync();
        Console.WriteLine($"通过 {_checks} 项账户区域限制检查。");
    }

    /// <summary>使用合成响应验证多来源探测：任一来源确认境外即限制，全部失败不限制，港澳台按境外处理。</summary>
    private static async Task CheckDetectionAsync()
    {
        Check(await Policy(new()
        {
            ["api.bilibili.com"] = Zone("中国", "广东"),
            ["myip.ipip.net"] = "当前 IP：1.2.3.4  来自于：中国 广东 江门  广东广电",
            ["api.country.is"] = """{"country":"JP"}"""
        }).IsOutsideMainlandAsync(), "境外出口确认即限制");

        Check(!await Policy(new()
        {
            ["api.bilibili.com"] = Zone("中国", "广东"),
            ["myip.ipip.net"] = "当前 IP：1.2.3.4  来自于：中国 广东 江门  广东广电",
            ["api.country.is"] = """{"country":"CN"}"""
        }).IsOutsideMainlandAsync(), "全部来源确认大陆时不限制");

        Check(await Policy(new()
        {
            ["api.bilibili.com"] = Zone("日本"),
            ["myip.ipip.net"] = "当前 IP：1.2.3.4  来自于：日本 东京  NTT",
            ["api.country.is"] = """{"country":"JP"}"""
        }).IsOutsideMainlandAsync(), "多来源确认境外");

        Check(await Policy(new()
        {
            ["api.bilibili.com"] = Zone("中国", "香港"),
            ["myip.ipip.net"] = "当前 IP：1.2.3.4  来自于：中国 香港  香港宽频",
            ["api.country.is"] = """{"country":"HK"}"""
        }).IsOutsideMainlandAsync(), "港澳台按大陆以外处理");

        Check(await Policy(new()
        {
            ["api.bilibili.com"] = "FAIL",
            ["myip.ipip.net"] = "FAIL",
            ["api.country.is"] = """{"country":"JP"}"""
        }).IsOutsideMainlandAsync(), "国内来源失败时采用境外结果");

        Check(!await Policy(new()
        {
            ["api.bilibili.com"] = "FAIL",
            ["myip.ipip.net"] = "FAIL",
            ["api.country.is"] = "FAIL"
        }).IsOutsideMainlandAsync(), "全部失败时不限制");

        Check(!await Policy(new()
        {
            ["api.bilibili.com"] = "FAIL",
            ["myip.ipip.net"] = "当前 IP：1.2.3.4  来自于：中国 北京  联通",
            ["api.country.is"] = "FAIL"
        }).IsOutsideMainlandAsync(), "仅国内来源可用时按大陆放行");

        Check(await Policy(new()
        {
            ["api.bilibili.com"] = "BADJSON",
            ["myip.ipip.net"] = "当前 IP：1.2.3.4  来自于：中国 北京  联通",
            ["api.country.is"] = """{"country":"JP"}"""
        }).IsOutsideMainlandAsync(), "格式错误不影响其他境外结果");

        Check(await Policy(new()
        {
            ["api.bilibili.com"] = Zone("美国"),
            ["myip.ipip.net"] = "FAIL",
            ["api.country.is"] = "FAIL"
        }).IsOutsideMainlandAsync(), "单一来源确认境外即可限制");
    }

    /// <summary>创建只响应本次场景的探测服务；未声明的主机返回网关错误，FAIL 模拟网络失败，BADJSON 模拟格式错误。</summary>
    private static AccountRegionPolicy Policy(Dictionary<string, string> responses) =>
        new(new HttpClient(new Handler((request, _) =>
        {
            if (!responses.TryGetValue(request.RequestUri!.Host, out string? body) || body == "FAIL")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            body = body == "BADJSON" ? "{" : body;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        })));

    /// <summary>构造 B 站 zone 响应，覆盖国家与省份字段。</summary>
    private static string Zone(string country, string province = "") =>
        "{\"code\":0,\"data\":{\"country\":\"" + country + "\",\"province\":\"" + province + "\"}}";

    /// <summary>断言业务结果；不满足时立即失败并标明行为。</summary>
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("失败：" + name);
        _checks++;
    }

    /// <summary>将请求交给测试场景，并保留真实取消令牌。</summary>
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
