using System.IO.Compression;
using System.Security.Cryptography;

namespace NyxLauncher.Services;

/// <summary>识别本地资源归档并安全复制原文件；不执行或解压归档内容。</summary>
public sealed record LocalResource(string Path, string Category, string[] Loaders)
{
    public string DirectoryName => Category switch { "mod" => "mods", "datapack" => "saves", "shader" => "shaderpacks", _ => "resourcepacks" };

    /// <summary>依据根目录标记识别模组、整合包、资源包、数据包及光影；未知或混合资源拒绝猜测，坏归档向界面报告。</summary>
    public static LocalResource Read(string path)
    {
        if (!new[] { ".zip", ".jar", ".mrpack" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()))
            throw new IOException("请拖入 ZIP、JAR 或 MRPACK 资源文件。");
        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(x => x.FullName).ToHashSet(StringComparer.Ordinal);
        if (zip.Entries.Any(x => x.Name is "modrinth.index.json" or "mcbbs.packmeta" or "mmc-pack.json"))
            return new(path, "modpack", []);
        var loaders = new List<string>();
        if (names.Contains("fabric.mod.json")) loaders.Add("Fabric");
        if (names.Contains("quilt.mod.json")) loaders.Add("Quilt");
        if (names.Contains("META-INF/mods.toml")) loaders.Add("Forge");
        if (names.Contains("META-INF/neoforge.mods.toml")) loaders.Add("NeoForge");
        if (names.Contains("mcmod.info")) loaders.Add("Forge");
        if (loaders.Count > 0) return new(path, "mod", loaders.ToArray());
        bool data = names.Any(x => x.StartsWith("data/", StringComparison.Ordinal));
        bool assets = names.Any(x => x.StartsWith("assets/", StringComparison.Ordinal));
        if (names.Contains("pack.mcmeta") && data != assets) return new(path, data ? "datapack" : "resourcepack", []);
        if (names.Any(x => x.StartsWith("shaders/", StringComparison.Ordinal))) return new(path, "shader", []);
        throw new IOException("无法确定此文件的资源类型；请使用包含根目录资源标记的资源包、数据包、模组或整合包。");
    }

    /// <summary>排除损坏版本；本地模组按声明的加载器筛选，游戏版本与依赖约束仍由用户核对。</summary>
    // shortcut: 本地元数据只用于加载器筛选，自动兼容判定需补齐各格式的版本范围与依赖解析。
    public bool Supports(InstalledVersion version) => version.Error is null &&
        (Category != "mod" || Loaders.Contains(version.Loader.Split(':')[0].Trim(), StringComparer.OrdinalIgnoreCase));

    /// <summary>重新验证版本与存档归属，返回隔离规则下的目标；失败不创建目录。</summary>
    public string Resolve(string folder, InstalledVersion version, IsolationMode isolation, string? world)
    {
        string root = ResourceInstallationService.ResolveRoot(folder, version, version.GameVersion, isolation);
        var current = InstalledVersionService.Scan(folder).Single(x => x.Name == version.Name);
        if (!Supports(current)) throw new IOException("目标加载器已变化，请重新选择。");
        if (Category != "datapack") return System.IO.Path.Combine(root, DirectoryName);
        if (world is null || !ResourceInstallationService.Worlds(root).Contains(world, StringComparer.OrdinalIgnoreCase))
            throw new IOException("请选择仍存在的存档。");
        return System.IO.Path.Combine(world, "datapacks");
    }

    /// <summary>异步复制到同目录临时文件后原子发布；相同文件复用，不覆盖不同内容，异常清理临时文件并保留源文件。</summary>
    public async Task CopyAsync(string directory)
    {
        string destination = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(Path));
        GameInstallationService.EnsureNoLinks(destination);
        using var source = File.OpenRead(Path);
        if (File.Exists(destination))
        {
            using var existing = File.OpenRead(destination);
            byte[] sourceHash = await SHA256.HashDataAsync(source);
            byte[] existingHash = await SHA256.HashDataAsync(existing);
            if (sourceHash.SequenceEqual(existingHash)) return;
            throw new IOException("目标已有同名但内容不同的文件，未覆盖：" + destination);
        }
        Directory.CreateDirectory(directory);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(output);
            GameInstallationService.EnsureNoLinks(destination);
            File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
