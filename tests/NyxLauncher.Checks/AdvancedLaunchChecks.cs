using System.Diagnostics;
using CmlLib.Core.ProcessBuilder;
using NyxLauncher.Services;

internal static class AdvancedLaunchChecks
{
    /// <summary>在独立目录验证高级配置继承、原子保存、参数解析、GC 和兼容组件开关，不改用户配置或显卡设置。</summary>
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-advanced-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string global = Path.Combine(root, "global.json");
            var version = new InstalledVersion("test", Path.Combine(root, "test"), "1.21.1", "正式版", "", null);
            Directory.CreateDirectory(version.DirectoryPath);
            var local = new AdvancedLaunchSettings("-Dtitle=\"中文 空格\"", "--width 900", GarbageCollector.G1, true, true, false);
            Check(AdvancedLaunchSettingsStore.Read(version, global) == new AdvancedLaunchSettings(), "缺失配置使用默认值");
            AdvancedLaunchSettingsStore.Save(local, globalPath: global);
            Check(AdvancedLaunchSettingsStore.Read(version, global) == local, "无覆盖时继承全局");
            AdvancedLaunchSettingsStore.Save(local with { Collector = GarbageCollector.Z }, version, global);
            AdvancedLaunchSettingsStore.Save(new(), globalPath: global);
            Check(AdvancedLaunchSettingsStore.Read(version, global).Collector == GarbageCollector.Z && AdvancedLaunchSettingsStore.Read(null, global) == new AdvancedLaunchSettings(), "全局与版本双向隔离");
            AdvancedLaunchSettingsStore.Reset(version);
            Check(AdvancedLaunchSettingsStore.Read(version, global) == new AdvancedLaunchSettings(), "重置继承最新全局");
            AdvancedLaunchSettingsStore.Save(local, globalPath: global);
            Check(AdvancedLaunchSettingsStore.Read(version, global) == local && !File.Exists(AdvancedLaunchSettingsStore.PathFor(version)), "恢复后持续跟随后续全局修改");
            AdvancedLaunchSettingsStore.Save(new(), globalPath: global);
            AdvancedLaunchSettingsStore.Save(local, version, global);
            var renamed = version with { DirectoryPath = Path.Combine(root, "renamed") };
            Directory.Move(version.DirectoryPath, renamed.DirectoryPath);
            Check(AdvancedLaunchSettingsStore.Read(renamed, global) == local, "改名保留高级覆盖");
            File.WriteAllText(AdvancedLaunchSettingsStore.PathFor(renamed), "broken");
            Throws<System.Text.Json.JsonException>(() => AdvancedLaunchSettingsStore.Save(local, renamed, global));
            Check(File.ReadAllText(AdvancedLaunchSettingsStore.PathFor(renamed)) == "broken", "损坏配置不覆盖");
            Directory.CreateDirectory(global + ".tmp");
            Throws<UnauthorizedAccessException>(() => AdvancedLaunchSettingsStore.Save(local, globalPath: global));
            Check(AdvancedLaunchSettingsStore.Read(null, global) == new AdvancedLaunchSettings(), "写入失败保留旧值");
            Check(LaunchArguments.Parse("-Dpath=\"C:\\游戏 目录\\mods\"\r\n-Dx=1").SequenceEqual(new[] { "-Dpath=C:\\游戏 目录\\mods", "-Dx=1" }), "Windows 路径和多行参数不损坏");
            Check(LaunchArguments.Parse("\"\" \"a\\\"b\" \"C:\\a b\\\\\"").SequenceEqual(new[] { "", "a\"b", "C:\\a b\\" }), "空参数、转义引号及尾反斜杠");
            Throws<ArgumentException>(() => LaunchArguments.Parse("\"unclosed"));
            Throws<ArgumentException>(() => AdvancedLaunchSettingsStore.Validate(new(JvmArguments: "a\0b")));
            Throws<ArgumentException>(() => AdvancedLaunchSettingsStore.Validate(new(Collector: (GarbageCollector)99)));
            var option = new MLaunchOption();
            LaunchArguments.Apply(option, local with { Collector = GarbageCollector.Z }, 21);
            string[] jvm = option.ExtraJvmArguments.SelectMany(x => x.Values).ToArray();
            Check(jvm.Contains("-XX:+UseZGC") && !jvm.Any(x => x.Contains("G1")) && jvm.Contains("-Dtitle=中文 空格"), "ZGC 无默认 G1 冲突，保留用户参数");
            Check(option.ExtraGameArguments.SelectMany(x => x.Values).SequenceEqual(new[] { "--width", "900" }), "游戏参数独立传递");
            LaunchArguments.Apply(option, new(), 8);
            Check(!option.ExtraJvmArguments.SelectMany(x => x.Values).Any(x => x.Contains("GC")), "不指定 GC 不注入默认收集器");
            Throws<ArgumentException>(() => LaunchArguments.Apply(option, new(Collector: GarbageCollector.Z), 8));
            Throws<ArgumentException>(() => LaunchArguments.Apply(option, new(JvmArguments: "-XX:+UseG1GC", Collector: GarbageCollector.Z), 21));
            foreach (int major in new[] { 8, 17, 21, 25 })
            {
                var info = Info();
                LaunchCompatibility.Apply(info, new(), major, "game.Main", true);
                Check(info.ArgumentList.Contains("-jar") == (major < 19), "Wrapper 适用范围 " + major);
                Check(info.ArgumentList.Any(x => x.StartsWith("-javaagent:")) == (major >= 25), "Agent 适用范围 " + major);
                Check(info.ArgumentList.IndexOf("game.Main") < info.ArgumentList.IndexOf("--username"), "主类与游戏参数顺序");
                var disabled = Info();
                LaunchCompatibility.Apply(disabled, new(DisableJavaWrapper: true, DisableLwjglAgent: true), major, "game.Main", true);
                Check(!disabled.ArgumentList.Contains("-jar") && !disabled.ArgumentList.Any(x => x.StartsWith("-javaagent:")), "禁用开关生效 " + major);
            }
            var unaffected = Info();
            LaunchCompatibility.Apply(unaffected, new(), 25, "game.Main", false);
            Check(!unaffected.ArgumentList.Any(x => x.StartsWith("-javaagent:")), "非目标 LWJGL 不注入 Agent");
            var modular = Info();
            modular.ArgumentList[2] = "cpw.mods.bootstraplauncher.BootstrapLauncher";
            LaunchCompatibility.Apply(modular, new(), 17, modular.ArgumentList[2], false);
            Check(modular.ArgumentList[0] == "--add-exports=cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED", "模块化 Forge 向 Wrapper 开放主类");
            var legacy = new ProcessStartInfo { Arguments = "-cp \"C:\\游戏 路径\\a.jar\" game.Main --username Steve" };
            LaunchCompatibility.Apply(legacy, new(), 8, "game.Main", false);
            var parsed = LaunchArguments.Parse(legacy.Arguments);
            Check(legacy.ArgumentList.Count == 0 && parsed[1] == "C:\\游戏 路径\\a.jar" && parsed[2] == "-jar", "保留字符串命令行接口及 Wrapper 插入位置");
            string[] roundTrip = ["", "a\"b", @"C:\中文 目录\", "--username", "中文"];
            Check(LaunchArguments.Parse(LaunchArguments.Join(roundTrip)).SequenceEqual(roundTrip), "Windows 参数编码往返一致");
            Check(LaunchCompatibility.GpuPreference("AutoHDREnable=1;GpuPreference=1;", true) == "AutoHDREnable=1;GpuPreference=2;", "显卡偏好保留其他设置");
            Check(LaunchCompatibility.GpuPreference("GpuPreference=2;", false) == "GpuPreference=0;", "关闭高性能恢复系统默认");
            Console.WriteLine("高级启动设置、参数与兼容组件检查通过。");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>构造包含主类、类路径与游戏参数的最小命令行，用于检查插入位置。</summary>
    private static ProcessStartInfo Info()
    {
        var info = new ProcessStartInfo();
        foreach (string arg in new[] { "-cp", @"C:\游戏 空格\a.jar", "game.Main", "--username", "Steve" }) info.ArgumentList.Add(arg);
        return info;
    }

    /// <summary>确认指定异常真实发生，未发生时立即失败。</summary>
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("预期异常：" + typeof(T).Name);
    }

    /// <summary>对具体业务结果断言并保留失败原因。</summary>
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
