using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NyxLauncher.Services;

public enum ModpackExportFormat { Modrinth, Mcbbs, MultiMC }

/// <summary>一次导出的元数据、目标及资源策略；文件选择由界面在开始时冻结。</summary>
public sealed record ModpackExportOptions(string Name, string Author, string Version, string Destination,
    ModpackExportFormat Format, bool BundleResources, bool OnlyModrinth);

/// <summary>生成三种标准整合包；只读源目录，成功后原子发布，不覆盖已有文件。</summary>
public static class ModpackExportService
{
    /// <summary>导出所选文件并返回被平台筛选跳过的数量；网络失败、变化文件、不安全路径或取消均删除暂存文件，保留已有目标。</summary>
    public static async Task<int> ExportAsync(string root, InstalledVersion version, ModpackExportOptions options,
        IReadOnlyList<ModpackExportEntry> entries, ModrinthCatalogService catalog,
        IProgress<string>? progress, CancellationToken token)
    {
        if (version.Error is not null || string.IsNullOrWhiteSpace(version.GameVersion)) throw new IOException("版本信息无效。");
        if (new[] { options.Name, options.Author, options.Version }.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256 || x.Any(char.IsControl)))
            throw new IOException("请填写整合包名、作者和版本（最多 256 字，不能包含换行）。");
        if (!Enum.IsDefined(options.Format) || options.OnlyModrinth && (options.BundleResources || options.Format != ModpackExportFormat.Modrinth))
            throw new IOException("整合包格式与资源选项不兼容。");
        string destination = Path.GetFullPath(options.Destination);
        string extension = options.Format == ModpackExportFormat.Modrinth ? ".mrpack" : ".zip";
        if (!destination.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new IOException("导出文件扩展名应为 " + extension);
        GameInstallationService.EnsureNoLinks(root);
        GameInstallationService.EnsureNoLinks(destination);
        if (Path.Exists(destination)) throw new IOException("导出文件已存在，请选择其他文件名。");
        var selected = ModpackExportFiles.SelectedFiles(entries).ToArray();
        if (selected.Length > 99998 || selected.Select(x => x.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new IOException("所选文件过多或包含重复路径。");
        var dependencies = Dependencies(version, options.Format);
        var hashes = new Dictionary<string, string>();
        long total = 0;
        foreach (var entry in selected)
        {
            token.ThrowIfCancellationRequested();
            string path = SourcePath(root, entry.RelativePath);
            if (path.Equals(destination, StringComparison.OrdinalIgnoreCase)) throw new IOException("不能将导出包写入所选源文件。");
            if (entry.IsDirectory) continue;
            total += new FileInfo(path).Length;
            if (total > 20L * 1024 * 1024 * 1024) throw new IOException("所选文件超过 20 GiB。");
            if (options.Format == ModpackExportFormat.Modrinth && !options.BundleResources && IsResource(entry.RelativePath))
            {
                progress?.Report("正在识别：" + entry.RelativePath);
                using var input = File.OpenRead(path);
                hashes.Add(entry.RelativePath, Convert.ToHexStringLower(await SHA1.HashDataAsync(input, token)));
            }
        }
        var downloads = await catalog.FindExportFilesAsync(hashes.Values, token);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        int skipped = 0;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                var files = new List<object>();
                foreach (var entry in selected)
                {
                    token.ThrowIfCancellationRequested();
                    string path = SourcePath(root, entry.RelativePath);
                    progress?.Report("正在打包：" + entry.RelativePath);
                    string prefix = options.Format == ModpackExportFormat.MultiMC ? ".minecraft/" : "overrides/";
                    if (entry.IsDirectory) { zip.CreateEntry(prefix + entry.RelativePath + "/"); continue; }
                    using var input = File.OpenRead(path);
                    string? hash = hashes.GetValueOrDefault(entry.RelativePath);
                    if (hash is not null)
                    {
                        string actual = Convert.ToHexStringLower(await SHA1.HashDataAsync(input, token));
                        if (actual != hash) throw new IOException("文件已变化，请重新导出：" + entry.RelativePath);
                        input.Position = 0;
                        if (downloads.TryGetValue(hash, out var download))
                        {
                            string sha512 = Convert.ToHexStringLower(await SHA512.HashDataAsync(input, token));
                            if (input.Length != download.Size || !sha512.Equals(download.Sha512, StringComparison.OrdinalIgnoreCase))
                                throw new IOException("平台文件与本地文件校验不一致：" + entry.RelativePath);
                            files.Add(new { path = entry.RelativePath, hashes = new { sha1 = hash, sha512 },
                                downloads = new[] { download.Url }, fileSize = input.Length });
                            continue;
                        }
                        if (options.OnlyModrinth) { skipped++; continue; }
                    }
                    if (options.Format == ModpackExportFormat.Mcbbs)
                    {
                        string sha1 = Convert.ToHexStringLower(await SHA1.HashDataAsync(input, token));
                        input.Position = 0;
                        files.Add(new { type = "addon", path = entry.RelativePath, hash = sha1 });
                    }
                    using var target = zip.CreateEntry(prefix + entry.RelativePath, CompressionLevel.Optimal).Open();
                    await input.CopyToAsync(target, token);
                }
                WriteManifest(zip, options, dependencies, files);
            }
            token.ThrowIfCancellationRequested();
            GameInstallationService.EnsureNoLinks(destination);
            File.Move(temporary, destination);
            return skipped;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>资源目录中的文件统一按哈希识别，包含禁用模组和资源包；普通配置、存档等仍按选择内置。</summary>
    private static bool IsResource(string path) => path.Split('/')[0].ToLowerInvariant() is "mods" or "resourcepacks" or "shaderpacks";

    /// <summary>再次验证相对路径和每一级链接，防止陈旧选择或调用方把外部文件、运行库和认证状态导入压缩包。</summary>
    private static string SourcePath(string root, string relative)
    {
        ModpackManifest.SafePath(relative);
        if (!ModpackExportFiles.Allowed(relative)) throw new IOException("不能导出启动器或运行库文件：" + relative);
        if (relative.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) + ".json", StringComparison.OrdinalIgnoreCase))
            throw new IOException("不能导出当前版本清单。");
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("源文件不属于游戏目录。");
        GameInstallationService.EnsureNoLinks(path);
        return path;
    }

    /// <summary>将已识别的游戏及加载器版本映射到目标格式；未知加载器明确失败，避免导出不能还原的包。</summary>
    private static Dictionary<string, string> Dependencies(InstalledVersion version, ModpackExportFormat format)
    {
        var result = new Dictionary<string, string> { [format == ModpackExportFormat.MultiMC ? "net.minecraft" : format == ModpackExportFormat.Mcbbs ? "game" : "minecraft"] = version.GameVersion };
        if (version.Loader.Length == 0) return result;
        string[] loader = version.Loader.Split(':', 2, StringSplitOptions.TrimEntries);
        if (loader.Length != 2 || loader[1].Length == 0) throw new IOException("加载器版本信息无效。");
        string id = (loader[0], format) switch
        {
            ("Fabric", ModpackExportFormat.MultiMC) => "net.fabricmc.fabric-loader",
            ("Forge", ModpackExportFormat.MultiMC) => "net.minecraftforge",
            ("NeoForge", ModpackExportFormat.MultiMC) => "net.neoforged",
            ("Fabric", ModpackExportFormat.Modrinth) => "fabric-loader",
            ("Fabric", _) => "fabric", ("Forge", _) => "forge", ("NeoForge", _) => "neoforge",
            _ => throw new IOException("暂不支持此加载器的整合包导出。")
        };
        result.Add(id, loader[1]);
        return result;
    }

    /// <summary>写入最多 8 MiB 的 UTF-8 清单和 MultiMC 配置；作者与包版本在扩展字段保留，不导出本机启动参数。</summary>
    private static void WriteManifest(ZipArchive zip, ModpackExportOptions options, Dictionary<string, string> dependencies, List<object> files)
    {
        object manifest;
        string name;
        switch (options.Format)
        {
            case ModpackExportFormat.Modrinth:
                name = "modrinth.index.json";
                manifest = new { formatVersion = 1, game = "minecraft", versionId = options.Version, name = options.Name,
                    author = options.Author, files, dependencies };
                break;
            case ModpackExportFormat.Mcbbs:
                name = "mcbbs.packmeta";
                manifest = new { manifestType = "minecraftModpack", manifestVersion = 1, name = options.Name, author = options.Author,
                    version = options.Version, description = "", addons = dependencies.Select(x => new { id = x.Key, version = x.Value }),
                    libraries = Array.Empty<object>(), files };
                break;
            default:
                name = "mmc-pack.json";
                manifest = new { formatVersion = 1, components = dependencies.Select(x => new { uid = x.Key, version = x.Value, important = true }),
                    name = options.Name, author = options.Author, version = options.Version };
                using (var writer = new StreamWriter(zip.CreateEntry("instance.cfg").Open(), new UTF8Encoding(false)))
                    writer.Write("[General]\nInstanceType=OneSix\nname=" + options.Name.Replace("\\", "\\\\") + "\n");
                break;
        }
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        if (data.Length > 8 * 1024 * 1024) throw new IOException("导出清单超过 8 MiB，请减少所选资源。");
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(data);
    }
}
