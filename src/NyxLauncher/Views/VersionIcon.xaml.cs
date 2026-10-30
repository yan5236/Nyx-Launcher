using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>在详情、版本列表和首页复用版本图标；图像缺失或损坏时使用默认图标，不持有磁盘文件句柄。</summary>
public sealed partial class VersionIcon : UserControl
{
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version),
        typeof(InstalledVersion), typeof(VersionIcon), new PropertyMetadata(null, VersionChanged));
    public InstalledVersion? Version { get => (InstalledVersion?)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    private int _generation;

    /// <summary>初始化默认游戏图标；绑定实际版本后再读取自定义图片。</summary>
    public VersionIcon() => InitializeComponent();

    /// <summary>响应绑定版本变化并重新载入图片，读取错误仅回退默认图标。</summary>
    private static void VersionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((VersionIcon)sender).Reload();

    /// <summary>有界读取自定义 PNG 并以流解码，避免 URI 缓存及文件锁；版本切换后丢弃迟到图像，损坏图像保留默认显示。</summary>
    public async void Reload()
    {
        int generation = ++_generation;
        CustomIcon.Source = null;
        DefaultIcon.Visibility = Visibility.Visible;
        try
        {
            if (Version is null) return;
            string path = Path.Combine(Version.DirectoryPath, VersionManagementService.IconFileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) return;
            byte[] bytes = await File.ReadAllBytesAsync(path);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage { DecodePixelWidth = 128 };
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            if (generation == _generation)
            { CustomIcon.Source = image; DefaultIcon.Visibility = Visibility.Collapsed; }
        }
        catch (Exception) { /* 图标为可选展示内容，不阻断版本管理和启动。 */ }
    }
}
