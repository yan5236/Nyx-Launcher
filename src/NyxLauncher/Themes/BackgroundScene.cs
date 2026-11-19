using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NyxLauncher.Services;

namespace NyxLauncher.Themes;

/// <summary>窗口背景层的切换动效与时段轮换：新图缓慢推近淡入，旧图在上层淡出；只操作背景图片，不触碰业务页面。</summary>
internal sealed class BackgroundScene
{
    private readonly Image _image;
    private readonly Image? _veil;
    private readonly ScaleTransform _zoom = new() { ScaleX = 1, ScaleY = 1 };
    private readonly DispatcherQueueTimer _clock;
    private readonly Action _settled;
    private Storyboard? _transition;
    private string _scene = TimeOfDayScene.Resolve(DateTime.Now);

    /// <summary>本地时段切换到新场景时触发，由外观服务重新解析背景路径。</summary>
    public event Action? SceneDue;

    /// <summary>在背景图片上方插入同尺寸的过渡层，并建立每分钟一次的时段检查；背景不在面板中时只做无动画替换。</summary>
    public BackgroundScene(FrameworkElement root, Image image, Action settled)
    {
        _image = image;
        _settled = settled;
        image.RenderTransformOrigin = new Windows.Foundation.Point(.5, .5);
        image.RenderTransform = _zoom;
        if (image.Parent is Panel host)
        {
            _veil = new Image { Stretch = image.Stretch, IsHitTestVisible = false, Opacity = 0 };
            Grid.SetRowSpan(_veil, Grid.GetRowSpan(image));
            Grid.SetColumnSpan(_veil, Grid.GetColumnSpan(image));
            host.Children.Insert(host.Children.IndexOf(image) + 1, _veil);
        }
        _clock = root.DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromMinutes(1);
        _clock.IsRepeating = true;
        _clock.Tick += Clock_Tick;
        root.Unloaded += (_, _) => _clock.Stop();
    }

    /// <summary>仅内置场景需要随时段轮换；自选图片或移除背景时停止计时，避免无意义唤醒。</summary>
    public void SetFollowing(bool following)
    {
        if (following && !_clock.IsRunning) { _scene = TimeOfDayScene.Resolve(DateTime.Now); _clock.Start(); }
        else if (!following && _clock.IsRunning) _clock.Stop();
    }

    /// <summary>每分钟比较一次所在时段，跨过时段边界时通知外观服务载入下一张场景。</summary>
    private void Clock_Tick(DispatcherQueueTimer sender, object args)
    {
        string scene = TimeOfDayScene.Resolve(DateTime.Now);
        if (scene == _scene) return;
        _scene = scene;
        SceneDue?.Invoke();
    }

    /// <summary>替换背景图片：旧图复制到上层并淡出，新图从 104% 缓慢推近至 100%；系统关闭动画时直接替换。</summary>
    public void Swap(ImageSource? source)
    {
        _transition?.Stop();
        var previous = _image.Source;
        _image.Source = source;
        if (_veil is null || !AnimationsEnabled())
        {
            if (_veil is not null) { _veil.Source = null; _veil.Opacity = 0; }
            _zoom.ScaleX = _zoom.ScaleY = 1;
            _settled();
            return;
        }
        var story = new Storyboard();
        if (previous is not null)
        {
            // 旧图以当前背景不透明度覆盖在新图上方，再淡出，形成交叉溶解。
            _veil.Source = previous;
            _veil.Opacity = 0;
            story.Children.Add(Animate(_veil, "Opacity", _image.Opacity, 0, 900, new SineEase { EasingMode = EasingMode.EaseInOut }));
        }
        else if (source is not null)
        {
            // 首次出现：背景从透明淡入，配合推近形成「场景苏醒」的开场。
            story.Children.Add(Animate(_image, "Opacity", 0, _image.Opacity, 700, new CubicEase { EasingMode = EasingMode.EaseOut }));
        }
        if (source is not null)
        {
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 };
            story.Children.Add(Animate(_zoom, "ScaleX", 1.04, 1, 1600, ease));
            story.Children.Add(Animate(_zoom, "ScaleY", 1.04, 1, 1600, ease));
        }
        story.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_transition, story)) return;
            _veil.Source = null;
            _settled();
        };
        _transition = story;
        story.Begin();
    }

    /// <summary>建立结束后回落到本地值的双精度动画，避免长时间持有覆盖值干扰不透明度设置。</summary>
    private static DoubleAnimation Animate(DependencyObject target, string property, double from, double to, int milliseconds, EasingFunctionBase ease)
    {
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = ease, FillBehavior = FillBehavior.Stop
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    /// <summary>读取系统动画开关，不增加独立设置项。</summary>
    private static bool AnimationsEnabled() => new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
}
