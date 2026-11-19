using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using NyxLauncher.Services;
using NyxLauncher.Themes;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace NyxLauncher.Views;

/// <summary>独立编辑外观配置，不依赖游戏下载设置或重新创建页面。</summary>
public sealed partial class AppearanceSettingsView : UserControl
{
    private bool _loading = true;
    private readonly ThemeManager _themes = ThemeManager.Current;

    /// <summary>使用应用共享的外观服务创建控件，加载期间抑制初始化事件；卸载时解除订阅。</summary>
    public AppearanceSettingsView()
    {
        InitializeComponent();
        Refresh();
        Loaded += ViewLoaded;
        Unloaded += ViewUnloaded;
    }

    /// <summary>加载后监听背景解码错误；重新加载时恢复从保存值刷新，并展示当前配置读取问题。</summary>
    private void ViewLoaded(object sender, RoutedEventArgs args) { if (_loading) Refresh(); _themes.Changed += ThemeChanged; ThemeChanged(); }

    /// <summary>移除事件订阅，避免已经卸载的控件被主题服务持有；未关闭取色器时补交已选颜色。</summary>
    private void ViewUnloaded(object sender, RoutedEventArgs args)
    {
        _themes.Changed -= ThemeChanged;
        if (_themes.HasPreview && CustomPanel is not null) Save(Draft());
        _loading = true;
    }

    /// <summary>展示当前外观读取或背景加载错误，不覆盖编辑中的颜色。</summary>
    private void ThemeChanged() => ShowError(_themes.Store.LoadError ?? _themes.BackgroundError);

    /// <summary>从保存值刷新外观与背景图片按钮并抑制初始化事件；只有自定义配色才创建颜色选择器。</summary>
    private void Refresh()
    {
        _loading = true;
        var settings = _themes.Store.Current;
        Mode.SelectedIndex = (int)settings.Mode;
        Preset.SelectedIndex = (int)settings.Preset;
        if (settings.Preset == ThemePreset.Custom)
        {
            var panel = (StackPanel)FindName(nameof(CustomPanel));
            panel.Visibility = Visibility.Visible;
        }
        else if (CustomPanel is not null) UnloadObject(CustomPanel);
        OpacitySlider.Value = settings.BackgroundOpacity * 100;
        SidebarOpacitySlider.Value = settings.SidebarOpacity * 100;
        RemoveImage.IsEnabled = settings.HasBackground;
        RestoreImage.IsEnabled = settings.HideBackground || settings.BackgroundPath is not null;
        OpacitySlider.IsEnabled = settings.HasBackground;
        LoadColors();
        _loading = false;
    }

    /// <summary>切换外观模式或配色预设并立即保存，不改变自定义的两组颜色。</summary>
    private void Options_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_loading) return;
        Save(_themes.Store.Current with { Mode = (AppearanceMode)Mode.SelectedIndex, Preset = (ThemePreset)Preset.SelectedIndex });
    }

    /// <summary>切换要编辑的浅色或深色配色，不改变实际外观模式，也不立即改写共享画刷。</summary>
    private void ColorMode_Changed(object sender, SelectionChangedEventArgs args) { if (!_loading) LoadColors(); }

    /// <summary>仅填充已经创建的颜色行，未提交的修改在切换组别时丢弃，不为预设配色创建编辑器。</summary>
    private void LoadColors()
    {
        if (CustomPanel is null) return;
        bool previous = _loading;
        _loading = true;
        try
        {
            var colors = ColorMode.SelectedIndex == 1 ? _themes.Store.Current.Dark : _themes.Store.Current.Light;
            ApplyColor(TonePicker, ToneSwatch, colors.Tone);
            ApplyColor(PrimaryPicker, PrimarySwatch, colors.Primary);
            ApplyColor(SecondaryPicker, SecondarySwatch, colors.Secondary);
        }
        finally { _loading = previous; }
    }

    /// <summary>把配置中的不透明 RGB 同步到取色器、右侧色块与读屏提示。</summary>
    private void ApplyColor(ColorPicker picker, Border swatch, string value)
    {
        var color = ThemeManager.Parse(value);
        picker.Color = color;
        swatch.Background = new SolidColorBrush(color);
        if (RowFor(picker) is { } row) AutomationProperties.SetHelpText(row, "打开取色器，当前 " + value);
    }

    /// <summary>取色器变化时立即刷新色块与共享画刷，只预览、不写盘，关闭取色器后再保存。</summary>
    private void Picker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading) return;
        if (SwatchFor(sender) is { } swatch) swatch.Background = new SolidColorBrush(sender.Color);
        if (RowFor(sender) is { } row) AutomationProperties.SetHelpText(row, "打开取色器，当前 " + Hex(sender.Color));
        _themes.Preview(Draft());
    }

    /// <summary>关闭取色器后保存当前明暗组的色调、主色和副色；没有改动时不重写配置。</summary>
    private void Picker_Closed(object sender, object args)
    {
        if (_loading || CustomPanel is null) return;
        var draft = Draft();
        if (!_themes.HasPreview && draft == _themes.Store.Current) return;
        Save(draft);
    }

    /// <summary>组合三行取色器的当前颜色，只替换正在编辑的明暗组。</summary>
    private ThemeSettings Draft()
    {
        var colors = new ThemeColors(Hex(TonePicker.Color), Hex(PrimaryPicker.Color), Hex(SecondaryPicker.Color));
        return ColorMode.SelectedIndex == 1 ? _themes.Store.Current with { Dark = colors } : _themes.Store.Current with { Light = colors };
    }

    /// <summary>按取色器找到对应行的右侧色块。</summary>
    private Border? SwatchFor(ColorPicker picker) =>
        ReferenceEquals(picker, TonePicker) ? ToneSwatch :
        ReferenceEquals(picker, PrimaryPicker) ? PrimarySwatch :
        ReferenceEquals(picker, SecondaryPicker) ? SecondarySwatch : null;

    /// <summary>按取色器找到对应整行按钮，用于同步读屏提示。</summary>
    private Button? RowFor(ColorPicker picker) =>
        ReferenceEquals(picker, TonePicker) ? ToneRow :
        ReferenceEquals(picker, PrimaryPicker) ? PrimaryRow :
        ReferenceEquals(picker, SecondaryPicker) ? SecondaryRow : null;

    /// <summary>转换颜色为不透明 RGB 配置字符串，忽略选择器的透明分量。</summary>
    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>选择并验证窗口背景图片，保存独立副本后替换时段场景；取消或失败保留原背景。</summary>
    private async void ChooseImage_Click(object sender, RoutedEventArgs args)
    {
        ChooseImage.IsEnabled = false;
        string? destination = null;
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(extension);
            var selected = await picker.PickSingleFileAsync();
            if (selected is null) return;
            byte[] bytes;
            using (var input = File.OpenRead(selected.Path))
            {
                if (input.Length is 0 or > 32 * 1024 * 1024) throw new IOException("图片文件须小于 32 MiB。");
                var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
                if (decoder.PixelWidth is 0 or > 8192 || decoder.PixelHeight is 0 or > 8192 || (long)decoder.PixelWidth * decoder.PixelHeight > 32_000_000)
                    throw new IOException("图片尺寸不能超过 8192 像素，且总像素不能超过 3200 万。");
                using var bitmap = await decoder.GetSoftwareBitmapAsync();
                input.Position = 0;
                using var buffer = new MemoryStream();
                await input.CopyToAsync(buffer);
                bytes = buffer.ToArray();
            }
            Directory.CreateDirectory(_themes.Store.DirectoryPath);
            destination = Path.Combine(_themes.Store.DirectoryPath, "background-" + Guid.NewGuid().ToString("N") + Path.GetExtension(selected.Path));
            await File.WriteAllBytesAsync(destination, bytes);
            var current = _themes.Store.Current;
            var oldPath = current.BackgroundPath;
            if (Save(current with { BackgroundPath = destination, HideBackground = false }))
            {
                destination = null;
                DeleteOwnedImage(oldPath);
            }
        }
        catch (Exception ex) { ShowError("未更换背景图片：" + ex.Message); }
        finally { DeleteOwnedImage(destination); ChooseImage.IsEnabled = true; }
    }

    /// <summary>移除背景（包括内置场景），窗口只保留主题底色；成功后只清理本应用拥有的旧副本。</summary>
    private void RemoveImage_Click(object sender, RoutedEventArgs args)
    {
        var path = _themes.Store.Current.BackgroundPath;
        if (Save(_themes.Store.Current with { BackgroundPath = null, HideBackground = true })) DeleteOwnedImage(path);
    }

    /// <summary>恢复随本地时段变化的内置场景，保存成功后清理先前自定义副本。</summary>
    private void RestoreImage_Click(object sender, RoutedEventArgs args)
    {
        var path = _themes.Store.Current.BackgroundPath;
        if (Save(_themes.Store.Current with { BackgroundPath = null, HideBackground = false })) DeleteOwnedImage(path);
    }

    /// <summary>改变图片本身的不透明度，不改变文字和控件透明度。</summary>
    private void Opacity_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_loading) Save(_themes.Store.Current with { BackgroundOpacity = OpacitySlider.Value / 100 });
    }

    /// <summary>单独保存玻璃面板（侧栏）的主题底色不透明度，不影响背景图片和导航内容。</summary>
    private void SidebarOpacity_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_loading) Save(_themes.Store.Current with { SidebarOpacity = SidebarOpacitySlider.Value / 100 });
    }

    /// <summary>只删除配置目录中名称为应用生成 GUID 的背景副本；清理失败不影响已保存设置。</summary>
    private void DeleteOwnedImage(string? path)
    {
        if (path is null) return;
        try
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (Path.GetDirectoryName(Path.GetFullPath(path)) == _themes.Store.DirectoryPath && name.StartsWith("background-") && Guid.TryParseExact(name[11..], "N", out _)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    /// <summary>提交设置并恢复到实际保存值，返回是否成功，失败时展示错误。</summary>
    private bool Save(ThemeSettings settings)
    {
        string? error = _themes.Save(settings);
        Refresh();
        ShowError(error ?? _themes.BackgroundError);
        return error is null;
    }

    /// <summary>仅在失败时显示必要错误信息，不增加常驻说明文案。</summary>
    private void ShowError(string? message) { Error.Message = message ?? ""; Error.IsOpen = message is not null; }
}
