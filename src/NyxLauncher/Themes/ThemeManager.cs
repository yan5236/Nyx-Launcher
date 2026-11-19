using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Services;
using Windows.UI;

namespace NyxLauncher.Themes;

/// <summary>在 UI 线程维护共享画刷与根主题；更新现有页面，不重建导航或丢失业务状态。</summary>
public sealed class ThemeManager
{
    public static ThemeManager Current { get; } = new();
    public ThemeSettingsStore Store { get; } = new();
    private ThemeSettings? _preview;
    private FrameworkElement? _root;
    private Image? _background;
    private BackgroundScene? _scene;
    private string? _loadedPath;
    private int _imageGeneration;
    private bool _trayHidden;
    private BackgroundTextContrast? _contrast;
    private readonly Dictionary<string, SolidColorBrush> _brushes = new();
    private readonly Dictionary<string, AcrylicBrush> _acrylics = new();
    public string? BackgroundError { get; private set; }
    public event Action? Changed;

    /// <summary>取色器拖动期间的临时配置；只原地更新共享画刷，不写盘也不切换原生模板主题，关闭取色器后再提交。</summary>
    public void Preview(ThemeSettings settings)
    {
        _preview = settings;
        if (_root is null) return;
        UpdateBrushes(_root.ActualTheme == ElementTheme.Dark);
    }

    /// <summary>是否存在尚未提交的取色预览；卸载外观设置时用于补交已选颜色。</summary>
    public bool HasPreview => _preview is not null;

    /// <summary>初始化全部共享画刷；必须在创建窗口和页面前调用。</summary>
    public void Initialize() => UpdateBrushes(false);

    /// <summary>把窗口焦点状态交给背景文字取样器，不改变用户保存的主题设置。</summary>
    public void SetForeground(bool foreground) => _contrast?.SetForeground(foreground);

    /// <summary>隐藏至托盘时停止轮换并卸载背景位图和取样；恢复时按当前时段及外观配置重新加载。</summary>
    public void SetTrayHidden(bool hidden)
    {
        _trayHidden = hidden;
        if (!hidden) { Apply(); return; }
        _imageGeneration++;
        _loadedPath = null;
        _scene?.SetFollowing(false);
        _scene?.Swap(null, animate: false);
        _contrast?.SetImage(null);
    }

    /// <summary>绑定窗口根节点和背景层，监听系统明暗变化并启动时段场景；窗口生命周期内仅调用一次。</summary>
    public void Attach(FrameworkElement root, Image background, Microsoft.UI.Windowing.AppWindow? window = null)
    {
        _root = root;
        _background = background;
        _contrast = new BackgroundTextContrast(root, background, window);
        _scene = new BackgroundScene(root, background, () => _contrast?.Invalidate());
        _scene.SceneDue += () => Apply();
        root.ActualThemeChanged += ActualThemeChanged;
        Apply();
    }

    /// <summary>系统实际主题变化后更新自定义画刷，不修改用户保存的跟随系统选项。</summary>
    private void ActualThemeChanged(FrameworkElement sender, object args)
    {
        _contrast?.Restore();
        UpdateBrushes(sender.ActualTheme == ElementTheme.Dark);
        _contrast?.Invalidate();
        Changed?.Invoke();
    }

    /// <summary>弹窗位于独立视觉树，显式继承窗口当前明暗并共享交互画刷，避免回退到系统默认配色。</summary>
    public void ApplyToDialog(ContentDialog dialog)
    {
        if (_root is not null) dialog.RequestedTheme = _root.ActualTheme;
        foreach (var pair in _brushes) dialog.Resources[pair.Key] = pair.Value;
    }

    /// <summary>成功保存后立即应用外观；失败返回错误并保留当前界面，取色预览在成功或失败后都退出生效状态。</summary>
    public string? Save(ThemeSettings settings)
    {
        var saved = Store.Current;
        var error = Store.Save(settings);
        if (error is null)
        {
            _preview = null;
            Apply(saved.Preset != settings.Preset || saved.Light != settings.Light || saved.Dark != settings.Dark);
        }
        else if (_preview is not null)
        {
            _preview = null;
            Apply(true);
        }
        return error;
    }

    /// <summary>当前生效配置；未提交的取色预览优先于已保存值。</summary>
    private ThemeSettings Effective => _preview ?? Store.Current;

    /// <summary>当前应显示的背景文件：移除时为空，自选图片优先，否则为本地时段对应的内置场景。</summary>
    private static string? ResolveBackground(ThemeSettings settings) =>
        settings.HideBackground ? null : settings.BackgroundPath ?? TimeOfDayScene.PathOf(TimeOfDayScene.Resolve(DateTime.Now));

    /// <summary>应用明暗模式与背景透明度；配色变化时刷新原生模板缓存，只有图片路径改变时才重新解码图片。</summary>
    private void Apply(bool refreshPalette = false)
    {
        if (_root is null || _background is null) return;
        var current = Effective;
        _contrast?.Restore();
        if (refreshPalette) _root.RequestedTheme = _root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        _root.RequestedTheme = current.Mode switch { AppearanceMode.Light => ElementTheme.Light, AppearanceMode.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        UpdateBrushes(_root.ActualTheme == ElementTheme.Dark);
        // 只有内置场景需要按时段轮换；自选图片或移除背景时停止计时。
        _scene?.SetFollowing(!_trayHidden && current.HasBackground && current.BackgroundPath is null);
        var path = _trayHidden ? null : ResolveBackground(current);
        if (path != _loadedPath)
        {
            _loadedPath = path;
            BackgroundError = null;
            if (path is null) { _scene?.Swap(null); _contrast?.SetImage(null); }
            _ = LoadBackgroundAsync(path, ++_imageGeneration);
        }
        _background.Opacity = current.BackgroundOpacity;
        _contrast?.Invalidate();
        Changed?.Invoke();
    }

    /// <summary>有界读取背景并以流解码，使用最长边 1024 像素的独立取样图保留小字号下的纹理；避免文件锁，丢弃迟到图像，失败回退纯色。</summary>
    private async Task LoadBackgroundAsync(string? path, int generation)
    {
        if (path is null) return;
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length is 0 or > 32 * 1024 * 1024) throw new IOException("背景图片须小于 32 MiB。");
            using var stream = new MemoryStream();
            await input.CopyToAsync(stream);
            if (generation != _imageGeneration) return;
            stream.Position = 0;
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            if (generation != _imageGeneration) return;
            if (decoder.PixelWidth is 0 or > 8192 || decoder.PixelHeight is 0 or > 8192 || (long)decoder.PixelWidth * decoder.PixelHeight > 32_000_000)
                throw new IOException("背景图片尺寸超出限制。 ");
            double scale = Math.Min(1, 1024d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            uint width = Math.Max(1, (uint)(decoder.PixelWidth * scale)), height = Math.Max(1, (uint)(decoder.PixelHeight * scale));
            var pixels = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Straight, new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = width, ScaledHeight = height },
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);
            bool rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
            if (generation != _imageGeneration) return;
            var sample = new BackgroundContrast((int)(rotated ? height : width), (int)(rotated ? width : height), pixels.DetachPixelData());
            stream.Position = 0;
            var bitmap = new BitmapImage { DecodePixelWidth = (int)Math.Min(2048u, decoder.PixelWidth) };
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            if (generation == _imageGeneration && _background is not null)
            {
                _scene?.Swap(bitmap);
                _contrast?.SetImage(sample);
            }
        }
        catch (Exception ex)
        {
            if (generation != _imageGeneration) return;
            _scene?.Swap(null);
            _contrast?.SetImage(null);
            BackgroundError = "无法加载背景图片：" + ex.Message;
            Changed?.Invoke();
        }
    }

    /// <summary>按三种主题色生成表面、玻璃与交互画刷，原地更新以覆盖已打开的模板和弹窗。</summary>
    private void UpdateBrushes(bool dark)
    {
        var current = Effective;
        var colors = current.Colors(dark);
        var tone = Parse(colors.Tone); var primary = Parse(colors.Primary); var secondary = Parse(colors.Secondary);
        bool glass = current.HasBackground;
        Set("ShellBrush", Mix(dark ? Parse("#0F1311") : Parse("#EEF2EE"), tone, dark ? .10 : .06));
        Set("PageBrush", dark ? Parse("#161B18") : Parse("#F7F9F7"));
        Set("CardBrush", dark ? Mix(Parse("#202622"), tone, .05) : Parse("#FFFFFF"));
        Set("MenuSurfaceBrush", dark ? Mix(Parse("#1B201D"), tone, .05) : Parse("#FFFFFF"));
        Set("CardHoverBrush", Mix(_brushes["CardBrush"].Color, dark ? Parse("#FFFFFF") : tone, dark ? .06 : .07));
        Set("CardPressedBrush", Mix(_brushes["CardBrush"].Color, dark ? Parse("#FFFFFF") : tone, dark ? .10 : .13));
        // 背景图片存在时卡片保留部分透明，让底层面板的玻璃质感透出；无图时回到完全不透明。
        foreach (string key in new[] { "CardBrush", "CardHoverBrush", "CardPressedBrush" })
            _brushes[key].Opacity = glass ? (dark ? .66 : .74) : 1;
        Set("LineBrush", dark ? Parse("#343C37") : Parse("#DCE3DD"));
        _brushes["LineBrush"].Opacity = glass ? .8 : 1;
        Set("TextBrush", dark ? Parse("#EEF3EF") : Parse("#1C2721"));
        Set("MutedBrush", dark ? Parse("#A6B3AA") : Parse("#5E6B63"));
        Set("TintBrush", Mix(dark ? Parse("#1E2420") : Parse("#FFFFFF"), tone, dark ? .30 : .16));
        Set("SecondaryBrush", secondary);
        Set("DangerBrush", dark ? Parse("#FF99A4") : Parse("#B3261E"));
        Set("DangerLineBrush", dark ? Parse("#9C5559") : Parse("#E8B4AF"));
        Set("SystemControlHighlightAccentBrush", primary);
        UpdateGlass(dark, tone, current);
        var foreground = current.Preset == ThemePreset.VuiBlue && !dark ? Parse("#FFFFFF") : Contrast(primary);
        foreach (string key in new[] { "AccentButtonBackground", "AccentButtonBorderBrush", "AccentFillColorDefaultBrush" }) Set(key, primary);
        foreach (string key in new[] { "AccentButtonBackgroundPointerOver", "AccentButtonBorderBrushPointerOver", "AccentFillColorSecondaryBrush" }) Set(key, Mix(primary, foreground, .10));
        foreach (string key in new[] { "AccentButtonBackgroundPressed", "AccentButtonBorderBrushPressed", "AccentFillColorTertiaryBrush" }) Set(key, Mix(primary, foreground, .18));
        foreach (string key in new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed", "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush" }) Set(key, foreground);
        uint selection = BackgroundContrast.SelectionBackground(0xFF000000u | (uint)primary.R << 16 | (uint)primary.G << 8 | primary.B);
        var selectionColor = Color.FromArgb(255, (byte)(selection >> 16), (byte)(selection >> 8), (byte)selection);
        foreach (string key in new[] { "TextControlSelectionHighlightColor", "AccentFillColorSelectedTextBackgroundBrush" }) Set(key, selectionColor);
        Set("AccentTextFillColorPrimaryBrush", secondary);
        Set("AccentTextFillColorSecondaryBrush", secondary);
        Application.Current.Resources["SystemAccentColor"] = primary;
        UpdateControlBrushes();
    }

    /// <summary>生成三种玻璃表面：侧栏、内容面板与首页控制台；均为应用内亚克力，透出并模糊背景场景。</summary>
    private void UpdateGlass(bool dark, Color tone, ThemeSettings current)
    {
        var tint = Mix(dark ? Parse("#101512") : Parse("#F3F6F3"), tone, dark ? .08 : .05);
        double sidebar = current.HasBackground ? current.SidebarOpacity : 1;
        // 侧栏与导航模板共用一个亚克力实例，展开、收起时外观一致。
        var pane = SetAcrylic("SidebarGlassBrush", tint, sidebar * (dark ? .78 : .70), dark ? .80 : .62);
        foreach (string key in new[] { "NavigationViewDefaultPaneBackground", "NavigationViewExpandedPaneBackground" })
            Publish(key, pane);
        SetAcrylic("SheetGlassBrush", tint, current.HasBackground ? (dark ? .70 : .62) : 1, dark ? .88 : .80);
        SetAcrylic("DeckGlassBrush", tint, current.HasBackground ? (dark ? .62 : .55) : 1, dark ? .82 : .74);
    }

    /// <summary>覆盖原生控件已静态解析的强调色别名，使导航、滑块、选择框和进度也随配色更新。</summary>
    private void UpdateControlBrushes()
    {
        foreach (string key in new[]
        {
            "TextControlBorderBrushFocused",
            "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillIndeterminate", "CheckBoxCheckBackgroundStrokeChecked",
            "CheckBoxCheckBackgroundStrokeIndeterminate", "ComboBoxItemPillFillBrush", "ListViewItemCheckBoxSelectedBrush",
            "ListViewItemMultiArrangeOverlayTextBackground", "ListViewItemSelectionIndicatorBrush", "ListViewItemSelectionIndicatorPointerOverBrush",
            "ListViewItemSelectionIndicatorPressedBrush", "NavigationViewSelectionIndicatorForeground", "ProgressBarForeground",
            "ProgressRingForegroundThemeBrush", "RadioButtonOuterEllipseCheckedFill", "RadioButtonOuterEllipseCheckedStroke",
            "SelectorBarItemPillFill", "SliderThumbBackground", "SliderTrackValueFill",
            "ToggleSwitchFillOn", "ToggleSwitchStrokeOn",
        }) Set(key, _brushes["AccentFillColorDefaultBrush"].Color);
        foreach (string key in new[]
        {
            "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillIndeterminatePointerOver", "CheckBoxCheckBackgroundStrokeCheckedPointerOver",
            "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver", "ListViewItemCheckBoxSelectedPointerOverBrush", "RadioButtonOuterEllipseCheckedFillPointerOver",
            "RadioButtonOuterEllipseCheckedStrokePointerOver", "SliderThumbBackgroundPointerOver", "SliderTrackValueFillPointerOver",
            "ToggleSwitchFillOnPointerOver", "ToggleSwitchStrokeOnPointerOver",
        }) Set(key, _brushes["AccentFillColorSecondaryBrush"].Color);
        foreach (string key in new[]
        {
            "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundFillIndeterminatePressed", "CheckBoxCheckBackgroundStrokeCheckedPressed",
            "CheckBoxCheckBackgroundStrokeIndeterminatePressed", "ListViewItemCheckBoxSelectedPressedBrush", "RadioButtonOuterEllipseCheckedFillPressed",
            "RadioButtonOuterEllipseCheckedStrokePressed", "SliderThumbBackgroundPressed", "SliderTrackValueFillPressed",
            "ToggleSwitchFillOnPressed", "ToggleSwitchStrokeOnPressed",
        }) Set(key, _brushes["AccentFillColorTertiaryBrush"].Color);
        foreach (string key in new[]
        {
            "HyperlinkButtonForeground", "HyperlinkForeground",
        }) Set(key, _brushes["AccentTextFillColorPrimaryBrush"].Color);
        foreach (string key in new[]
        {
            "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundIndeterminate",
            "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "CheckBoxCheckGlyphForegroundUnchecked", "CheckBoxCheckGlyphForegroundUncheckedPointerOver",
            "CheckBoxCheckGlyphForegroundUncheckedPressed", "ListViewItemCheckBrush", "ListViewItemDragForeground",
            "ListViewItemMultiArrangeOverlayTextBorder", "RadioButtonCheckGlyphFill", "RadioButtonCheckGlyphFillPointerOver",
            "RadioButtonCheckGlyphFillPressed", "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver",
            "ToggleSwitchKnobFillOnPressed",
        }) Set(key, _brushes["TextOnAccentFillColorPrimaryBrush"].Color);
    }

    /// <summary>计算黑白文字中对比度较高的一种，避免浅色自定义主色上的白字不可读。</summary>
    private static Color Contrast(Color color) => BackgroundContrast.TextColor(0xFF000000u | (uint)color.R << 16 | (uint)color.G << 8 | color.B) == 0xFFFFFFFF
        ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;

    /// <summary>解析已校验的 RGB 字符串为不透明 WinUI 颜色。</summary>
    public static Color Parse(string value)
    {
        uint rgb = ThemeSettingsStore.ParseColor(value);
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>按给定比例混合两种不透明颜色，生成表面色与交互状态色。</summary>
    private static Color Mix(Color a, Color b, double ratio) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * ratio), (byte)(a.G + (b.G - a.G) * ratio), (byte)(a.B + (b.B - a.B) * ratio));

    /// <summary>复用已有画刷对象；首次启动时建立尚不存在的应用资源。</summary>
    private void Set(string key, Color color)
    {
        if (_brushes.TryGetValue(key, out var brush)) brush.Color = color;
        else
        {
            brush = new SolidColorBrush(color);
            _brushes[key] = brush;
        }
        Publish(key, brush);
    }

    /// <summary>原地更新亚克力画刷的色调、透明度与回退色；不透明度为 1 时等同实色表面。</summary>
    private AcrylicBrush SetAcrylic(string key, Color tint, double tintOpacity, double luminosity)
    {
        if (!_acrylics.TryGetValue(key, out var brush))
        {
            brush = new AcrylicBrush();
            _acrylics[key] = brush;
        }
        brush.TintColor = tint;
        brush.TintOpacity = Math.Clamp(tintOpacity, 0, 1);
        brush.TintLuminosityOpacity = Math.Clamp(Math.Max(luminosity, tintOpacity), 0, 1);
        brush.FallbackColor = tint;
        Publish(key, brush);
        return brush;
    }

    /// <summary>把画刷写入应用资源及窗口根资源；根资源优先，保证已打开模板读取到同一实例。</summary>
    private void Publish(string key, Brush brush)
    {
        if (!ReferenceEquals(Application.Current.Resources.TryGetValue(key, out var existing) ? existing : null, brush))
            Application.Current.Resources[key] = brush;
        if (_root is not null) _root.Resources[key] = brush;
    }
}
