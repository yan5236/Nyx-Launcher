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
    private FrameworkElement? _root;
    private Image? _background;
    private string? _loadedPath;
    private int _imageGeneration;
    private BackgroundTextContrast? _contrast;
    private readonly Dictionary<string, SolidColorBrush> _brushes = new();
    public string? BackgroundError { get; private set; }
    public event Action? Changed;

    /// <summary>初始化全部共享画刷；必须在创建窗口和页面前调用。</summary>
    public void Initialize() => UpdateBrushes(false);

    /// <summary>绑定窗口根节点和背景层，监听系统明暗变化；窗口生命周期内仅调用一次。</summary>
    public void Attach(FrameworkElement root, Image background, Microsoft.UI.Windowing.AppWindow? window = null)
    {
        _root = root;
        _background = background;
        _contrast = new BackgroundTextContrast(root, background, window);
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

    /// <summary>成功保存后立即应用外观；失败返回错误并保留当前界面。</summary>
    public string? Save(ThemeSettings settings)
    {
        var previous = Store.Current;
        var error = Store.Save(settings);
        if (error is null) Apply(previous.Preset != settings.Preset || previous.Light != settings.Light || previous.Dark != settings.Dark);
        return error;
    }

    /// <summary>应用明暗模式与背景透明度；配色变化时刷新原生模板缓存，只有图片路径改变时才重新解码图片。</summary>
    private void Apply(bool refreshPalette = false)
    {
        if (_root is null || _background is null) return;
        _contrast?.Restore();
        if (refreshPalette) _root.RequestedTheme = _root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        _root.RequestedTheme = Store.Current.Mode switch { AppearanceMode.Light => ElementTheme.Light, AppearanceMode.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        UpdateBrushes(_root.ActualTheme == ElementTheme.Dark);
        var path = Store.Current.BackgroundPath;
        if (path != _loadedPath)
        {
            _loadedPath = path;
            BackgroundError = null;
            _background.Source = null;
            _contrast?.SetImage(null);
            _ = LoadBackgroundAsync(path, ++_imageGeneration);
        }
        _background.Opacity = Store.Current.BackgroundOpacity;
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
            stream.Position = 0;
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            if (decoder.PixelWidth is 0 or > 8192 || decoder.PixelHeight is 0 or > 8192 || (long)decoder.PixelWidth * decoder.PixelHeight > 32_000_000)
                throw new IOException("背景图片尺寸超出限制。 ");
            double scale = Math.Min(1, 1024d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            uint width = Math.Max(1, (uint)(decoder.PixelWidth * scale)), height = Math.Max(1, (uint)(decoder.PixelHeight * scale));
            var pixels = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Straight, new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = width, ScaledHeight = height },
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);
            bool rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
            var sample = new BackgroundContrast((int)(rotated ? height : width), (int)(rotated ? width : height), pixels.DetachPixelData());
            stream.Position = 0;
            var bitmap = new BitmapImage { DecodePixelWidth = (int)Math.Min(2048u, decoder.PixelWidth) };
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            if (generation == _imageGeneration && _background is not null)
            {
                _background.Source = bitmap;
                _contrast?.SetImage(sample);
            }
        }
        catch (Exception ex)
        {
            if (generation != _imageGeneration) return;
            BackgroundError = "无法加载背景图片：" + ex.Message;
            Changed?.Invoke();
        }
    }

    /// <summary>按三种主题色生成表面与交互画刷，原地更新以覆盖已打开的模板和弹窗。</summary>
    private void UpdateBrushes(bool dark)
    {
        var colors = Store.Current.Colors(dark);
        var tone = Parse(colors.Tone); var primary = Parse(colors.Primary); var secondary = Parse(colors.Secondary);
        Set("ShellBrush", Mix(dark ? Parse("#181818") : Parse("#F8F8F8"), tone, dark ? .12 : .06));
        foreach (string key in new[] { "NavigationViewDefaultPaneBackground", "NavigationViewExpandedPaneBackground" })
        {
            Set(key, _brushes["ShellBrush"].Color);
            _brushes[key].Opacity = Store.Current.SidebarOpacity;
        }
        Set("PageBrush", dark ? Parse("#202020") : Parse("#FAFBF8"));
        Set("CardBrush", dark ? Mix(Parse("#292929"), tone, .06) : Parse("#FFFFFF"));
        Set("MenuSurfaceBrush", _brushes["CardBrush"].Color);
        Set("CardHoverBrush", Mix(_brushes["CardBrush"].Color, tone, .06));
        Set("CardPressedBrush", Mix(_brushes["CardBrush"].Color, tone, .12));
        foreach (string key in new[] { "CardBrush", "CardHoverBrush", "CardPressedBrush" })
            _brushes[key].Opacity = Store.Current.BackgroundPath is null ? 1 : .88;
        Set("LineBrush", dark ? Parse("#454545") : Parse("#E3E7DF"));
        Set("TextBrush", dark ? Parse("#F3F3F3") : Parse("#263B30"));
        Set("MutedBrush", dark ? Parse("#B9C3BC") : Parse("#6B7A70"));
        Set("TintBrush", Mix(dark ? Parse("#242424") : Parse("#FFFFFF"), tone, dark ? .30 : .16));
        Set("SecondaryBrush", secondary);
        Set("DangerBrush", dark ? Parse("#FF99A4") : Parse("#A4262C"));
        Set("DangerLineBrush", dark ? Parse("#9C5559") : Parse("#E3B4B1"));
        Set("SystemControlHighlightAccentBrush", primary);
        var foreground = Store.Current.Preset == ThemePreset.VuiBlue && !dark ? Parse("#FFFFFF") : Contrast(primary);
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
            Application.Current.Resources[key] = brush;
        }
        if (_root is not null) _root.Resources[key] = brush;
    }
}
