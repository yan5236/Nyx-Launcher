using System.IO.Compression;
using System.Text.Json.Nodes;

namespace NyxLauncher.Services;

/// <summary>整合包中需要下载或校验的文件；地址为空表示文件必须由压缩包提供。</summary>
public sealed record ModpackFile(string Path, string? Sha1, long Size, IReadOnlyList<Uri> Urls);

/// <summary>三种整合包的统一安装计划；仅解析数据，不执行包内脚本或启动参数。</summary>
public sealed record ModpackManifest(string Name, string Game, LoaderVersion? Loader,
    IReadOnlyList<ModpackFile> Files, IReadOnlyDictionary<string, string> Entries)
{
    /// <summary>读取 ZIP 清单并验证所有路径、大小和加载器；畸形、重复或不支持的内容明确失败，不写入磁盘。</summary>
    public static ModpackManifest Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 100000 || zip.Entries.Sum(x => x.Length) > 20L * 1024 * 1024 * 1024)
            throw new IOException("整合包超过 100000 个文件或解压大小超过 20 GiB。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            SafePath(entry.FullName.TrimEnd('/'));
            if (!names.Add(entry.FullName) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new IOException("整合包包含重复路径或符号链接。");
        }
        var manifests = zip.Entries.Where(x => x.Name is "modrinth.index.json" or "mcbbs.packmeta" or "mmc-pack.json").ToArray();
        if (manifests.Length != 1) throw new IOException("请选择包含唯一 Modrinth、MCBBS 或 MultiMC 清单的整合包。");
        var manifest = manifests[0];
        string prefix = manifest.FullName[..^manifest.Name.Length];
        var json = ReadJson(manifest);
        var files = new List<ModpackFile>();
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var components = new Dictionary<string, string>();
        string name = json["name"]?.GetValue<string>() ?? System.IO.Path.GetFileNameWithoutExtension(path);
        if (manifest.Name == "modrinth.index.json")
        {
            if (json["formatVersion"]?.GetValue<int>() != 1 || json["game"]?.GetValue<string>() != "minecraft")
                throw new IOException("不支持的 Modrinth 整合包格式。");
            foreach (var pair in json["dependencies"]!.AsObject()) components.Add(pair.Key, pair.Value!.GetValue<string>());
            foreach (var node in json["files"]!.AsArray())
            {
                if (node!["env"]?["client"]?.GetValue<string>() == "unsupported") continue;
                string relative = node["path"]!.GetValue<string>();
                SafePath(relative);
                string hash = node["hashes"]!["sha1"]!.GetValue<string>();
                ValidateHash(hash);
                long size = node["fileSize"]!.GetValue<long>();
                var urls = node["downloads"]!.AsArray().Select(x => DownloadTransport.ValidateUrl(x!.GetValue<string>())).ToArray();
                if (size < 0 || urls.Length == 0) throw new IOException("整合包文件缺少有效下载信息。");
                files.Add(new(relative, hash, size, urls));
            }
            AddEntries(zip, prefix + "overrides/", entries);
            AddEntries(zip, prefix + "client-overrides/", entries);
        }
        else if (manifest.Name == "mcbbs.packmeta")
        {
            if (json["manifestVersion"]?.GetValue<int>() != 1) throw new IOException("不支持的 MCBBS 清单版本。");
            foreach (var addon in json["addons"]!.AsArray()) components.Add(addon!["id"]!.GetValue<string>(), addon["version"]!.GetValue<string>());
            if (json["libraries"] is JsonArray libraries && libraries.Count > 0) throw new IOException("暂不支持带自定义运行库的 MCBBS 整合包。");
            AddEntries(zip, prefix + "overrides/", entries);
            foreach (var node in json["files"]!.AsArray())
            {
                string type = node!["type"]!.GetValue<string>();
                if (type == "addon")
                {
                    string relative = node["path"]!.GetValue<string>();
                    SafePath(relative);
                    string hash = node["hash"]!.GetValue<string>();
                    ValidateHash(hash);
                    var urls = new List<Uri>();
                    if (!entries.ContainsKey(relative) && json["fileApi"]?.GetValue<string>() is { Length: > 0 } api)
                        urls.Add(DownloadTransport.ValidateUrl(api.TrimEnd('/') + "/overrides/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString))));
                    files.Add(new(relative, hash, 0, urls));
                }
                else if (type == "curse")
                {
                    string fileName = node["fileName"]?.GetValue<string>() ?? "";
                    GameInstallationService.ValidateName(fileName);
                    string url = node["url"]?.GetValue<string>() ?? "";
                    if (url.Length == 0) throw new IOException("此 MCBBS 包的 CurseForge 文件没有下载地址，请使用包含模组文件的导出包。");
                    files.Add(new("mods/" + fileName, null, 0, [DownloadTransport.ValidateUrl(url)]));
                }
                else throw new IOException("不支持的 MCBBS 文件类型：" + type);
            }
        }
        else
        {
            if (json["formatVersion"]?.GetValue<int>() != 1) throw new IOException("不支持的 MultiMC 清单版本。");
            foreach (var component in json["components"]!.AsArray())
            {
                string id = component!["uid"]!.GetValue<string>();
                if (id is "org.lwjgl" or "org.lwjgl3") continue;
                components.Add(id, component["version"]!.GetValue<string>());
            }
            if (zip.Entries.Any(x => x.FullName.StartsWith(prefix + "patches/", StringComparison.Ordinal)))
                throw new IOException("暂不支持包含自定义 patches 的 MultiMC 实例。");
            var cfg = zip.GetEntry(prefix + "instance.cfg");
            if (cfg is not null)
            {
                if (cfg.Length > 1024 * 1024) throw new IOException("MultiMC 配置过大。");
                using var reader = new StreamReader(cfg.Open());
                foreach (string line in reader.ReadToEnd().Split('\n'))
                    if (line.StartsWith("name=", StringComparison.Ordinal)) name = line[5..].TrimEnd('\r');
            }
            AddEntries(zip, prefix + "minecraft/", entries);
            AddEntries(zip, prefix + ".minecraft/", entries);
        }
        string? game = null;
        LoaderVersion? loader = null;
        foreach (var (id, version) in components)
        {
            GameInstallationService.ValidateName(version);
            if (id is "minecraft" or "game" or "net.minecraft") { game = version; continue; }
            var kind = id switch
            {
                "fabric-loader" or "fabric" or "net.fabricmc.fabric-loader" => LoaderKind.Fabric,
                "forge" or "net.minecraftforge" => LoaderKind.Forge,
                "neoforge" or "net.neoforged" => LoaderKind.NeoForge,
                _ => throw new IOException("暂不支持整合包组件：" + id)
            };
            if (loader is not null) throw new IOException("整合包声明了多个加载器。");
            loader = new(kind, version, version, "");
        }
        if (game is null) throw new IOException("整合包没有声明 Minecraft 版本。");
        if (loader?.Loader == LoaderKind.Forge)
            loader = loader with { Id = loader.Version.StartsWith(game + "-") ? loader.Version : game + "-" + loader.Version,
                Version = loader.Version.StartsWith(game + "-") ? loader.Version[(game.Length + 1)..] : loader.Version };
        if (files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new IOException("整合包清单包含重复文件。");
        return new(name, game, loader, files, entries);
    }

    /// <summary>限制清单到 8 MiB，以 UTF-8 解析 JSON；缺失字段由调用方作为导入错误反馈。</summary>
    private static JsonObject ReadJson(ZipArchiveEntry entry)
    {
        if (entry.Length > 8 * 1024 * 1024) throw new IOException("整合包清单过大。");
        using var stream = entry.Open();
        return JsonNode.Parse(stream)?.AsObject() ?? throw new IOException("整合包清单为空。");
    }

    /// <summary>把覆盖目录映射为实例内相对路径；客户端覆盖按规范覆盖普通 overrides，目录项忽略。</summary>
    private static void AddEntries(ZipArchive zip, string prefix, Dictionary<string, string> entries)
    {
        foreach (var entry in zip.Entries.Where(x => x.FullName.StartsWith(prefix, StringComparison.Ordinal) && !x.FullName.EndsWith('/')))
        {
            string relative = entry.FullName[prefix.Length..];
            SafePath(relative);
            entries[relative] = entry.FullName;
        }
    }

    /// <summary>拒绝绝对路径、回退、Windows 设备名和流语法，避免 ZIP 及远程清单越出实例目录。</summary>
    internal static void SafePath(string relative)
    {
        if (relative.Length == 0 || relative.Contains('\\') || relative.StartsWith('/')) throw new IOException("整合包包含不安全路径。");
        foreach (string part in relative.Split('/')) GameInstallationService.ValidateName(part);
    }

    /// <summary>验证清单要求的 SHA-1，拒绝空值和畸形校验码。</summary>
    private static void ValidateHash(string hash)
    {
        if (hash.Length != 40 || !hash.All(Uri.IsHexDigit)) throw new IOException("整合包文件 SHA-1 无效。");
    }
}
