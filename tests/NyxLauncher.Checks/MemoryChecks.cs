using NyxLauncher.Services;

internal static class MemoryChecks
{
    /// <summary>在独立临时目录验证继承、覆盖、恢复、坏配置保护、模组目录和动态分配，不触碰用户配置。</summary>
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string global = Path.Combine(root, "global.json");
            var first = new InstalledVersion("first", Path.Combine(root, "first"), "1.21.1", "正式版", "Fabric", null);
            var second = first with { Name = "second", DirectoryPath = Path.Combine(root, "second") };
            Check(MemorySettingsStore.Read(first, global).Automatic, "默认跟随自动分配");
            MemorySettingsStore.Save(new(false, 6144), globalPath: global);
            Check(MemorySettingsStore.Read(first, global) == new MemorySettings(false, 6144), "默认版本继承全局");
            Directory.CreateDirectory(first.DirectoryPath);
            MemorySettingsStore.Save(new(false, 8192), first, global);
            MemorySettingsStore.Save(new(false, 3072), globalPath: global);
            Check(MemorySettingsStore.Read(first, global).ManualMiB == 8192 && MemorySettingsStore.Read(second, global).ManualMiB == 3072, "局部覆盖不受全局更改影响且不污染其他版本");
            MemorySettingsStore.Reset(first);
            Check(MemorySettingsStore.Read(first, global).ManualMiB == 3072 && !File.Exists(MemorySettingsStore.PathFor(first)), "恢复后继承最新全局");
            MemorySettingsStore.Save(new(true, 4096), first, global);
            Check(MemorySettingsStore.Read(first, global).Automatic && !MemorySettingsStore.Read(second, global).Automatic, "版本可单独自动分配");
            var renamed = first with { DirectoryPath = Path.Combine(root, "renamed") };
            Directory.Move(first.DirectoryPath, renamed.DirectoryPath);
            Check(MemorySettingsStore.Read(renamed, global).Automatic, "版本改名保留覆盖");
            File.WriteAllText(MemorySettingsStore.PathFor(renamed), "broken");
            try { MemorySettingsStore.Save(new(), renamed, global); throw new Exception("损坏配置应拒绝覆盖"); }
            catch (System.Text.Json.JsonException) { }
            Check(File.ReadAllText(MemorySettingsStore.PathFor(renamed)) == "broken", "坏配置保留");
            Directory.CreateDirectory(global + ".tmp");
            try { MemorySettingsStore.Save(new(), globalPath: global); throw new Exception("应报告写入失败"); }
            catch (UnauthorizedAccessException) { }
            Check(MemorySettingsStore.Read(null, global).ManualMiB == 3072, "写入失败保留已保存设置");
            var roomy = new PhysicalMemory(32768, 24000);
            var vanilla = first with { Loader = "" };
            Check(MemoryAllocation.Resolve(new(), roomy, vanilla, 0) == 3072, "现代原版基础分配");
            Check(MemoryAllocation.Resolve(new(), roomy, vanilla with { GameVersion = "1.12.2" }, 0) == 2048, "旧版基础分配");
            Check(MemoryAllocation.Resolve(new(), roomy, first, 100) > MemoryAllocation.Resolve(new(), roomy, first, 0), "模组数量增加预算");
            Check(MemoryAllocation.Resolve(new(), new(16384, 3000), first, 100) < MemoryAllocation.Resolve(new(), roomy, first, 100), "总量和空闲量限制自动预算");
            Check(MemoryAllocation.Resolve(new(), new(4096, 200), first, 100) == 512, "低可用内存保持有效最小值");
            Check(MemoryAllocation.Resolve(new(false, 8192), new(16384, 1024), first, 0) == 8192, "手动允许超过空闲内存");
            Check(MemoryAllocation.Resolve(new(false, 8192), new(4096, 1024), first, 0) == 4096, "手动不超过物理总量");
            string mods = Path.Combine(renamed.DirectoryPath, "mods");
            Directory.CreateDirectory(mods);
            File.WriteAllText(Path.Combine(mods, "a.jar"), "");
            File.WriteAllText(Path.Combine(mods, "b.jar.disabled"), "");
            Check(MemoryAllocation.CountMods(root, renamed, IsolationMode.All) == 1 && MemoryAllocation.CountMods(root, renamed, IsolationMode.Disabled) == 0, "按隔离策略统计实际启用模组");
            var snapshot = MemoryAllocation.Snapshot();
            Check(snapshot.TotalMiB > 0 && snapshot.AvailableMiB >= 0 && snapshot.AvailableMiB <= snapshot.TotalMiB, "系统真实物理内存快照");
            Console.WriteLine("内存分配与继承检查通过。");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>业务断言失败时报告具体边界。</summary>
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
