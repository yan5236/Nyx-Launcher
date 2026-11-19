using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace NyxLauncher.Themes;

/// <summary>全局动效令牌与通用动效行为；时长与缓动集中定义，页面只引用这里的常量，保证节奏一致。</summary>
public static class Motion
{
    /// <summary>按压、悬停等即时反馈。</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(140);
    /// <summary>页面、Tab 内容进入。</summary>
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(280);
    /// <summary>首页控制台等大块面板的进入。</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(460);

    /// <summary>页面级进入位移（像素），从下方滑入。</summary>
    public const double EnterOffset = 16;

    /// <summary>读取系统动画开关；关闭时所有动效直接到达终态。</summary>
    public static bool Enabled => new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    /// <summary>「减速」缓动：起步快、收尾柔和，用于一切进入动效。</summary>
    public static EasingFunctionBase Decelerate() => new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };

    /// <summary>为真时元素按下缩小到 97%，松开以合成线程过渡回弹；适用于主按钮与可点击卡片。</summary>
    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
        "Press", typeof(bool), typeof(Motion), new PropertyMetadata(false, Press_Changed));

    /// <summary>读取元素是否启用按压缩放。</summary>
    public static bool GetPress(DependencyObject element) => (bool)element.GetValue(PressProperty);

    /// <summary>启用或关闭按压缩放。</summary>
    public static void SetPress(DependencyObject element, bool value) => element.SetValue(PressProperty, value);

    /// <summary>挂接或解除指针事件；按钮内部会标记按下事件已处理，因此以 handledEventsToo 订阅。</summary>
    private static void Press_Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if ((bool)args.NewValue)
        {
            element.ScaleTransition = new Vector3Transition { Duration = Fast };
            element.SizeChanged += Press_SizeChanged;
            element.AddHandler(UIElement.PointerPressedEvent, PressedHandler, true);
            element.AddHandler(UIElement.PointerReleasedEvent, ReleasedHandler, true);
            element.AddHandler(UIElement.PointerCanceledEvent, ReleasedHandler, true);
            element.AddHandler(UIElement.PointerCaptureLostEvent, ReleasedHandler, true);
            element.AddHandler(UIElement.PointerExitedEvent, ReleasedHandler, true);
        }
        else
        {
            element.SizeChanged -= Press_SizeChanged;
            element.RemoveHandler(UIElement.PointerPressedEvent, PressedHandler);
            element.RemoveHandler(UIElement.PointerReleasedEvent, ReleasedHandler);
            element.RemoveHandler(UIElement.PointerCanceledEvent, ReleasedHandler);
            element.RemoveHandler(UIElement.PointerCaptureLostEvent, ReleasedHandler);
            element.RemoveHandler(UIElement.PointerExitedEvent, ReleasedHandler);
            element.Scale = Vector3.One;
        }
    }

    private static readonly PointerEventHandler PressedHandler = (sender, _) => SetScale(sender, .97f);
    private static readonly PointerEventHandler ReleasedHandler = (sender, _) => SetScale(sender, 1f);

    /// <summary>缩放以元素中心为原点，尺寸变化时同步，避免按钮向左上角收缩。</summary>
    private static void Press_SizeChanged(object sender, SizeChangedEventArgs args) =>
        ((UIElement)sender).CenterPoint = new Vector3((float)args.NewSize.Width / 2, (float)args.NewSize.Height / 2, 0);

    /// <summary>禁用元素或系统关闭动画时不缩放，保持静止。</summary>
    private static void SetScale(object sender, float scale)
    {
        if (sender is not FrameworkElement element) return;
        if (scale < 1 && (!Enabled || element is Microsoft.UI.Xaml.Controls.Control { IsEnabled: false })) return;
        element.Scale = new Vector3(scale, scale, 1);
    }

    /// <summary>以合成线程过渡让元素从下方淡入；系统关闭动画时直接显示。delay 用于同一批元素的错峰进入。</summary>
    public static void Reveal(UIElement element, double offset, TimeSpan duration, TimeSpan delay = default)
    {
        if (!Enabled)
        {
            element.OpacityTransition = null; element.TranslationTransition = null;
            element.Opacity = 1; element.Translation = Vector3.Zero;
            return;
        }
        // 先在无过渡的情况下放到起点，再启用过渡并设终点，由合成器插值。
        element.OpacityTransition = null; element.TranslationTransition = null;
        element.Opacity = 0; element.Translation = new Vector3(0, (float)offset, 0);
        element.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            element.OpacityTransition = new ScalarTransition { Duration = duration };
            element.TranslationTransition = new Vector3Transition { Duration = duration, Components = Vector3TransitionComponents.Y };
            if (delay > TimeSpan.Zero)
            {
                var timer = element.DispatcherQueue.CreateTimer();
                timer.Interval = delay; timer.IsRepeating = false;
                timer.Tick += (_, _) => { element.Opacity = 1; element.Translation = Vector3.Zero; };
                timer.Start();
            }
            else { element.Opacity = 1; element.Translation = Vector3.Zero; }
        });
    }
}
