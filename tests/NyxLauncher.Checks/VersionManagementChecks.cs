using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.VersionLoader;
using NyxLauncher.Services;

internal static class VersionManagementChecks
{
    private static int _checks;

    /// <summary>在独立临时树验证改名、大小写、客户端路径、内容保留、失败保护、图标持久化及五种隔离目录，最后仅清理本测试树。</summary>
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-details-check-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var version = WriteVersion(root, "生存", """{"id":"生存","jar":"生存","type":"release","clientVersion":"1.21.1","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.14"}],"mainClass":"net.minecraft.client.main.Main","unknown":{"keep":true}}""");
            File.WriteAllText(Path.Combine(version.DirectoryPath, "生存.jar"), "client");
            Directory.CreateDirectory(Path.Combine(version.DirectoryPath, "saves", "world"));
            File.WriteAllText(Path.Combine(version.DirectoryPath, "saves", "world", "level.dat"), "world");
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aG1kAAAAASUVORK5CYII=");
            VersionManagementService.SaveIcon(root, version, png);
            string icon = Path.Combine(version.DirectoryPath, VersionManagementService.IconFileName);
            Check(File.ReadAllBytes(icon).SequenceEqual(png), "图标保存到版本目录");
            Throws<ArgumentException>(() => VersionManagementService.SaveIcon(root, version, [1, 2, 3]), "拒绝非 PNG 图标");
            Check(File.ReadAllBytes(icon).SequenceEqual(png), "图标保存失败保留原图片");
            foreach (string invalid in new[] { "", "../escape", "CON", "name.", "name ", "a/b" })
                Throws<ArgumentException>(() => VersionManagementService.Rename(root, version, invalid), "拒绝非法或保留名称：" + invalid);
            WriteVersion(root, "已存在", """{"id":"已存在","type":"release"}""");
            Throws<IOException>(() => VersionManagementService.Rename(root, version, "已存在"), "拒绝覆盖现有版本");
            File.WriteAllText(Path.Combine(root, "versions", "占用"), "keep");
            Throws<IOException>(() => VersionManagementService.Rename(root, version, "占用"), "拒绝覆盖现有文件");
            Check(VersionManagementService.Rename(root, version, version.Name) == version, "名称未变不改写文件");
            version = VersionManagementService.Rename(root, version, "新的生存");
            Check(!Directory.Exists(Path.Combine(root, "versions", "生存")) && Directory.Exists(version.DirectoryPath), "目录同步改名");
            Check(File.ReadAllText(Path.Combine(version.DirectoryPath, "新的生存.jar")) == "client", "同名客户端 JAR 同步改名且内容不变");
            Check(File.ReadAllText(Path.Combine(version.DirectoryPath, "saves", "world", "level.dat")) == "world", "保留独立存档");
            Check(File.ReadAllBytes(Path.Combine(version.DirectoryPath, VersionManagementService.IconFileName)).SequenceEqual(png), "改名保留自定义图标");
            using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(version.DirectoryPath, "新的生存.json"))))
                Check(json.RootElement.GetProperty("id").GetString() == version.Name && json.RootElement.GetProperty("jar").GetString() == version.Name &&
                    json.RootElement.GetProperty("unknown").GetProperty("keep").GetBoolean(), "清单同步改名并保留未知属性");
            var scanned = InstalledVersionService.Scan(root).Single(x => x.Name == version.Name);
            Check(scanned.GameVersion == "1.21.1" && scanned.Loader == "Fabric: 0.16.14", "重新扫描保留真实游戏版本与加载器");
            var parameters = MinecraftLauncherParameters.CreateDefault(new MinecraftPath(root));
            parameters.VersionLoader = new LocalJsonVersionLoader(new MinecraftPath(root));
            var profile = await new MinecraftLauncher(parameters).GetVersionAsync(version.Name);
            Check(profile.Id == version.Name && profile.MainJarId == version.Name, "真实启动清单解析指向改名后的客户端");
            var vanilla = WriteVersion(root, "Case", """{"id":"1.20.4","type":"release"}""");
            File.WriteAllText(Path.Combine(vanilla.DirectoryPath, "Case.jar"), "vanilla");
            vanilla = VersionManagementService.Rename(root, vanilla, "case");
            Check(Path.GetFileName(vanilla.DirectoryPath) == "case" && File.Exists(Path.Combine(vanilla.DirectoryPath, "case.json")) &&
                File.Exists(Path.Combine(vanilla.DirectoryPath, "case.jar")), "支持仅大小写改变的改名");
            Check(InstalledVersionService.Scan(root).Single(x => x.Name == "case").GameVersion == "1.20.4", "原版未写 clientVersion 时改名保留原游戏编号");
            var child = WriteVersion(root, "child", """{"id":"child","inheritsFrom":"case"}""");
            Throws<IOException>(() => VersionManagementService.Rename(root, vanilla, "parent-renamed"), "拒绝破坏已知继承依赖");
            child = VersionManagementService.Rename(root, child, "child-renamed");
            Check(InstalledVersionService.Scan(root).Single(x => x.Name == child.Name).GameVersion == "1.20.4", "子版本改名保留继承关系");
            var jarParent = WriteVersion(root, "jar-parent", """{"id":"jar-parent","type":"release"}""");
            WriteVersion(root, "jar-child", """{"id":"jar-child","jar":"jar-parent","type":"release"}""");
            Throws<IOException>(() => VersionManagementService.Rename(root, jarParent, "jar-moved"), "拒绝破坏仅 JAR 引用的版本");
            Throws<IOException>(() => InstalledVersionService.GetDeletionTarget(root, jarParent), "删除同时保护仅 JAR 引用");
            var broken = WriteVersion(root, "broken", "{broken");
            Throws<IOException>(() => VersionManagementService.Rename(root, broken, "fixed"), "坏清单不能改名");
            Throws<IOException>(() => VersionManagementService.Rename(root, version with { DirectoryPath = root }, "escape"), "拒绝伪造版本目录");
            File.WriteAllText(Path.Combine(version.DirectoryPath, "conflict.json"), "keep");
            Throws<IOException>(() => VersionManagementService.Rename(root, version, "conflict"), "不覆盖版本内同名文件");
            Check(File.ReadAllText(Path.Combine(version.DirectoryPath, "conflict.json")) == "keep", "冲突文件原样保留");
            using (var locked = new FileStream(Path.Combine(version.DirectoryPath, version.Name + ".jar"), FileMode.Open, FileAccess.Read, FileShare.None))
                Throws<IOException>(() => VersionManagementService.Rename(root, version, "locked"), "文件被占用时明确失败");
            Check(File.Exists(Path.Combine(version.DirectoryPath, version.Name + ".json")) &&
                File.ReadAllText(Path.Combine(version.DirectoryPath, version.Name + ".jar")) == "client" &&
                !Directory.Exists(Path.Combine(root, "versions", "locked")), "改名失败恢复目录、清单与客户端");
            Check(!Directory.EnumerateDirectories(Path.Combine(root, "versions"), ".nyx-rename-*").Any(), "失败没有遗留暂存目录");
            foreach (var mode in Enum.GetValues<IsolationMode>())
                foreach (var selected in new[] { version, vanilla })
                    foreach (string category in new[] { "mods", "saves", "screenshots" })
                    {
                        string expected = Path.Combine(VersionIsolation.Resolve(root, selected, mode), category);
                        Check(VersionManagementService.GetQuickFolder(root, selected, category, mode) == expected && Directory.Exists(expected), "快捷目录匹配隔离策略：" + mode + "/" + category);
                    }
            Check(VersionManagementService.GetQuickFolder(root, version, "version", IsolationMode.Disabled) == version.DirectoryPath, "版本目录入口不受隔离开关影响");
            Throws<ArgumentException>(() => VersionManagementService.GetQuickFolder(root, version, "../outside", IsolationMode.All), "拒绝未知快捷路径");
            var removed = version with { Name = "removed", DirectoryPath = Path.Combine(root, "versions", "removed") };
            Throws<DirectoryNotFoundException>(() => VersionManagementService.GetQuickFolder(root, removed, "mods", IsolationMode.All), "不重建已经移除的版本目录");
        }
        finally
        {
            string full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("nyx-details-check-", StringComparison.Ordinal)) throw new IOException("拒绝清理非测试目录。");
            Directory.Delete(full, true);
        }
        Console.WriteLine($"通过 {_checks} 项版本详情管理检查。");
    }

    /// <summary>只向指定临时游戏目录写最小清单，并返回真实扫描结果，供无 Mock 的文件系统断言使用。</summary>
    private static InstalledVersion WriteVersion(string root, string name, string json)
    {
        string path = Path.Combine(root, "versions", name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, name + ".json"), json);
        return InstalledVersionService.Scan(root).Single(x => x.Name == name);
    }

    /// <summary>验证具体业务结果，失败立即中断并标明受影响行为。</summary>
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); _checks++; }

    /// <summary>要求边界错误确实以指定异常报告，防止失败被伪装成成功。</summary>
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { _checks++; return; }
        throw new Exception(message);
    }
}
