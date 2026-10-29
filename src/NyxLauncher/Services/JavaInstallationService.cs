using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>在启动器目录安装可移动 Java；暂存校验完成后原子发布，取消仅清理本任务目录。</summary>
public sealed class JavaInstallationService
{
    private readonly string _root;
    private readonly string _staging;
    private readonly JavaPackage _package;
    private readonly JavaRuntimeStore _store;
    private bool _published;
    public string Destination { get; }

    /// <summary>将包固定在启动器的 Java 子目录，任务暂存按独立 ID 隔离；已有目标不覆盖。</summary>
    public JavaInstallationService(string launcherDirectory, string taskId, JavaPackage package, JavaRuntimeStore store)
    {
        GameInstallationService.ValidateName(taskId);
        _root = Path.GetFullPath(Path.Combine(launcherDirectory, "Java"));
        _staging = Path.Combine(_root, ".staging", taskId);
        _package = package; _store = store;
        Destination = Path.Combine(_root, package.FolderName);
        GameInstallationService.EnsureNoLinks(Destination);
        if (Directory.Exists(Destination) || File.Exists(Destination)) throw new IOException("此 Java 版本已安装，请扫描或导入现有目录。");
    }

    /// <summary>用共享传输器下载与校验，解包后核实实际主版本及 x64 架构，再发布并登记；配置写入失败可重试登记而不重装。</summary>
    public async Task InstallAsync(DownloadTransport transport, Action<double, string> report, CancellationToken token)
    {
        if (!_published)
        {
            GameInstallationService.EnsureNoLinks(_staging);
            GameInstallationService.EnsureNoLinks(Destination);
            if (Directory.Exists(Destination) || File.Exists(Destination)) throw new IOException("Java 安装目录已存在，不能覆盖。");
            Directory.CreateDirectory(_staging);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            report(0, "获取 Java 下载清单");
            var package = await new JavaCatalogService(http).ResolveAsync(_package, token);
            string unpack = Path.Combine(_staging, "unpack");
            Directory.CreateDirectory(unpack);
            if (package.Channel == JavaChannel.Mojang)
                await DownloadMojangAsync(package, unpack, transport, report, token);
            else
            {
                string archive = Path.Combine(_staging, "package.zip");
                string hash = JavaCatalogService.RequireHash(package.Hash, 64);
                if (!await ValidArchiveAsync(archive, hash, token))
                {
                    if (File.Exists(archive)) File.Delete(archive);
                    // Zulu 元数据 size 按千字节取整；用 HTTP 实际长度和随后必需的 SHA-256 校验，不能把估算值当精确字节数。
                    await transport.DownloadAsync(new(JavaCatalogService.RequireHttps(package.Url)), archive, null, package.Channel == JavaChannel.Zulu ? 0 : package.Size, false, true,
                        (done, total) => report(total > 0 ? 80d * done / total : 0, $"下载 Java · {done / 1048576d:0.0} MiB"), token);
                    if (!await ValidArchiveAsync(archive, hash, token)) throw new IOException("Java 压缩包 SHA-256 校验失败。");
                }
                report(82, "校验完成，正在解压 Java");
                await ExtractAsync(archive, unpack, token);
            }
            token.ThrowIfCancellationRequested();
            var runtimes = JavaDiscovery.Scan([unpack], null, token).Runtimes;
            var runtime = runtimes.OrderBy(x => x.Home.Length).FirstOrDefault(x => x.Major == package.Major && x.Architecture == "x64")
                ?? throw new IOException("下载内容不包含所选主版本的 Windows x64 Java。");
            report(98, "验证并发布 Java");
            GameInstallationService.EnsureNoLinks(runtime.Home);
            GameInstallationService.EnsureNoLinks(Destination);
            token.ThrowIfCancellationRequested();
            Directory.Move(runtime.Home, Destination);
            _published = true;
        }
        var installed = JavaRuntime.Inspect(Destination) ?? throw new IOException("已安装的 Java 无法读取，请重新扫描。");
        _store.Add([installed]);
    }

    /// <summary>先校验 Mojang 清单，再并行下载每个原始文件并验证 SHA-1；不创建清单中的链接或允许路径越界。</summary>
    private async Task DownloadMojangAsync(JavaPackage package, string unpack, DownloadTransport transport,
        Action<double, string> report, CancellationToken token)
    {
        string manifest = Path.Combine(_staging, "manifest.json");
        await transport.DownloadAsync(new(JavaCatalogService.RequireHttps(package.Url)), manifest,
            JavaCatalogService.RequireHash(package.Hash, 40), package.Size, true, false, null, token);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, token));
        var files = doc.RootElement.GetProperty("files").EnumerateObject().ToArray();
        // 在产生任何清单文件前校验全部路径，失败不会留下越界内容。
        foreach (var file in files)
        {
            SafePath(unpack, file.Name);
            if (file.Value.GetProperty("type").GetString() is not ("file" or "directory"))
                throw new IOException("不支持含链接的 Windows Java 清单。");
        }
        int done = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = transport.MaxParallelDownloads }, async (file, ct) =>
        {
            string path = SafePath(unpack, file.Name);
            if (file.Value.GetProperty("type").GetString() == "directory") Directory.CreateDirectory(path);
            else
            {
                var raw = file.Value.GetProperty("downloads").GetProperty("raw");
                await transport.DownloadAsync(new(JavaCatalogService.RequireHttps(raw.GetProperty("url").GetString()!)), path,
                    JavaCatalogService.RequireHash(raw.GetProperty("sha1").GetString(), 40), raw.GetProperty("size").GetInt64(), true, false, null, ct);
            }
            int completed = Interlocked.Increment(ref done);
            report(5 + 90d * completed / Math.Max(files.Length, 1), $"下载 Java 文件 · {completed}/{files.Length}");
        });
    }

    /// <summary>验证归档完整 SHA-256；缺失、空文件或校验不符均返回假，取消由调用方处理。</summary>
    internal static async Task<bool> ValidArchiveAsync(string path, string hash, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        return stream.Length > 0 && Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>只解压普通目录和文件，拒绝链接、重复目标和超过 4 GiB 的展开内容；逐文件取消且不使用系统安装器。</summary>
    internal static async Task ExtractAsync(string archivePath, string destination, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        long size = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string path = SafePath(destination, entry.FullName.TrimEnd('/'));
            if (!seen.Add(path) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || (size += entry.Length) > 4L * 1024 * 1024 * 1024)
                throw new IOException("Java 压缩包包含重复路径、链接或过大内容。");
        }
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string path = SafePath(destination, entry.FullName.TrimEnd('/'));
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = File.Create(path);
            await input.CopyToAsync(output, token);
        }
    }

    /// <summary>把远程相对路径限制在指定根目录，逐段拒绝 Windows 保留名、备用数据流和目录穿越，并拒绝已有联接。</summary>
    internal static string SafePath(string root, string relative)
    {
        foreach (string part in relative.Replace('\\', '/').Split('/')) GameInstallationService.ValidateName(part);
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Java 包路径越界。");
        GameInstallationService.EnsureNoLinks(path);
        return path;
    }

    /// <summary>只清理当前任务的暂存区，已发布的 Java 永不回收；递归前验证绝对范围和所有链接。</summary>
    public void Cleanup()
    {
        string allowed = Path.Combine(_root, ".staging") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_staging).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Java 暂存目录越界。");
        if (!Directory.Exists(_staging)) return;
        GameInstallationService.EnsureNoLinks(_staging);
        foreach (string path in Directory.EnumerateFileSystemEntries(_staging, "*", SearchOption.AllDirectories)) GameInstallationService.EnsureNoLinks(path);
        Directory.Delete(_staging, true);
    }
}
