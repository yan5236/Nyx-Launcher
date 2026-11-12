using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Themes;

namespace NyxLauncher.Views;

public sealed partial class HomeView
{
    private string? _homeImageKey;
    private int _homeImageGeneration;

    /// <summary>挂载时订阅已有外观服务并应用首页图；不修改外观或游戏配置。</summary>
    private void HomeImage_Loaded(object sender, RoutedEventArgs args)
    {
        ThemeManager.Current.Changed += ApplyHomeImage;
        ApplyHomeImage();
    }

    /// <summary>卸载时解除事件并使在途图片解码失效，避免隐藏控件被长期持有。</summary>
    private void HomeImage_Unloaded(object sender, RoutedEventArgs args)
    {
        ThemeManager.Current.Changed -= ApplyHomeImage;
        _homeImageGeneration++;
        _homeImageKey = null;
    }

    /// <summary>只在首页图片设置变化时更新；移除时同时隐藏暗角与遮罩，版本选择器和启动操作保持可用。</summary>
    private async void ApplyHomeImage()
    {
        var settings = ThemeManager.Current.Store.Current;
        string key = settings.HideHomeImage ? "hidden" : settings.HomeImagePath ?? "default";
        if (key == _homeImageKey) return;
        _homeImageKey = key;
        int generation = ++_homeImageGeneration;
        StageCard.Background = null;
        BottomShade.Visibility = EdgeShade.Visibility = settings.HideHomeImage ? Visibility.Collapsed : Visibility.Visible;
        if (settings.HideHomeImage) { StageImage.ImageSource = null; return; }
        if (settings.HomeImagePath is null)
        {
            StageImage.ImageSource = new BitmapImage(new Uri("ms-appx:///Assets/HomeLandscape.jpg"));
            StageCard.Background = StageImage;
            return;
        }
        try
        {
            using var input = File.OpenRead(settings.HomeImagePath);
            if (input.Length is 0 or > 32 * 1024 * 1024) throw new IOException("首页图片须小于 32 MiB。");
            using var stream = new MemoryStream();
            await input.CopyToAsync(stream);
            stream.Position = 0;
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            if (decoder.PixelWidth is 0 or > 8192 || decoder.PixelHeight is 0 or > 8192 || (long)decoder.PixelWidth * decoder.PixelHeight > 32_000_000)
                throw new IOException("首页图片尺寸超出限制。");
            stream.Position = 0;
            var bitmap = new BitmapImage { DecodePixelWidth = (int)Math.Min(2048u, decoder.PixelWidth) };
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            if (generation != _homeImageGeneration) return;
            StageImage.ImageSource = bitmap;
            StageCard.Background = StageImage;
        }
        catch (Exception ex)
        {
            if (generation != _homeImageGeneration) return;
            BottomShade.Visibility = EdgeShade.Visibility = Visibility.Collapsed;
            Status.Message = "无法加载首页图片：" + ex.Message;
            Status.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
            Status.IsOpen = true;
        }
    }
}
