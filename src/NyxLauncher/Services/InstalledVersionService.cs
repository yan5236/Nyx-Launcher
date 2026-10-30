using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>本地版本的真实目录、游戏编号与加载器信息；解析失败的版本仍可在列表中定位。</summary>
public sealed record InstalledVersion(string Name, string DirectoryPath, string GameVersion, string Kind,
    string Loader, string? Error)
{
    public string Detail => Error is not null ? "版本信息读取失败" :
        string.Join(" · ", new[] { GameVersion, Kind, Loader }.Where(x => !string.IsNullOrEmpty(x)));
    public string LaunchDetail => Error is not null ? "版本信息读取失败" :
        string.Join(" · ", new[] { "Minecraft " + GameVersion, Loader }.Where(x => !string.IsNullOrEmpty(x)));
    public string AccessibleName => Name + "，" + Detail;

    /// <summary>让列表容器和辅助技术使用可读版本信息，避免朗读记录的内部字段。</summary>
    public override string ToString() => AccessibleName;
}

/// <summary>扫描 versions 下的本地清单，解析继承与加载器；不下载文件、不执行清单中的命令。</summary>
public static class InstalledVersionService
{
    /// <summary>读取目录直属版本；单个坏清单不阻断其他版本，目录访问失败向调用方报告，取消可中断扫描。</summary>
    public static IReadOnlyList<InstalledVersion> Scan(string folder, CancellationToken token = default)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("游戏目录不存在或不可访问：" + folder);
        string versions = Path.Combine(folder, "versions");
        if (!Directory.Exists(versions)) return Array.Empty<InstalledVersion>();
        var result = new List<InstalledVersion>();
        foreach (string directory in Directory.EnumerateDirectories(versions))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(directory);
            if (!File.Exists(Path.Combine(directory, name + ".json"))) continue;
            try
            {
                var data = ReadMetadata(versions, name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                result.Add(new(name, directory, data.Game, data.Kind, data.Loader, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { result.Add(new(name, directory, "", "", "", ex.Message)); }
        }
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>仅接受单级目录名；阻止清单继承路径或删除目标通过相对路径逃逸 versions。</summary>
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\') || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("无效的版本目录名。");
    }

    /// <summary>递归合并最多 16 层继承，子版本优先；优先读取 clientVersion，并从库坐标及现代 FML 启动参数识别加载器，限制清单为 8 MiB并报告循环或坏格式。</summary>
    private static (string Game, string Kind, string Loader) ReadMetadata(string versions, string name, HashSet<string> seen)
    {
        ValidateName(name);
        if (seen.Count >= 16 || !seen.Add(name)) throw new JsonException("版本继承循环或层级过深。");
        string path = Path.Combine(versions, name, name + ".json");
        using var stream = File.OpenRead(path);
        if (stream.Length > 8 * 1024 * 1024) throw new JsonException("版本清单超过 8 MiB。");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("版本清单不是对象。");
        string id = Text(root, "id");
        if (id.Length == 0) throw new JsonException("版本清单缺少 id。");
        string parent = Text(root, "inheritsFrom");
        var inherited = parent.Length > 0 ? ReadMetadata(versions, parent, seen) : (Game: id, Kind: "", Loader: "");
        string type = Text(root, "type");
        string kind = type switch { "release" => "正式版", "snapshot" => "快照版", "old_alpha" or "old_beta" => "远古版", _ => inherited.Kind };
        string loader = "";
        string clientVersion = Text(root, "clientVersion");
        string game = clientVersion.Length > 0 ? clientVersion : inherited.Game;
        if (root.TryGetProperty("libraries", out var libraries))
        {
            if (libraries.ValueKind != JsonValueKind.Array) throw new JsonException("libraries 必须为列表。");
            foreach (var library in libraries.EnumerateArray())
            {
                string[] coordinate = Text(library, "name").Split(':');
                if (coordinate.Length < 3) continue;
                string label = (coordinate[0], coordinate[1]) switch
                {
                    ("net.fabricmc", "fabric-loader") => "Fabric",
                    ("net.minecraftforge", "forge") => "Forge",
                    ("net.neoforged", "neoforge" or "forge") => "NeoForge",
                    _ => ""
                };
                if (label.Length == 0) continue;
                string build = coordinate[2];
                if (coordinate[1] == "forge" && build.Contains('-'))
                {
                    int split = build.IndexOf('-');
                    if (parent.Length == 0) game = build[..split];
                    build = build[(split + 1)..];
                }
                loader = label + ": " + build;
            }
        }
        if (root.TryGetProperty("patches", out var patches) && patches.ValueKind == JsonValueKind.Array)
            foreach (var patch in patches.EnumerateArray())
                if (Text(patch, "id") == "game" && Text(patch, "version") is { Length: > 0 } patchedGame) game = patchedGame;
        if (root.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("game", out var gameArguments) && gameArguments.ValueKind == JsonValueKind.Array)
        {
            var tokens = gameArguments.EnumerateArray().ToArray();
            for (int i = 0; i + 1 < tokens.Length; i++)
            {
                if (tokens[i].ValueKind != JsonValueKind.String || tokens[i + 1].ValueKind != JsonValueKind.String) continue;
                string value = tokens[i + 1].GetString()!;
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--")) continue;
                switch (tokens[i].GetString())
                {
                    case "--fml.neoForgeVersion": loader = "NeoForge: " + value; break;
                    case "--fml.forgeVersion" when !loader.StartsWith("NeoForge:"): loader = "Forge: " + value; break;
                    case "--fml.mcVersion": game = value; break;
                }
            }
        }
        if (AprilFoolsVersions.Ids.Contains(game)) kind = "愚人节版";
        return (game, kind, loader.Length > 0 ? loader : inherited.Loader);
    }

    /// <summary>读取可选文本字段；类型不正确时报告清单错误，避免把错误数据伪装为版本号。</summary>
    private static string Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException("清单条目必须为对象。");
        if (!element.TryGetProperty(name, out var value)) return "";
        if (value.ValueKind != JsonValueKind.String) throw new JsonException(name + " 必须为文本。");
        return value.GetString() ?? "";
    }

    /// <summary>删除或改名前验证直属目录、继承及 JAR 依赖和链接；保护依赖它的版本和外部文件，失败不修改任何内容。</summary>
    public static string GetDeletionTarget(string folder, InstalledVersion version)
    {
        ValidateName(version.Name);
        string root = Path.GetFullPath(folder);
        string versions = Path.Combine(root, "versions");
        string target = Path.Combine(versions, version.Name);
        if (!string.Equals(Path.GetFullPath(version.DirectoryPath), target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("版本路径与当前游戏目录不匹配。");
        foreach (string ancestor in new[] { root, versions, target })
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("为保护外部文件，不能修改链接目录中的版本。");
        foreach (string directory in Directory.EnumerateDirectories(versions))
        {
            string name = Path.GetFileName(directory);
            if (string.Equals(name, version.Name, StringComparison.OrdinalIgnoreCase)) continue;
            string manifest = Path.Combine(directory, name + ".json");
            if (!File.Exists(manifest)) continue;
            try
            {
                using var input = File.OpenRead(manifest);
                if (input.Length > 8 * 1024 * 1024) throw new IOException("清单过大，无法确认版本依赖：" + name);
                using var json = JsonDocument.Parse(input);
                if (new[] { "inheritsFrom", "jar" }.Any(field =>
                    string.Equals(Text(json.RootElement, field), version.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("版本“" + name + "”仍依赖此版本，请先处理依赖版本。");
            }
            catch (JsonException) { /* 已损坏的清单不能作为有效继承依赖。 */ }
        }
        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.TryPop(out var current))
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("版本含有文件链接，请手动管理该目录。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        return target;
    }
}
