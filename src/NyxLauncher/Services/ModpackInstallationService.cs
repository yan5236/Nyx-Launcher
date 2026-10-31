using System.IO.Compression;

namespace NyxLauncher.Services;

/// <summary>在游戏安装发布前完成整合包资源，复用统一传输、暂存和取消机制。</summary>
public sealed class ModpackInstallationService(string folder, string taskId, DownloadSettings settings)
{
    private readonly GameInstallationService _game = new(folder, taskId, settings);
    private readonly string _staging = Path.Combine(Path.GetFullPath(folder), ".nyx-install", taskId);
    public const string IsolationMarker = ".nyx-modpack";

    /// <summary>下载或快照本地整合包，重新验证清单后安装精确游戏与加载器；全部资源成功才原子发布版本。</summary>
    public async Task InstallAsync(string name, string? localPath, ModrinthModFile? remote,
        DownloadTransport transport, Action<double, string> report, CancellationToken token)
    {
        GameInstallationService.EnsureNoLinks(_staging);
        Directory.CreateDirectory(_staging);
        string archive = Path.Combine(_staging, "pack.zip");
        if (remote is not null)
        {
            remote.Validate();
            await transport.DownloadAsync(remote.Url, archive, remote.Sha1, remote.Size, true, true,
                (done, total) => report(total > 0 ? done * 10d / total : 0, "下载整合包"), token);
        }
        else if (!File.Exists(archive))
        {
            string temporary = archive + ".copy";
            using (var source = File.OpenRead(localPath!))
            using (var target = File.Create(temporary)) await source.CopyToAsync(target, token);
            File.Move(temporary, archive, true);
        }
        var manifest = ModpackManifest.Read(archive);
        await _game.InstallAsync(name, manifest.Game, manifest.Loader, transport,
            (percent, detail) => report(10 + percent * .55, detail), token,
            beforePublish: async (publish, cancellation) =>
            {
                await InstallFilesAsync(archive, publish, name, manifest, transport, report, cancellation);
                await File.WriteAllTextAsync(Path.Combine(publish, IsolationMarker), "1", cancellation);
            });
    }

    /// <summary>下载声明文件并解压覆盖目录；拒绝覆盖启动清单、客户端和内部标记，缺少文件或校验失败不发布实例。</summary>
    internal static async Task InstallFilesAsync(string archive, string publish, string name, ModpackManifest manifest,
        DownloadTransport transport, Action<double, string> report, CancellationToken token)
    {
        foreach (string relative in manifest.Files.Select(x => x.Path).Concat(manifest.Entries.Keys))
        {
            string first = relative.Split('/')[0];
            if (first.StartsWith(".nyx", StringComparison.OrdinalIgnoreCase) ||
                first.Equals(name + ".json", StringComparison.OrdinalIgnoreCase) ||
                first.Equals(name + ".jar", StringComparison.OrdinalIgnoreCase))
                throw new IOException("整合包试图覆盖启动器管理文件：" + relative);
        }
        int completed = 0;
        await Parallel.ForEachAsync(manifest.Files.Where(x => x.Urls.Count > 0 && !manifest.Entries.ContainsKey(x.Path)),
            new ParallelOptions { MaxDegreeOfParallelism = transport.MaxParallelDownloads, CancellationToken = token }, async (file, cancellation) =>
        {
            string destination = Path.Combine(publish, file.Path);
            GameInstallationService.EnsureNoLinks(destination);
            Exception? failure = null;
            foreach (var url in file.Urls)
            {
                try
                {
                    await transport.DownloadAsync(url, destination, file.Sha1, file.Size, true, true, null, cancellation);
                    failure = null;
                    break;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException) { failure = ex; }
            }
            if (failure is not null) throw failure;
            report(65 + 25d * Interlocked.Increment(ref completed) / Math.Max(1, manifest.Files.Count), "下载整合包资源 · " + file.Path);
        });
        using var zip = ZipFile.OpenRead(archive);
        foreach (var (relative, entryName) in manifest.Entries)
        {
            token.ThrowIfCancellationRequested();
            string destination = Path.Combine(publish, relative);
            GameInstallationService.EnsureNoLinks(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var entry = zip.GetEntry(entryName)!;
            using var source = entry.Open();
            using var target = File.Create(destination);
            await CopyEntryAsync(source, target, entry.Length, token);
            report(92, "解压整合包 · " + relative);
        }
        foreach (var file in manifest.Files)
        {
            // Modrinth 覆盖目录允许替换下载文件；MCBBS 内嵌文件仍须符合其声明哈希。
            if (file.Urls.Count > 0 && manifest.Entries.ContainsKey(file.Path)) continue;
            if (!await DownloadTransport.ValidAsync(Path.Combine(publish, file.Path), file.Sha1, file.Size, token))
                throw new IOException("整合包文件缺失或校验失败：" + file.Path);
            if (file.Sha1 is null && file.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                using var jar = ZipFile.OpenRead(Path.Combine(publish, file.Path));
                if (jar.Entries.Count == 0) throw new IOException("整合包下载的模组 JAR 无效：" + file.Path);
            }
        }
        report(99, "完成整合包校验");
    }

    /// <summary>按清单声明大小限制实际解压字节，防止伪造 ZIP 长度绕过总量限制；取消或长度不符时失败。</summary>
    private static async Task CopyEntryAsync(Stream source, Stream target, long expected, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total > expected) throw new IOException("整合包解压长度超过声明值。");
            await target.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (total != expected) throw new IOException("整合包文件解压不完整。");
    }

    /// <summary>只清理本任务暂存区；保留已发布版本和共享资源。</summary>
    public void Cleanup() => _game.Cleanup();
}
