using NyxLauncher.Services;

internal static class ThemeChecks
{
    /// <summary>验证外观配置往返、两组颜色独立、非法输入拒绝与损坏文件保护，仅使用临时目录。</summary>
    public static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "nyx-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "theme.json");
            var store = new ThemeSettingsStore(path);
            Check(store.Current.Mode == AppearanceMode.System && store.Current.Preset == ThemePreset.Green, "默认跟随系统和绿色");
            Check(store.Current.SidebarOpacity == .55, "玻璃面板默认 55% 主题底色");
            Check(store.Current.HasBackground && store.Current.BackgroundPath is null && store.Current.BackgroundOpacity == 1, "默认显示完全不透明的时段场景背景");
            var home = store.Current with { BackgroundPath = Path.Combine(folder, "custom.png"), HideBackground = false };
            Check(store.Save(home) is null && new ThemeSettingsStore(path).Current == home, "自选背景设置往返");
            Check(store.Save(home with { BackgroundPath = null, HideBackground = true }) is null && !new ThemeSettingsStore(path).Current.HasBackground, "移除背景可持久化");
            foreach (var (hour, scene) in new[] { (0, "midnight"), (4, "predawn"), (6, "sunrise"), (9, "morning"), (12, "noon"), (17, "sunset"), (18, "dusk"), (21, "night"), (23, "midnight") })
                Check(TimeOfDayScene.Resolve(new DateTime(2026, 1, 1, hour, 30, 0)) == scene, $"{hour}:30 使用 {scene} 场景");
            Check(TimeOfDayScene.Resolve(new DateTime(2026, 1, 1, 7, 0, 0)) == "morning", "时段边界归属后一个时段");
            Check(TimeOfDayScene.Names.Count == 8, "内置八个时段场景");
            var custom = store.Current with { Mode = AppearanceMode.Dark, Preset = ThemePreset.Custom, Light = new("#123456", "#234567", "#345678"), Dark = new("#ABCDEF", "#FEDCBA", "#AABBCC"), BackgroundOpacity = 0, SidebarOpacity = .75 };
            Check(store.Save(custom) is null && new ThemeSettingsStore(path).Current == custom, "自定义配色与零不透明度往返");
            Check(custom.Colors(false) == custom.Light && custom.Colors(true) == custom.Dark, "明暗配色独立");
            Check(store.Save(custom with { BackgroundOpacity = 1 }) is null, "完全不透明边界");
            string previous = File.ReadAllText(path);
            foreach (var bad in new[] { custom with { SidebarOpacity = -1 }, custom with { SidebarOpacity = double.NaN }, custom with { SidebarOpacity = 1.01 }, custom with { BackgroundOpacity = -1 }, custom with { BackgroundOpacity = double.NaN }, custom with { Mode = (AppearanceMode)42 }, custom with { Light = new("red", "#000000", "#FFFFFF") }, custom with { Light = new("# 12345", "#000000", "#FFFFFF") } })
                Check(store.Save(bad) is not null && File.ReadAllText(path) == previous, "非法设置不覆盖配置");
            File.WriteAllText(path, "broken");
            var broken = new ThemeSettingsStore(path);
            Check(broken.LoadError is not null && broken.Save(custom) is not null && File.ReadAllText(path) == "broken", "损坏文件保留并报告");
            string occupied = Path.Combine(folder, "occupied"); Directory.CreateDirectory(occupied);
            var blocked = new ThemeSettingsStore(occupied);
            Check(blocked.Save(custom) is not null && blocked.Current != custom, "写入失败保持内存原值");
            Check(new ThemeSettings { Preset = ThemePreset.VuiBlue }.Colors(false).Primary == "#0078D4", "经典蓝预设");
            var dark = new BackgroundContrast(1, 1, new byte[] { 0, 0, 0, 255 });
            var light = new BackgroundContrast(1, 1, new byte[] { 255, 255, 255, 255 });
            Check(BackgroundContrast.TextColor(dark.Sample(0, 0, 50, 20, 100, 100, 0xFFFFFFFF, 1)) == 0xFFFFFFFF, "黑图使用白字");
            Check(BackgroundContrast.TextColor(light.Sample(0, 0, 50, 20, 100, 100, 0xFF000000, 1)) == 0xFF000000, "白图使用黑字");
            Check(BackgroundContrast.TextColor(dark.Sample(0, 0, 50, 20, 100, 100, 0xFFFFFFFF, 0)) == 0xFF000000, "图片不透明度为零时按底色选择文字");
            Check(BackgroundContrast.TextColor(BackgroundContrast.Composite(0xFFFFFFFF, 0xFF000000, .8)) == 0xFF000000, "白色遮罩覆盖黑图后仍使用黑字");
            Check(BackgroundContrast.SelectionBackground(0xFF28563B) == 0xFF28563B, "默认深绿选区保留原主色");
            foreach (uint primary in new uint[] { 0xFF80C99A, 0xFFFFFFFF, 0xFFFFFF00, 0xFF60CDFF })
                Check(BackgroundContrast.TextColor(BackgroundContrast.SelectionBackground(primary)) == 0xFFFFFFFF, "浅色主色生成适合原生白字的选区底色");
            var split = new BackgroundContrast(2, 1, new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 });
            Check(BackgroundContrast.TextColor(split.Sample(0, 0, 20, 20, 200, 100, 0xFFFFFFFF, 1)) == 0xFFFFFFFF &&
                BackgroundContrast.TextColor(split.Sample(180, 0, 20, 20, 200, 100, 0xFFFFFFFF, 1)) == 0xFF000000, "同一图片两侧分别选择文字颜色");
            foreach (bool darkTexture in new[] { true, false })
            {
                var pixels = new byte[4 * 4 * 4];
                for (int i = 0; i < 16; i++)
                {
                    byte value = darkTexture ? (i < 4 ? (byte)255 : (byte)80) : (i < 4 ? (byte)0 : (byte)160);
                    pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = value;
                    pixels[i * 4 + 3] = 255;
                }
                uint expected = darkTexture ? 0xFFFFFFFF : 0xFF000000;
                Check(BackgroundContrast.TextColor(new BackgroundContrast(4, 4, pixels).Sample(0, 0, 40, 40, 40, 40, 0xFFFFFFFF, 1)) == expected,
                    "少量亮斑或暗斑不改变主体纹理的文字颜色");
            }
            Console.WriteLine("Theme checks passed.");
        }
        finally { Directory.Delete(folder, true); }
    }

    /// <summary>断言具体配置行为，失败时抛出异常供命令行返回失败。</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
