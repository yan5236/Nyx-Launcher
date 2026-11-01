using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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

    /// <summary>初始化外观控件并在加载期间订阅错误通知，卸载时解除订阅。</summary>
    public AppearanceSettingsView()
    {
        InitializeComponent();
        Refresh();
        Loaded += ViewLoaded;
        Unloaded += ViewUnloaded;
    }

    /// <summary>加载后监听背景解码错误，并展示当前配置读取问题。</summary>
    private void ViewLoaded(object sender, RoutedEventArgs args) { _themes.Changed += ThemeChanged; ThemeChanged(); }

    /// <summary>移除事件订阅，避免已经卸载的控件被主题服务持有。</summary>
    private void ViewUnloaded(object sender, RoutedEventArgs args) => _themes.Changed -= ThemeChanged;

    /// <summary>展示当前外观读取或背景加载错误，不覆盖编辑中的颜色。</summary>
    private void ThemeChanged() => ShowError(_themes.Store.LoadError ?? _themes.BackgroundError);

    /// <summary>从已保存值刷新控件并抑制初始化事件，保存失败时用于回退。</summary>
    private void Refresh()
    {
        _loading = true;
        var settings = _themes.Store.Current;
        Mode.SelectedIndex = (int)settings.Mode;
        Preset.SelectedIndex = (int)settings.Preset;
        CustomPanel.Visibility = settings.Preset == ThemePreset.Custom ? Visibility.Visible : Visibility.Collapsed;
        OpacitySlider.Value = settings.BackgroundOpacity * 100;
        SidebarOpacitySlider.Value = settings.SidebarOpacity * 100;
        RemoveImage.IsEnabled = settings.BackgroundPath is not null;
        OpacitySlider.IsEnabled = settings.BackgroundPath is not null;
        LoadColors();
        _loading = false;
    }

    /// <summary>切换外观模式或配色预设并立即保存，不改变自定义的两组颜色。</summary>
    private void Options_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_loading) return;
        Save(_themes.Store.Current with { Mode = (AppearanceMode)Mode.SelectedIndex, Preset = (ThemePreset)Preset.SelectedIndex });
    }

    /// <summary>切换要编辑的浅色或深色配色，不改变实际外观模式。</summary>
    private void ColorMode_Changed(object sender, SelectionChangedEventArgs args) { if (!_loading) LoadColors(); }

    /// <summary>填充选定明暗配置的三个颜色选择器，未应用的修改在切换组别时丢弃。</summary>
    private void LoadColors()
    {
        var colors = ColorMode.SelectedIndex == 1 ? _themes.Store.Current.Dark : _themes.Store.Current.Light;
        Tone.Color = ThemeManager.Parse(colors.Tone);
        Primary.Color = ThemeManager.Parse(colors.Primary);
        Secondary.Color = ThemeManager.Parse(colors.Secondary);
    }

    /// <summary>保存当前明暗组的色调、主色和副色，另一组保持不变。</summary>
    private void ApplyColors_Click(object sender, RoutedEventArgs args)
    {
        var colors = new ThemeColors(Hex(Tone.Color), Hex(Primary.Color), Hex(Secondary.Color));
        Save(ColorMode.SelectedIndex == 1 ? _themes.Store.Current with { Dark = colors } : _themes.Store.Current with { Light = colors });
    }

    /// <summary>转换颜色为不透明 RGB 配置字符串，忽略选择器的透明分量。</summary>
    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>选择并验证图片，将独立副本写入配置目录后保存路径；取消、解码或保存失败保留原背景。</summary>
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
            var oldPath = _themes.Store.Current.BackgroundPath;
            if (Save(_themes.Store.Current with { BackgroundPath = destination }))
            {
                destination = null;
                DeleteOwnedImage(oldPath);
            }
        }
        catch (Exception ex) { ShowError("未更换背景图片：" + ex.Message); }
        finally { DeleteOwnedImage(destination); ChooseImage.IsEnabled = true; }
    }

    /// <summary>清空背景引用并保存，成功后只清理本应用拥有的旧副本。</summary>
    private void RemoveImage_Click(object sender, RoutedEventArgs args)
    {
        var path = _themes.Store.Current.BackgroundPath;
        if (Save(_themes.Store.Current with { BackgroundPath = null })) DeleteOwnedImage(path);
    }

    /// <summary>改变图片本身的不透明度，不改变文字和控件透明度。</summary>
    private void Opacity_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_loading) Save(_themes.Store.Current with { BackgroundOpacity = OpacitySlider.Value / 100 });
    }

    /// <summary>单独保存侧栏主题底色的遮罩不透明度，不影响页面图片和导航内容。</summary>
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
