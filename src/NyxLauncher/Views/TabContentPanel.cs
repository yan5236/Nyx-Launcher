using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace NyxLauncher.Views;

/// <summary>承载 Tab 正文并统一淡入、上移转场；标签栏与提示由父视图放在容器之外。</summary>
public sealed class TabContentPanel : Grid
{
    private readonly Storyboard _entrance = new() { FillBehavior = FillBehavior.Stop };

    /// <summary>为专用内容层建立 220 毫秒缓出动画；动画结束或卸载后使用默认透明度与零位移，不持有业务页面。</summary>
    public TabContentPanel()
    {
        var offset = new TranslateTransform();
        RenderTransform = offset;
        var opacity = new DoubleAnimation
        {
            From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var movement = new DoubleAnimation
        {
            From = 16, To = 0, Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacity, this);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(movement, offset);
        Storyboard.SetTargetProperty(movement, "Y");
        _entrance.Children.Add(opacity);
        _entrance.Children.Add(movement);
        Unloaded += Panel_Unloaded;
    }

    /// <summary>正文完成切换后调用；打断旧动画并立即播放最新内容，首次加载和系统禁用动画时直接显示。</summary>
    public void PlayTransition()
    {
        _entrance.Stop();
        if (IsLoaded && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) _entrance.Begin();
    }

    /// <summary>容器卸载时停止动画，避免重新挂载后残留透明度或位移。</summary>
    private void Panel_Unloaded(object sender, RoutedEventArgs e) => _entrance.Stop();
}
