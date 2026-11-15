using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Services;
using NyxLauncher.Themes;
using NyxLauncher.Views;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace NyxLauncher;

/// <summary>用临时外观配置验证自定义配色三行的布局、取色实时预览与关闭浮层后的持久化，并保存真实控件截图。</summary>
public partial class App : Application
{
    private Window? _window;
    private readonly string _log = Path.Combine(AppContext.BaseDirectory, "appearance-checks.log");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>临时外观配置：浅色组为亮色系、深色组为暗色系，便于确认两组互不影响。</summary>
    private const string Seed = """
        {
          "Mode": 1,
          "Preset": 0,
          "Light": { "Tone": "#336699", "Primary": "#AA4400", "Secondary": "#008877" },
          "Dark": { "Tone": "#112233", "Primary": "#80C99A", "Secondary": "#A2D6B4" },
          "BackgroundOpacity": 0.3,
          "SidebarOpacity": 0.45
        }
        """;

    /// <summary>加载正式应用样式，使检查使用与启动器相同的控件模板。</summary>
    public App() => InitializeComponent();

    /// <summary>检查设置页外观区域的三行自定义配色、实时预览、关闭持久化与浅深色截图；不读写用户配置目录。</summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nyx-appearance-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        File.WriteAllText(_log, "START\n");
        try
        {
            string themePath = Path.Combine(temporary, "theme.json");
            File.WriteAllText(themePath, Seed);
            // 与 ReleaseApp 相同：替换共享外观服务的存储，产品代码不增加测试专用入口。
            var themes = ThemeManager.Current;
            typeof(ThemeManager).GetField("<Store>k__BackingField", Private)!.SetValue(themes, new ThemeSettingsStore(themePath));
            themes.Initialize();

            var root = new Grid
            {
                Width = 760,
                Height = 980,
                Background = new SolidColorBrush(Color.FromArgb(255, 248, 248, 248)),
                RequestedTheme = ElementTheme.Light,
            };
            var background = new Image { Opacity = 0 };
            var view = new AppearanceSettingsView();
            root.Children.Add(background);
            root.Children.Add(view);
            _window = new Window { Title = "Appearance UI checks", Content = root };
            _window.Activate();
            double scale = root.XamlRoot?.RasterizationScale ?? 1;
            _window.AppWindow.Resize(new((int)(root.Width * scale) + 40, (int)(root.Height * scale) + 80));
            themes.Attach(root, background, _window.AppWindow);
            await Task.Delay(900);

            var preset = Element<ComboBox>(view, "Preset");
            Check(preset.SelectedIndex == 0, "临时配置初始为绿色预设，实际索引 " + preset.SelectedIndex);
            preset.SelectedIndex = 2;
            await Task.Delay(900);
            Check(preset.SelectedIndex == 2, "主题色切换为自定义");

            var light = ReadGroup(themePath, "Light");
            var dark = ReadGroup(themePath, "Dark");
            Check(light == ("#336699", "#AA4400", "#008877"), "切换自定义后浅色组保持临时配置颜色");
            Check(dark == ("#112233", "#80C99A", "#A2D6B4"), "切换自定义后深色组保持临时配置颜色");

            var toneRow = Element<Button>(view, "ToneRow");
            var primaryRow = Element<Button>(view, "PrimaryRow");
            var secondaryRow = Element<Button>(view, "SecondaryRow");
            var toneSwatch = Element<Border>(view, "ToneSwatch");
            var primarySwatch = Element<Border>(view, "PrimarySwatch");
            var secondarySwatch = Element<Border>(view, "SecondarySwatch");
            var colorMode = Element<ComboBox>(view, "ColorMode");
            Check(colorMode.SelectedIndex == 0, "自定义配色默认编辑浅色组");

            CheckRow(root, view, toneRow, toneSwatch, "色调", light.Tone, "色调");
            CheckRow(root, view, primaryRow, primarySwatch, "主色", light.Primary, "主色");
            CheckRow(root, view, secondaryRow, secondarySwatch, "副色", light.Secondary, "副色");
            double[] widths = { toneRow.ActualWidth, primaryRow.ActualWidth, secondaryRow.ActualWidth };
            Check(widths.Max() - widths.Min() <= 1, "三行等宽：" + string.Join(" / ", widths.Select(width => width.ToString("F1"))));

            var applyButton = Descendants(root).OfType<Button>()
                .FirstOrDefault(button => button.Content is string content && content.Contains("应用", StringComparison.Ordinal));
            Check(applyButton is null, "自定义配色不再显示“应用”按钮");

            await DrivePickerAsync(view, toneRow, "TonePicker", toneSwatch, Color.FromArgb(255, 255, 0, 0), "色调",
                new[] { "TintBrush", "ShellBrush" },
                () => ReadGroup(themePath, "Light").Tone == light.Tone);

            var lightAfterTone = ReadGroup(themePath, "Light");
            Check(lightAfterTone.Tone == "#FF0000", "关闭浮层后浅色组色调持久化为 #FF0000，实际 " + lightAfterTone.Tone);
            Check(lightAfterTone.Primary == light.Primary && lightAfterTone.Secondary == light.Secondary,
                "编辑色调不改动浅色组主色与副色");
            Check(ReadGroup(themePath, "Dark") == dark, "编辑浅色组不影响深色组颜色");
            Check(Hex(SwatchColor(toneSwatch)) == "#FF0000", "关闭浮层后色调色块显示已保存颜色：" + Hex(SwatchColor(toneSwatch)));

            colorMode.SelectedIndex = 1;
            await Task.Delay(500);
            Check(colorMode.SelectedIndex == 1, "自定义配色切换到深色组");
            var darkBefore = ReadGroup(themePath, "Dark");
            var lightBeforeDark = ReadGroup(themePath, "Light");
            await DrivePickerAsync(view, primaryRow, "PrimaryPicker", primarySwatch, Color.FromArgb(255, 0, 255, 0), "深色组主色",
                new[] { "SystemControlHighlightAccentBrush", "AccentFillColorDefaultBrush" },
                () => ReadGroup(themePath, "Dark").Primary == darkBefore.Primary,
                expectBrushChange: false);

            var darkAfter = ReadGroup(themePath, "Dark");
            Check(darkAfter.Primary == "#00FF00", "关闭浮层后深色组主色持久化为 #00FF00，实际 " + darkAfter.Primary);
            Check(darkAfter.Tone == darkBefore.Tone && darkAfter.Secondary == darkBefore.Secondary,
                "编辑深色组主色不改动同组其他颜色");
            Check(ReadGroup(themePath, "Light") == lightBeforeDark, "编辑深色组不影响浅色组已保存颜色");
            Check(Hex(SwatchColor(primarySwatch)) == "#00FF00", "深色组主色色块显示已保存颜色：" + Hex(SwatchColor(primarySwatch)));

            Check(Descendants(root).OfType<InfoBar>().All(bar => !bar.IsOpen), "外观编辑过程中错误提示保持关闭");

            // 展示阶段：仍走真实取色浮层，把两组配色恢复为平静的绿色系；截图代表实际设计，不保留测试用荧光色。
            colorMode.SelectedIndex = 0;
            await Task.Delay(500);
            Check(colorMode.SelectedIndex == 0, "展示阶段先恢复浅色组");
            await RestoreColorAsync(view, toneRow, "TonePicker", Color.FromArgb(255, 0x39, 0x74, 0x50), "浅色组色调", () => ReadGroup(themePath, "Light").Tone);
            await RestoreColorAsync(view, primaryRow, "PrimaryPicker", Color.FromArgb(255, 0x28, 0x56, 0x3B), "浅色组主色", () => ReadGroup(themePath, "Light").Primary);
            await RestoreColorAsync(view, secondaryRow, "SecondaryPicker", Color.FromArgb(255, 0x39, 0x74, 0x50), "浅色组副色", () => ReadGroup(themePath, "Light").Secondary);

            colorMode.SelectedIndex = 1;
            await Task.Delay(500);
            Check(colorMode.SelectedIndex == 1, "展示阶段继续恢复深色组");
            await RestoreColorAsync(view, toneRow, "TonePicker", Color.FromArgb(255, 0x39, 0x74, 0x50), "深色组色调", () => ReadGroup(themePath, "Dark").Tone);
            await RestoreColorAsync(view, primaryRow, "PrimaryPicker", Color.FromArgb(255, 0x80, 0xC9, 0x9A), "深色组主色", () => ReadGroup(themePath, "Dark").Primary);
            await RestoreColorAsync(view, secondaryRow, "SecondaryPicker", Color.FromArgb(255, 0xA2, 0xD6, 0xB4), "深色组副色", () => ReadGroup(themePath, "Dark").Secondary);

            colorMode.SelectedIndex = 0;
            await Task.Delay(600);
            var restingLight = ("#397450", "#28563B", "#397450");
            var restingDark = ("#397450", "#80C99A", "#A2D6B4");
            Check(ReadGroup(themePath, "Light") == restingLight, "浅色组已恢复为 #397450/#28563B/#397450，实际 " + ReadGroup(themePath, "Light"));
            Check(ReadGroup(themePath, "Dark") == restingDark, "深色组已恢复为 #397450/#80C99A/#A2D6B4，实际 " + ReadGroup(themePath, "Dark"));
            Check(Hex(SwatchColor(toneSwatch)) == restingLight.Item1 && Hex(SwatchColor(primarySwatch)) == restingLight.Item2 &&
                Hex(SwatchColor(secondarySwatch)) == restingLight.Item3, "浅色截图前三行色块显示恢复后的浅色组颜色");
            Check(RowColor(toneRow) == ResourceColor("ShellBrush"), "浅色截图前配色行背景使用 ShellBrush");
            Check(Hex(ResourceColor("PageBrush")) == "#FAFBF8", "浅色截图前页面底色为浅色主题：" + Hex(ResourceColor("PageBrush")));
            Check(Descendants(root).OfType<InfoBar>().All(bar => !bar.IsOpen), "恢复配色后错误提示保持关闭");
            await SnapshotAsync(root, "appearance-light.png");

            colorMode.SelectedIndex = 1;
            await Task.Delay(500);
            Check(colorMode.SelectedIndex == 1, "深色截图前自定义配色停留在深色组");
            var mode = Element<ComboBox>(view, "Mode");
            Color lightShell = ResourceColor("ShellBrush");
            mode.SelectedIndex = 2;
            await Task.Delay(800);
            Check(mode.SelectedIndex == 2, "外观已切换为深色，实际索引 " + mode.SelectedIndex);
            Check(ReadMode(themePath) == 2, "theme.json 已持久化深色模式，实际 " + ReadMode(themePath));
            Check(root.ActualTheme == ElementTheme.Dark, "根节点实际主题为深色：" + root.ActualTheme);
            Check(Hex(SwatchColor(toneSwatch)) == restingDark.Item1 && Hex(SwatchColor(primarySwatch)) == restingDark.Item2 &&
                Hex(SwatchColor(secondarySwatch)) == restingDark.Item3, "深色截图前三行色块显示深色组恢复值：" +
                Hex(SwatchColor(toneSwatch)) + " / " + Hex(SwatchColor(primarySwatch)) + " / " + Hex(SwatchColor(secondarySwatch)));
            Check(ResourceColor("ShellBrush") != lightShell, "切换深色后配色行背景画刷随之变化");
            Check(RowColor(toneRow) == ResourceColor("ShellBrush"), "深色截图前配色行背景使用 ShellBrush");
            Check(Hex(ResourceColor("PageBrush")) == "#202020", "深色截图前页面底色为深色主题：" + Hex(ResourceColor("PageBrush")));
            Check(Luminance(ResourceColor("ShellBrush")) < 0.35, "深色截图前行背景为暗色：" + Hex(ResourceColor("ShellBrush")));
            await SnapshotAsync(root, "appearance-dark.png");

            File.AppendAllText(_log, "PASS\n");
        }
        catch (Exception ex) { Environment.ExitCode = 1; File.AppendAllText(_log, "FAIL " + ex + "\n"); }
        finally
        {
            try { _window?.Close(); } catch (Exception) { }
            try { Directory.Delete(temporary, true); } catch (Exception) { }
            Exit();
        }
    }

    /// <summary>断言一行全宽配色控件：标签在左、色块在右且填充为已保存颜色。</summary>
    private void CheckRow(FrameworkElement root, FrameworkElement view, Button row, Border swatch, string label, string expectedHex, string what)
    {
        Check(row.Visibility == Visibility.Visible, what + "行可见");
        Check(row.ActualHeight >= 43.5, $"{what}行高度不小于 44：{row.ActualHeight:F1}");
        Check(row.ActualWidth >= view.ActualWidth * 0.85, $"{what}行接近内容宽度：{row.ActualWidth:F1} / {view.ActualWidth:F1}");
        Check(row.Flyout is not null, what + "行点击打开取色浮层");

        var text = Descendants(row).OfType<TextBlock>().FirstOrDefault(block => block.Text == label);
        Check(text is not null, what + "行左侧显示标签 " + label);
        var rowPoint = Position(row, root);
        var textPoint = Position(text!, root);
        Check(textPoint.X - rowPoint.X <= 48, $"{what}行标签靠左：标签 X {textPoint.X:F1}，行 X {rowPoint.X:F1}");
        Check(text!.ActualWidth > 0 && text.ActualHeight > 0, $"{what}行标签已布局：{text.ActualWidth:F1}×{text.ActualHeight:F1}");

        var swatchPoint = Position(swatch, root);
        Check(swatchPoint.X > rowPoint.X + row.ActualWidth * 0.6,
            $"{what}行色块在右侧：色块 X {swatchPoint.X:F1}，行 X {rowPoint.X:F1}，行宽 {row.ActualWidth:F1}");
        Check(textPoint.X < swatchPoint.X, $"{what}行标签位于色块左侧");
        double gap = rowPoint.X + row.ActualWidth - (swatchPoint.X + swatch.ActualWidth);
        Check(gap is >= -1 and <= 60, $"{what}行色块靠右且不越过行边界：距右边缘 {gap:F1}");
        Check(swatch.ActualWidth is >= 40 and <= 60 && swatch.ActualHeight is >= 20 and <= 34,
            $"{what}色块尺寸约 48×26：{swatch.ActualWidth:F1}×{swatch.ActualHeight:F1}");
        Check(swatch.CornerRadius.TopLeft > 0, $"{what}色块为圆角：" + swatch.CornerRadius.TopLeft);
        Check(Hex(SwatchColor(swatch)) == expectedHex, $"{what}色块填充等于已保存颜色 {expectedHex}：{Hex(SwatchColor(swatch))}");
    }

    /// <summary>打开真实浮层改色：确认拖动期间应用画刷与色块实时变化且不写盘，关闭后由控件持久化。</summary>
    private async Task DrivePickerAsync(FrameworkElement owner, Button row, string pickerName, Border swatch, Color color, string what,
        IReadOnlyList<string> brushKeys, Func<bool> stillOldDuringPreview, bool expectBrushChange = true)
    {
        var flyout = row.Flyout ?? throw new Exception(what + "行没有颜色浮层");
        var before = brushKeys.ToDictionary(key => key, ResourceColor);
        bool closed = false;
        flyout.Closed += (_, _) => closed = true;
        flyout.ShowAt(row);
        ColorPicker? picker = null;
        for (int i = 0; i < 30; i++)
        {
            picker = Find<ColorPicker>(owner, pickerName) ?? PickerInFlyout(row, pickerName);
            if (picker is not null && flyout.IsOpen && picker.IsLoaded) break;
            await Task.Delay(100);
        }
        File.AppendAllText(_log, $"{what}浮层诊断 IsOpen={flyout.IsOpen} 取色器={picker is not null} IsLoaded={picker?.IsLoaded} " +
            $"XamlRoot={picker?.XamlRoot is not null} 行XamlRoot={row.XamlRoot is not null} 行IsLoaded={row.IsLoaded}\n");
        if (picker is null) throw new Exception($"浮层内找不到取色器 {pickerName}");
        Check(flyout.IsOpen, what + "取色浮层已打开");
        Check(picker.IsLoaded, what + "取色器随浮层加载");
        picker.Color = color;
        await Task.Delay(220);

        if (expectBrushChange)
            Check(brushKeys.Any(key => ResourceColor(key) != before[key]), what + "取色时应用画刷立即改变（实时预览）");
        else
            File.AppendAllText(_log, what + "取色时应用画刷未变（当前编辑非当前明暗组，允许）\n");
        Check(Hex(SwatchColor(swatch)) == Hex(color), $"{what}色块随取色实时更新：{Hex(SwatchColor(swatch))}");
        Check(stillOldDuringPreview(), what + "取色器打开期间不写入 theme.json");

        flyout.Hide();
        for (int i = 0; i < 60 && !closed; i++) await Task.Delay(100);
        Check(closed, what + "关闭浮层触发 Closed 事件");
        await Task.Delay(700);
    }

    /// <summary>展示前通过真实取色浮层恢复一项颜色：设色、关闭、等待控件保存，并核对 theme.json 写入值。</summary>
    private async Task RestoreColorAsync(FrameworkElement owner, Button row, string pickerName, Color color, string what, Func<string> persisted)
    {
        var flyout = row.Flyout ?? throw new Exception(what + "行没有颜色浮层");
        bool closed = false;
        flyout.Closed += (_, _) => closed = true;
        flyout.ShowAt(row);
        ColorPicker? picker = null;
        for (int i = 0; i < 30; i++)
        {
            picker = Find<ColorPicker>(owner, pickerName) ?? PickerInFlyout(row, pickerName);
            if (picker is not null && flyout.IsOpen && picker.IsLoaded) break;
            await Task.Delay(100);
        }
        if (picker is null) throw new Exception($"浮层内找不到取色器 {pickerName}");
        Check(flyout.IsOpen && picker.IsLoaded, what + "取色器随浮层打开");
        picker.Color = color;
        await Task.Delay(220);
        flyout.Hide();
        for (int i = 0; i < 60 && !closed; i++) await Task.Delay(100);
        Check(closed, what + "关闭浮层触发 Closed 事件");
        await Task.Delay(600);
        Check(persisted() == Hex(color), $"{what}已通过取色器恢复为 {Hex(color)}，实际 {persisted()}");
    }

    /// <summary>读取配色行的背景画刷颜色，用于确认行背景跟随当前主题画刷。</summary>
    private static Color RowColor(Button row) => row.Background switch
    {
        SolidColorBrush brush => brush.Color,
        _ => throw new Exception("配色行没有背景画刷"),
    };

    /// <summary>按感知亮度判断画刷是否为暗色，避免深色截图仍沿用浅色行背景。</summary>
    private static double Luminance(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;

    /// <summary>读取共享主题画刷的当前颜色，用于确认取色预览已经作用到应用资源。</summary>
    private static Color ResourceColor(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? value) && value is SolidColorBrush brush) return brush.Color;
        throw new Exception("缺少共享画刷 " + key);
    }

    /// <summary>读取临时 theme.json 中一组配色；字段名大小写不敏感，兼容项目序列化设置。</summary>
    private static (string Tone, string Primary, string Secondary) ReadGroup(string path, string group)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var colors = Property(document.RootElement, group);
        return (Property(colors, "Tone").GetString() ?? "", Property(colors, "Primary").GetString() ?? "",
            Property(colors, "Secondary").GetString() ?? "");
    }

    /// <summary>读取临时 theme.json 中持久化的外观模式，确认深色截图来自真实设置而非只改根节点。</summary>
    private static int ReadMode(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return Property(document.RootElement, "Mode").GetInt32();
    }

    /// <summary>按名称大小写不敏感读取 JSON 字段，字段缺失时给出明确失败信息。</summary>
    private static JsonElement Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw new Exception("theme.json 缺少字段 " + name);
    }

    /// <summary>取色块实际填充色；非纯色填充视为设计不符。</summary>
    private static Color SwatchColor(Border swatch) => swatch.Background switch
    {
        SolidColorBrush brush => brush.Color,
        null => throw new Exception("色块没有填充色"),
        var other => throw new Exception("色块填充不是纯色：" + other.GetType().Name),
    };

    /// <summary>转换为不透明 RGB 字符串，与持久化格式一致。</summary>
    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>读取控件的生成字段，兼容延迟加载后再取值；缺失时给出可定位的失败信息。</summary>
    private static T Element<T>(object target, string name) where T : class =>
        Find<T>(target, name) ?? throw new Exception($"找不到控件 {name}（{typeof(T).Name}）");

    /// <summary>在页面字段或命名范围内查找控件，延迟创建的元素在加载后可见。</summary>
    private static T? Find<T>(object target, string name) where T : class
    {
        var field = target.GetType().GetField(name, Private);
        if (field?.GetValue(target) is T value) return value;
        if (target is FrameworkElement element && element.FindName(name) is T found) return found;
        return null;
    }

    /// <summary>浮层内容有独立命名范围，按 x:Name 在浮层内部兜底查找取色器。</summary>
    private static ColorPicker? PickerInFlyout(Button row, string name)
    {
        if ((row.Flyout as Flyout)?.Content is not DependencyObject content) return null;
        if (content is ColorPicker direct && direct.Name == name) return direct;
        return Descendants(content).OfType<ColorPicker>().FirstOrDefault(picker => picker.Name == name);
    }

    /// <summary>遍历真实视觉树，用于确认没有残留的“应用”按钮或打开的提示条。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }

    /// <summary>换算元素相对根节点的位置，用于断言标签与色块的左右关系。</summary>
    private static Windows.Foundation.Point Position(FrameworkElement element, UIElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformPoint(new Windows.Foundation.Point(0, 0));

    /// <summary>直接捕获真实 XAML 视觉树到 PNG，失败会使验收失败而不生成伪造截图。</summary>
    private async Task SnapshotAsync(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        string path = Path.Combine(AppContext.BaseDirectory, name);
        using (var file = File.Create(path))
        {
            using var stream = file.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
        }
        Check(bitmap.PixelWidth > 0 && new FileInfo(path).Length > 4096, $"截图已保存 {name}（{bitmap.PixelWidth}×{bitmap.PixelHeight}）");
        File.AppendAllText(_log, "shot " + path + "\n");
    }

    /// <summary>记录具体断言并在失败时中断验收。</summary>
    private void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); File.AppendAllText(_log, message + "\n"); }
}
