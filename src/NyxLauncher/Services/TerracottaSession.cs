using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>安装并管理 Windows x64 陶瓦进程，通过仅回环 HTTP 接口控制联机状态。</summary>
internal sealed class TerracottaSession
{
    private const string Executable = "terracotta-0.4.2-windows-x86_64.exe";
    private const string Archive = "terracotta-0.4.2-windows-x86_64-pkg.tar.gz";
    private const string Digest = "07EBE139E3CA5F74576E58B1A96EFE59ABDFBE148D3F1A49BFDCA8B6F70745F0";
    private static readonly HttpClient DownloadClient = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly HttpClient LocalClient = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly string LastLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NyxLauncher", "terracotta-last.log");
    private Process? _process;
    private DirectoryInfo? _runtime;
    private Uri? _endpoint;

    /// <summary>报告当前模块进程是否仍在运行，供界面恢复异常退出后的开关状态。</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>下载固定版本并校验官方 SHA-256，在目标目录内暂存解包后整体移动，返回可执行文件路径。失败时清理暂存目录。</summary>
    public static async Task<string> InstallAsync(string folder, CancellationToken token)
    {
        string destination = Path.Combine(folder, "Terracotta-0.4.2");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("目标目录已存在，请选择已安装的 EXE 或使用其他目录。");

        string staging = Path.Combine(folder, ".nyx-terracotta-" + Guid.NewGuid().ToString("N"));
        string package = Path.Combine(staging, Archive);
        Directory.CreateDirectory(staging);
        try
        {
            await using (var output = File.Create(package))
            {
                using var response = await DownloadClient.GetAsync(
                    "https://github.com/burningtnt/Terracotta/releases/download/v0.4.2/" + Archive,
                    HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                downloadTimeout.CancelAfter(TimeSpan.FromMinutes(3));
                await response.Content.CopyToAsync(output, downloadTimeout.Token);
            }

            await using (var input = File.OpenRead(package))
            {
                if (Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != Digest)
                    throw new InvalidDataException("下载文件与陶瓦官方校验值不一致。");
                input.Position = 0;
                await using var gzip = new GZipStream(input, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: false, cancellationToken: token);
            }

            if (!File.Exists(Path.Combine(staging, Executable)))
                throw new InvalidDataException("安装包中没有找到陶瓦可执行文件。");
            File.Delete(package);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return Path.Combine(destination, Executable);
        }
        finally { DeleteTemporaryDirectory(staging); }
    }

    /// <summary>以独立临时目录启动指定的陶瓦 EXE，从握手文件读取回环端口并确认接口兼容。失败或取消时清理进程。</summary>
    public async Task StartAsync(string executable, CancellationToken token)
    {
        if (_process is not null) throw new InvalidOperationException("陶瓦模块已经启动。");
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到陶瓦 EXE，请重新选择。");
        var runtime = Directory.CreateTempSubdirectory("nyx-terracotta-");
        _runtime = runtime;
        string handshake = Path.Combine(runtime.FullName, "endpoint.json");
        try
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)! };
            // Windows 的正式入口会分离子进程，使用同版本的工作进程入口以保留进程所有权。
            start.ArgumentList.Add("--hmcl2");
            start.ArgumentList.Add(handshake);
            start.Environment["TEMP"] = runtime.FullName;
            start.Environment["TMP"] = runtime.FullName;
            _process = Process.Start(start) ?? throw new IOException("无法启动陶瓦模块。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            while (!File.Exists(handshake))
            {
                if (_process.HasExited) throw new IOException("陶瓦进程已退出，请选择兼容的陶瓦 Windows x64 EXE。");
                await Task.Delay(100, timeout.Token);
            }
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(handshake, timeout.Token));
            if (!document.RootElement.TryGetProperty("port", out var value) || !value.TryGetUInt16(out ushort port) || port == 0)
                throw new InvalidDataException("陶瓦返回了无效的本地端口。");
            _endpoint = new Uri($"http://127.0.0.1:{port}/");
            using var metadata = JsonDocument.Parse(await LocalClient.GetStringAsync(new Uri(_endpoint, "meta"), timeout.Token));
            if (!metadata.RootElement.TryGetProperty("easytier_version", out _))
                throw new InvalidDataException("所选程序未提供兼容的陶瓦联机接口。");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    /// <summary>以所选玩家名扫描并共享首个局域网世界，等待状态查询返回邀请码；无玩家名时使用陶瓦默认名。</summary>
    public Task HostAsync(string? player, CancellationToken token) =>
        SendAsync("state/scanning" + (string.IsNullOrEmpty(player) ? "" : "?player=" + Uri.EscapeDataString(player)), token);

    /// <summary>用邀请码和可选玩家名加入陶瓦房间，所有用户输入均进行 URL 转义；无效邀请码由陶瓦拒绝。</summary>
    public Task JoinAsync(string code, string? player, CancellationToken token) =>
        SendAsync("state/guesting?room=" + Uri.EscapeDataString(code)
            + (string.IsNullOrEmpty(player) ? "" : "&player=" + Uri.EscapeDataString(player)), token);

    /// <summary>结束房主或房客会话并释放组网连接，保留已启用的陶瓦进程。</summary>
    public Task LeaveAsync(CancellationToken token) => SendAsync("state/ide", token);

    /// <summary>向当前会话的本地端口发送指令，失败时将 HTTP 状态交给页面显示。</summary>
    private async Task SendAsync(string route, CancellationToken token)
    {
        var endpoint = GetEndpoint();
        using var response = await LocalClient.GetAsync(new Uri(endpoint, route), token);
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new InvalidDataException("陶瓦未接受邀请码，请检查格式并确认当前没有其他联机会话。");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>读取陶瓦当前角色、邀请码或游戏地址，以及陶瓦报告的房间成员。</summary>
    public async Task<(string State, string Value, string[] Players)> ReadStateAsync(CancellationToken token)
    {
        using var document = JsonDocument.Parse(await LocalClient.GetStringAsync(new Uri(GetEndpoint(), "state/"), token));
        return ParseState(document.RootElement);
    }

    /// <summary>解析房主及访客状态；兼容未返回有效成员数组或成员名称的模块，保留其原有连接信息。</summary>
    internal static (string State, string Value, string[] Players) ParseState(JsonElement root)
    {
        string state = root.GetProperty("state").GetString() ?? "";
        string value = state switch
        {
            "host-ok" => root.GetProperty("room").GetString() ?? "",
            "guest-ok" => root.GetProperty("url").GetString() ?? "",
            "exception" => root.GetProperty("type").ToString(),
            _ => ""
        };
        var players = new List<string>();
        if (root.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var profile in profiles.EnumerateArray())
            {
                if (profile.ValueKind != JsonValueKind.Object || !profile.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())) continue;
                string label = profile.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() == "HOST" ? "（房主）" : "";
                players.Add(name.GetString() + label);
            }
        }
        return (state, value, players.ToArray());
    }

    /// <summary>打开当前陶瓦日志供导出，陶瓦可继续追加；当前不可用时尝试最近一次保存的日志。</summary>
    public Stream? OpenLog()
    {
        string? path = FindCurrentLog();
        if (path is null && File.Exists(LastLog)) path = LastLog;
        return path is null ? null : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>在本会话独立的临时目录内查找陶瓦 v0.4.2 的 application.log，尚未生成时返回空。</summary>
    private string? FindCurrentLog()
    {
        if (_runtime is null) return null;
        string root = Path.Combine(_runtime.FullName, "terracotta");
        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Select(dir => Path.Combine(dir, "application.log")).FirstOrDefault(File.Exists)
            : null;
    }

    /// <summary>在清理运行目录前原子替换最近会话日志；读取失败时保留上次日志并继续关闭进程。</summary>
    private void PreserveLog()
    {
        string temporary = LastLog + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (FindCurrentLog() is not { } path) return;
            Directory.CreateDirectory(Path.GetDirectoryName(LastLog)!);
            File.Copy(path, temporary);
            File.Move(temporary, LastLog, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>返回当前进程的回环端点，未启动或进程退出时报告错误。</summary>
    private Uri GetEndpoint() => _endpoint is not null && IsRunning
        ? _endpoint : throw new IOException("陶瓦模块尚未运行或已退出。");

    /// <summary>关闭房间并等待陶瓦进程退出，保留本次日志后清理其专用临时目录。</summary>
    public async Task StopAsync()
    {
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited && _endpoint is not null)
                {
                    try { await LeaveAsync(CancellationToken.None); }
                    catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { }
                }
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            process.Dispose();
            _process = null;
            _endpoint = null;
        }
        if (_runtime is { } runtime)
        {
            PreserveLog();
            DeleteTemporaryDirectory(runtime.FullName);
        }
        _runtime = null;
    }

    /// <summary>尽力清理本服务创建的专用暂存目录，清理失败时保留原始业务错误。</summary>
    private static void DeleteTemporaryDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
