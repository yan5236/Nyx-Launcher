using NyxLauncher.Services;

internal static class InstalledVersionChecks
{
    private static int _checks;

    /// <summary>用临时配置验证旧格式兼容、重启恢复、目录隔离、失效回退和写入失败保护；文件由 Run 统一清理。</summary>
    private static void CheckVersionSelection(string root)
    {
        string config = Path.Combine(root, "selection.json");
        string first = Path.Combine(root, ".minecraft");
        string second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(config, System.Text.Json.JsonSerializer.Serialize(new { Folders = new[] { first, second }, Selected = first }));
        var store = new GameFolderStore(config, root);
        InstalledVersion[] versions = [new("A", "", "", "", "", null), new("中文生存", "", "", "", "", null)];
        Check(store.RestoreVersion(versions) == versions[0], "旧配置缺少版本选择时回退首项");
        store.SelectVersion("中文生存");
        store = new GameFolderStore(config, root);
        Check(store.RestoreVersion(versions) == versions[1], "未启动游戏也能重启恢复所选版本");
        store.Select(second);
        Check(store.RestoreVersion(versions) == versions[0], "切换目录不沿用其他目录同名选择");
        store.SelectVersion("A");
        store.Select(first);
        Check(new GameFolderStore(config, root).RestoreVersion(versions) == versions[1], "切回目录保留各自选择");
        Check(store.RestoreVersion([versions[0]]) == versions[0], "已删除版本回退有效首项");
        Check(store.RestoreVersion([versions[0], versions[1] with { Error = "损坏" }]) == versions[0], "损坏版本不恢复");
        Check(store.RestoreVersion([]) is null, "空版本目录不产生选择");
        Directory.CreateDirectory(config + ".tmp");
        Throws<UnauthorizedAccessException>(() => store.SelectVersion("A"), "版本选择写入失败向界面报告");
        Check(store.RestoreVersion(versions) == versions[1] && new GameFolderStore(config, root).RestoreVersion(versions) == versions[1], "写入失败保留内存与磁盘的原选择");
        Directory.Delete(config + ".tmp");
        store.Remove(first);
        store.Add(first);
        store.Select(first);
        Check(store.RestoreVersion(versions) == versions[0], "移除目录清理对应版本选择");
    }

    /// <summary>在独立临时目录验证文件夹落盘、移除无损、真实清单读取与删除边界；结束只清理本次创建的测试树。</summary>
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-versions-check-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            CheckVersionSelection(root);
            string config = Path.Combine(root, "config", "folders.json");
            var store = new GameFolderStore(config, root);
            string defaultFolder = Path.Combine(root, ".minecraft");
            Check(Directory.Exists(defaultFolder) && store.Selected == defaultFolder, "启动创建并选择程序目录下的 .minecraft");
            string extra = Path.Combine(root, "中文 游戏");
            Directory.CreateDirectory(extra);
            File.WriteAllText(Path.Combine(extra, "keep.txt"), "preserve");
            store.Add(extra);
            store.Add(extra + Path.DirectorySeparatorChar);
            Check(store.Folders.Count == 2, "重复目录与尾部分隔符去重");
            store.Select(extra);
            var reloaded = new GameFolderStore(config, root);
            Check(reloaded.Selected == extra && reloaded.Folders.Count == 2, "重启恢复目录集合与当前选择");
            store.Remove(extra);
            Check(Directory.Exists(extra) && File.ReadAllText(Path.Combine(extra, "keep.txt")) == "preserve", "移除目录只修改配置");
            Check(new GameFolderStore(config, root).Selected == defaultFolder, "移除当前项回退剩余项并保存");
            store.Remove(defaultFolder);
            reloaded = new GameFolderStore(config, root);
            Check(reloaded.Folders.Count == 0 && reloaded.Selected is null && Directory.Exists(defaultFolder), "空集合重启不重新登记已移除默认项");
            Throws<DirectoryNotFoundException>(() => store.Add(Path.Combine(root, "missing")), "不存在的目录不能添加");
            Throws<ArgumentException>(() => store.Add("relative"), "相对路径不能登记");
            File.WriteAllText(config, "{broken");
            var corrupt = new GameFolderStore(config, root);
            Check(corrupt.LoadError is not null, "损坏配置明确报错");
            Throws<IOException>(() => corrupt.Add(extra), "损坏配置禁止覆盖");
            Check(File.ReadAllText(config) == "{broken", "损坏文件保持不变");
            string blocking = Path.Combine(root, "blocking");
            File.WriteAllText(blocking, "");
            var failed = new GameFolderStore(Path.Combine(blocking, "folders.json"), root);
            Throws<IOException>(() => failed.Add(extra), "写入失败向调用方报告");
            Check(failed.Folders.Count == 1 && failed.Selected == defaultFolder, "写入失败不修改内存");
            store = new GameFolderStore(Path.Combine(root, "missing-path.json"), root);
            store.Add(extra);
            Directory.Move(extra, extra + "-moved");
            Throws<DirectoryNotFoundException>(() => store.Select(extra), "失效目录保留记录但不能选中");
            Check(store.Selected == defaultFolder, "失败选择不覆盖当前项");

            WriteVersion(defaultFolder, "1.20.1", """{"id":"1.20.1","type":"release"}""");
            WriteVersion(defaultFolder, "Fabric 生存", """{"id":"custom","inheritsFrom":"1.20.1","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.10"}]}""");
            WriteVersion(defaultFolder, "Forge", """{"id":"forge","type":"release","libraries":[{"name":"net.minecraftforge:forge:1.20.1-47.4.0"}]}""");
            WriteVersion(defaultFolder, "NeoForge", """{"id":"neo","inheritsFrom":"1.20.1","libraries":[{"name":"net.neoforged:neoforge:21.1.1"}]}""");
            WriteVersion(defaultFolder, "broken", "{broken");
            WriteVersion(defaultFolder, "cycle", """{"id":"cycle","inheritsFrom":"cycle"}""");
            WriteVersion(defaultFolder, "escape", """{"id":"escape","inheritsFrom":"../../outside"}""");
            WriteVersion(defaultFolder, "missing-parent", """{"id":"child","inheritsFrom":"not-installed"}""");
            Directory.CreateDirectory(Path.Combine(defaultFolder, "versions", "without-json"));
            var versions = InstalledVersionService.Scan(defaultFolder);
            Check(versions.Count == 8, "只读取实际存在版本清单的目录");
            var fabric = versions.Single(x => x.Name == "Fabric 生存");
            Check(fabric.GameVersion == "1.20.1" && fabric.Kind == "正式版" && fabric.Loader == "Fabric: 0.16.10", "继承版本与 Fabric 构建准确展示");
            Check(versions.Single(x => x.Name == "Forge").Detail == "1.20.1 · 正式版 · Forge: 47.4.0", "Forge 坐标拆分游戏与加载器版本");
            Check(versions.Single(x => x.Name == "NeoForge").Loader == "NeoForge: 21.1.1", "NeoForge 识别");
            WriteVersion(defaultFolder, "ModernNeo", """{"id":"custom","type":"release","libraries":[{"name":"net.neoforged.fancymodloader:loader:4.0.45"}],"arguments":{"game":["--fml.neoForgeVersion","21.1.265","--fml.mcVersion","1.21.1"]}}""");
            var modernNeo = InstalledVersionService.Scan(defaultFolder).Single(x => x.Name == "ModernNeo");
            Check(modernNeo.Loader == "NeoForge: 21.1.265" && modernNeo.GameVersion == "1.21.1", "真实 NeoForge 清单通过 FML 参数识别");
            WriteVersion(defaultFolder, "RenamedFabric", """{"id":"自定义名称","clientVersion":"1.20.1","type":"release","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.14"}]}""");
            var renamed = InstalledVersionService.Scan(defaultFolder).Single(x => x.Name == "RenamedFabric");
            Check(renamed.GameVersion == "1.20.1" && renamed.LaunchDetail == "Minecraft 1.20.1 · Fabric: 0.16.14", "独立清单使用真实客户端版本，首页不重复自定义名称");
            Check(versions.Count(x => x.Error is not null) == 4, "坏清单、循环、越界继承和父版本缺失独立报错");
            Check(InstalledVersionService.Scan(Path.Combine(root, "中文 游戏-moved")).Count == 0, "无 versions 的真实文件夹为空");
            Throws<DirectoryNotFoundException>(() => InstalledVersionService.Scan(Path.Combine(root, "absent")), "失效根目录报告错误");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => InstalledVersionService.Scan(defaultFolder, cancellation.Token), "扫描响应取消");
            Check(InstalledVersionService.GetDeletionTarget(defaultFolder, fabric) == fabric.DirectoryPath, "删除仅定位当前直属版本");
            Throws<IOException>(() => InstalledVersionService.GetDeletionTarget(root, fabric), "拒绝其他根目录的版本");
            Throws<ArgumentException>(() => InstalledVersionService.GetDeletionTarget(defaultFolder, fabric with { Name = ".." }), "拒绝删除路径逃逸");
            Throws<IOException>(() => InstalledVersionService.GetDeletionTarget(defaultFolder, fabric with { DirectoryPath = root }), "拒绝伪造版本路径");
            Throws<IOException>(() => InstalledVersionService.GetDeletionTarget(defaultFolder, versions.Single(x => x.Name == "1.20.1")), "拒绝删除其他版本仍继承的基础版本");
            Check(Directory.Exists(fabric.DirectoryPath), "删除预检不修改任何文件");
        }
        finally
        {
            string full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("nyx-versions-check-", StringComparison.Ordinal)) throw new IOException("拒绝清理非测试目录。");
            Directory.Delete(full, true);
        }
        Console.WriteLine($"通过 {_checks} 项本地版本管理检查。");
    }

    /// <summary>仅在传入的测试游戏根目录写入最小版本 JSON，供真实文件读取断言使用。</summary>
    private static void WriteVersion(string folder, string name, string json)
    {
        string directory = Path.Combine(folder, "versions", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), json);
    }

    /// <summary>检查具体业务结果；失败时中断并给出对应行为名称。</summary>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }

    /// <summary>要求指定失败向调用方传播，防止失败被当成保存或删除成功。</summary>
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { _checks++; return; }
        throw new Exception(message);
    }
}
