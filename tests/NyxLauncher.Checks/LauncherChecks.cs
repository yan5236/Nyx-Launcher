using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using NyxLauncher.Services;

internal static class LauncherChecks
{
    /// <summary>验证协议、SemVer、正式发布清单与更新替换；仅使用临时文件和 HTTP 夹具，不连接公网或替换测试进程。</summary>
    public static async Task RunAsync()
    {
        foreach (var (left, right) in new[] { ("1.0.1", "1.0.0"), ("1.0.0", "1.0.0-rc.9"), ("1.0.0-beta.10", "1.0.0-beta.2"), ("1.0.0-beta.a", "1.0.0-beta.1"), ("1.0.0-beta.1", "1.0.0-beta") })
            Check(ReleaseVersion.Parse(left).CompareTo(ReleaseVersion.Parse(right)) > 0, "SemVer 排序：" + left);
        Check(ReleaseVersion.Parse("v1.2.3+build.4").CompareTo(ReleaseVersion.Parse("1.2.3+other")) == 0, "v 前缀与构建元数据不影响排序");
        foreach (string invalid in new[] { "1.2", "1.2.3.4", "01.2.3", "1.0.0-01", "1.0.0-", "65535.0.0", "../1.0.0" })
            Throws<FormatException>(() => ReleaseVersion.Parse(invalid), "拒绝坏版本：" + invalid);
        byte[] bytes = "synthetic-new-executable"u8.ToArray();
        string json = ReleaseJson(bytes);
        using (var http = new HttpClient(new Handler((request, token) =>
        {
            Check(request.RequestUri!.AbsoluteUri == LauncherUpdateService.LatestUrl && request.Headers.UserAgent.Count > 0, "只查询官方 latest 并带客户端标识");
            Check(request.Headers.Authorization is null, "更新不携带账户凭据");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        })))
            Check(await LauncherUpdateService.FetchAsync(default, http) is { Version: "9.0.0", Notes: "修复启动问题" }, "解析版本和更新内容");
        foreach (string invalid in new[] { json.Replace("\"draft\":false", "\"draft\":true"), json.Replace("\"prerelease\":false", "\"prerelease\":true"), json.Replace("sha256:", "sha1:"), json.Replace("github.com", "evil.example"), json.Replace(LauncherUpdateService.AssetName, "other.exe"), json.Replace("\"size\":" + bytes.Length, "\"size\":0") })
            Throws<JsonException>(() => Parse(invalid), "拒绝非官方或不完整更新清单");
        using (var missing = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))))
            Check(await LauncherUpdateService.FetchAsync(default, missing) is null, "没有正式发布不误报为最新");
        using (var limited = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))))
            await ThrowsAsync<HttpRequestException>(() => LauncherUpdateService.FetchAsync(default, limited), "限流明确失败");
        using (var cancelled = new CancellationTokenSource())
        using (var http = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); })))
        {
            cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => LauncherUpdateService.FetchAsync(cancelled.Token, http), "关闭弹窗可取消检查");
        }
        string folder = Path.Combine(Path.GetTempPath(), "nyx-launcher-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string receipt = Path.Combine(folder, "agreement.json");
            var store = new AgreementStore(receipt);
            Check(!store.IsAccepted && !File.Exists(receipt), "首次读取不自动同意或写盘");
            Check(store.Accept() is null && new AgreementStore(receipt).IsAccepted, "明确同意落盘并回读");
            File.WriteAllText(receipt, "{\"Revision\":\"older\",\"AcceptedAt\":\"2020-01-01T00:00:00Z\"}");
            Check(!new AgreementStore(receipt).IsAccepted, "旧协议需要重新确认");
            File.WriteAllText(receipt, "{broken");
            Check(!new AgreementStore(receipt).IsAccepted && File.ReadAllText(receipt) == "{broken", "损坏记录不自动覆盖或认作同意");
            var blocked = new AgreementStore(folder);
            Check(blocked.Accept() is not null && !blocked.IsAccepted, "保存失败不能标记已同意");
            await CheckInstallerAsync(folder, bytes);
        }
        finally { Directory.Delete(folder, true); }
        Check(!LauncherInfo.CanSelfUpdate, "目录测试构建不允许仅替换 EXE");
        Console.WriteLine("Launcher checks passed.");
    }

    /// <summary>用固定下载响应验证替换、哈希错误、取消、跨进程锁和回滚，断言旧文件与备份真实保留。</summary>
    private static async Task CheckInstallerAsync(string folder, byte[] bytes)
    {
        string exe = Path.Combine(folder, "NyxLauncher.exe");
        var release = Parse(ReleaseJson(bytes));
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Check(request.RequestUri == release.Download, "只下载已确认的官方产物");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }));
        using var transport = new DownloadTransport(new(), http);
        File.WriteAllText(exe, "old");
        var installer = new LauncherUpdateInstaller(release, exe);
        await installer.InstallAsync(transport, (_, _) => { }, default);
        Check(File.ReadAllBytes(exe).SequenceEqual(bytes) && File.ReadAllText(exe + ".previous") == "old", "替换当前路径并保留旧版");
        installer.Cleanup();
        Check(File.Exists(exe) && File.Exists(exe + ".previous"), "任务清理不删除程序或备份");
        LauncherUpdateInstaller.CleanupBackup(exe);
        Check(!File.Exists(exe + ".previous") && File.Exists(exe), "下次启动可清理未占用备份");
        File.WriteAllText(exe, "old");
        var bad = new LauncherUpdateInstaller(release with { Sha256 = new string('0', 64) }, exe);
        await ThrowsAsync<IOException>(() => bad.InstallAsync(transport, (_, _) => { }, default), "错误 SHA-256 不安装");
        Check(File.ReadAllText(exe) == "old" && !File.Exists(exe + ".update") && !File.Exists(exe + ".previous"), "校验失败保留旧版并丢弃坏文件");
        using (var cancelled = new CancellationTokenSource())
        {
            File.WriteAllBytes(exe + ".update", bytes);
            cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => new LauncherUpdateInstaller(release, exe).InstallAsync(transport, (_, _) => { }, cancelled.Token), "取消不提交替换");
            Check(File.ReadAllText(exe) == "old", "取消保留原 EXE");
        }
        using (var held = new FileStream(exe + ".update-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await ThrowsAsync<IOException>(() => new LauncherUpdateInstaller(release, exe).InstallAsync(transport, (_, _) => { }, default), "并发锁阻止第二个进程更新");
        Throws<FileNotFoundException>(() => LauncherUpdateInstaller.ReplaceExecutable(exe + ".missing", exe, exe + ".previous"), "提交失败明确抛错");
        Check(File.ReadAllText(exe) == "old" && !File.Exists(exe + ".previous"), "提交失败恢复原路径");
        File.WriteAllText(exe + ".previous", "preserve");
        Throws<IOException>(() => LauncherUpdateInstaller.ReplaceExecutable(exe + ".update", exe, exe + ".previous"), "已有备份不能覆盖");
        Check(File.ReadAllText(exe) == "old" && File.ReadAllText(exe + ".previous") == "preserve", "冲突保留原文件和备份");
    }

    /// <summary>按 GitHub 实际字段结构生成夹具，大小与 SHA-256 均来自真实测试字节。</summary>
    private static string ReleaseJson(byte[] bytes) => JsonSerializer.Serialize(new
    {
        tag_name = "v9.0.0", body = "修复启动问题", draft = false, prerelease = false,
        html_url = LauncherInfo.Repository + "/releases/tag/v9.0.0",
        assets = new[] { new { name = LauncherUpdateService.AssetName, size = bytes.Length,
            browser_download_url = LauncherInfo.Repository + "/releases/download/v9.0.0/" + LauncherUpdateService.AssetName,
            digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() } }
    });

    /// <summary>通过生产解析器处理夹具，保持测试和实际更新入口的边界校验一致。</summary>
    private static LauncherRelease Parse(string json)
    { using var document = JsonDocument.Parse(json); return LauncherUpdateService.Parse(document.RootElement); }

    /// <summary>断言具体业务结果，失败中止检查。</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    /// <summary>断言同步异常类型，避免校验或回滚静默成功。</summary>
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception(message); }

    /// <summary>断言异步异常类型，确保网络、取消和安装错误明确上报。</summary>
    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception(message); }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>传递真实请求与取消令牌给协议夹具，不访问公网。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
