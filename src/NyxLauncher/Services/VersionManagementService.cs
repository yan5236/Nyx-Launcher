using System.Text.Json;
using System.Text.Json.Nodes;

namespace NyxLauncher.Services;

/// <summary>负责版本改名、图标落盘及快捷目录定位；所有写操作限制在传入游戏目录的直属版本中。</summary>
public static class VersionManagementService
{
    public const string IconFileName = ".nyx-icon.png";

    /// <summary>校验版本确实属于当前根目录且仍存在，拒绝链接路径；返回真实版本目录，失败不创建任何文件。</summary>
    private static string GetDirectory(string root, InstalledVersion version)
    {
        GameInstallationService.ValidateName(version.Name);
        string directory = Path.Combine(Path.GetFullPath(root), "versions", version.Name);
        if (!string.Equals(directory, Path.GetFullPath(version.DirectoryPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("版本路径与当前游戏目录不匹配。");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("版本文件夹已不存在。");
        GameInstallationService.EnsureNoLinks(directory);
        return directory;
    }

    /// <summary>同步更改目录、同名 JSON/JAR 及清单 id/jar，保留游戏版本和其他内容；同名、依赖或链接被拒绝，普通 I/O 失败按逆序回滚，回滚失败报告保留目录。</summary>
    public static InstalledVersion Rename(string root, InstalledVersion version, string name)
    {
        GameInstallationService.ValidateName(name);
        string source = GetDirectory(root, version);
        if (name == version.Name) return version;
        // shortcut: 被其他版本继承或引用 JAR 的基础版本暂不改名，提供跨版本事务后再支持。
        InstalledVersionService.GetDeletionTarget(root, version);
        var current = InstalledVersionService.Scan(root).Single(x => x.Name == version.Name);
        if (current.Error is not null) throw new IOException(current.Error);
        string destination = Path.Combine(Path.GetDirectoryName(source)!, name);
        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) &&
            (Directory.Exists(destination) || File.Exists(destination))) throw new IOException("版本名称已存在，不能覆盖。");
        string manifest = version.Name + ".json";
        string renamedManifest = name + ".json";
        string jar = version.Name + ".jar";
        string renamedJar = name + ".jar";
        foreach (string file in new[] { renamedManifest, renamedJar })
            if (!string.Equals(file, manifest, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(file, jar, StringComparison.OrdinalIgnoreCase) && Path.Exists(Path.Combine(source, file)))
                throw new IOException("版本目录内存在同名文件，不能覆盖：" + file);
        var profile = JsonNode.Parse(File.ReadAllText(Path.Combine(source, manifest)))!.AsObject();
        profile["id"] = name;
        profile["clientVersion"] = current.GameVersion;
        if (string.Equals(profile["jar"]?.GetValue<string>(), version.Name, StringComparison.OrdinalIgnoreCase)) profile["jar"] = name;
        string staging = Path.Combine(Path.GetDirectoryName(source)!, ".nyx-rename-" + Guid.NewGuid().ToString("N"));
        string backup = ".nyx-original-" + Guid.NewGuid().ToString("N");
        var undo = new Stack<Action<string>>();
        Directory.Move(source, staging);
        string location = staging;
        try
        {
            File.Move(Path.Combine(staging, manifest), Path.Combine(staging, backup));
            undo.Push(path => File.Move(Path.Combine(path, backup), Path.Combine(path, manifest)));
            undo.Push(path => File.Delete(Path.Combine(path, renamedManifest)));
            using (var output = new FileStream(Path.Combine(staging, renamedManifest), FileMode.CreateNew))
                JsonSerializer.Serialize(output, profile, new JsonSerializerOptions { WriteIndented = true });
            if (File.Exists(Path.Combine(staging, jar)))
            {
                string temporaryJar = backup + ".jar";
                File.Move(Path.Combine(staging, jar), Path.Combine(staging, temporaryJar));
                undo.Push(path => File.Move(Path.Combine(path, temporaryJar), Path.Combine(path, jar)));
                File.Move(Path.Combine(staging, temporaryJar), Path.Combine(staging, renamedJar));
                undo.Push(path => File.Move(Path.Combine(path, renamedJar), Path.Combine(path, temporaryJar)));
            }
            Directory.Move(staging, destination);
            location = destination;
            File.Delete(Path.Combine(destination, backup));
            return current with { Name = name, DirectoryPath = destination };
        }
        catch (Exception failure)
        {
            try
            {
                if (location != staging) { Directory.Move(location, staging); location = staging; }
                while (undo.TryPop(out var restore)) restore(location);
                Directory.Move(location, source);
            }
            catch (Exception rollback)
            { throw new IOException($"改名失败：{failure.Message}；恢复失败：{rollback.Message}。文件保留在：{location}", failure); }
            throw;
        }
    }

    /// <summary>将已经解码并转换的 PNG 原子保存为版本图标；拒绝超限或非 PNG 输入、失效版本及链接，失败保留原图标。</summary>
    public static void SaveIcon(string root, InstalledVersion version, byte[] png)
    {
        if (png.Length is < 8 or > 16 * 1024 * 1024 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new ArgumentException("请选择有效的 PNG 图像，大小不能超过 16 MiB。");
        string target = Path.Combine(GetDirectory(root, version), IconFileName);
        GameInstallationService.EnsureNoLinks(target);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, png); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>定位版本目录或按隔离设置定位 mods/saves/screenshots；不存在的游戏子目录首次点击时创建，拒绝未知分类、链接与失效版本。</summary>
    public static string GetQuickFolder(string root, InstalledVersion version, string category, IsolationMode isolation)
    {
        string directory = GetDirectory(root, version);
        if (category == "version") return directory;
        if (category is not ("mods" or "saves" or "screenshots")) throw new ArgumentException("未知的快捷目录。");
        string target = Path.Combine(VersionIsolation.Resolve(root, version, isolation), category);
        GameInstallationService.EnsureNoLinks(target);
        Directory.CreateDirectory(target);
        return target;
    }
}
