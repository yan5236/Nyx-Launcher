using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>判断首次空账户时是否仅允许微软账户；分别探测国内与国际出口，任一来源明确位于境外即限制。</summary>
public sealed class AccountRegionPolicy
{
    /// <summary>单次探测的等待上限；超时按无法确认处理，不限制账户类型。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);
    internal static readonly string BilibiliUrl = "https://api.bilibili.com/x/web-interface/zone";
    internal static readonly string IpIpUrl = "https://myip.ipip.net/";
    internal static readonly string CountryUrl = "https://api.country.is/";
    private readonly HttpClient _http;
    private Task<bool>? _outside;

    /// <summary>创建探测服务；默认复用下载连接池，只记录目标主机而不记录响应正文。</summary>
    public AccountRegionPolicy(HttpClient? http = null) => _http = http ?? DownloadHttpClient.Create(Timeout);

    /// <summary>空账户、从未保存过账户且明确位于大陆以外时，首次使用只能添加微软账户。</summary>
    public static bool RequiresMicrosoftOnly(AccountStore accounts, bool outsideMainland) =>
        accounts.Accounts.Count == 0 && !accounts.HasAccountHistory && outsideMainland;

    /// <summary>本次运行只探测一次；并发调用复用同一任务，网络失败或无法确认时返回 false。</summary>
    public Task<bool> IsOutsideMainlandAsync() => _outside ??= DetectAsync();

    /// <summary>并行询问国内与国际来源；任一来源明确位于境外即限制，全部失败或确认大陆时不限制。</summary>
    private async Task<bool> DetectAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        bool?[] results = await Task.WhenAll(
            QueryAsync(BilibiliUrl, ParseBilibiliAsync, timeout.Token),
            QueryAsync(IpIpUrl, ParseIpIpAsync, timeout.Token),
            QueryAsync(CountryUrl, ParseCountryAsync, timeout.Token));
        return Array.IndexOf(results, true) >= 0;
    }

    /// <summary>读取一个来源；非成功状态、格式错误、超时及网络失败都返回无法确认。</summary>
    private async Task<bool?> QueryAsync(string url, Func<HttpResponseMessage, Task<bool?>> parse, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(ResourceMirror.UserAgent);
            using var response = await _http.SendAsync(request, token);
            return response.IsSuccessStatusCode ? await parse(response) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException or InvalidOperationException)
        { return null; }
    }

    /// <summary>B 站 zone 返回国家与省份；中国香港、澳门和台湾按大陆以外处理。</summary>
    private static async Task<bool?> ParseBilibiliAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0) return null;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("country", out var country) || country.ValueKind != JsonValueKind.String) return null;
        string name = country.GetString()!;
        if (name.Length == 0) return null;
        if (name != "中国") return true;
        string province = data.TryGetProperty("province", out var node) && node.ValueKind == JsonValueKind.String ? node.GetString()! : "";
        return IsSpecialRegion(province);
    }

    /// <summary>ipip.net 返回单行文本；解析“来自于”后的国家与省份，无法识别时视为未知。</summary>
    private static async Task<bool?> ParseIpIpAsync(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        int index = text.IndexOf("来自于：", StringComparison.Ordinal);
        if (index < 0) return null;
        string[] parts = text[(index + 4)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        if (parts[0] != "中国") return true;
        return IsSpecialRegion(parts.Length > 1 ? parts[1] : "");
    }

    /// <summary>country.is 返回 ISO 国家代码；CN 为大陆，其他非空代码为境外。</summary>
    private static async Task<bool?> ParseCountryAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!document.RootElement.TryGetProperty("country", out var country) || country.ValueKind != JsonValueKind.String) return null;
        string code = country.GetString()!;
        return code.Length == 0 ? null : code != "CN";
    }

    /// <summary>香港、澳门和台湾不等同于中国大陆，首次空账户同样只允许微软账户。</summary>
    private static bool IsSpecialRegion(string province) =>
        province.Contains("香港", StringComparison.Ordinal) || province.Contains("澳门", StringComparison.Ordinal) ||
        province.Contains("台湾", StringComparison.Ordinal);
}
