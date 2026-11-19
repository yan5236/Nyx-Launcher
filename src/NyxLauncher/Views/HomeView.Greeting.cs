using Microsoft.UI.Xaml;
using NyxLauncher.Themes;

namespace NyxLauncher.Views;

/// <summary>首页的时段问候与进入动效；只改变展示文字和视觉状态，不触碰版本选择或启动会话。</summary>
public sealed partial class HomeView
{
    private string? _greetingKey;

    /// <summary>挂载时刷新问候语，并以错峰方式让问候语与控制台从下方浮现。</summary>
    private void Greeting_Loaded(object sender, RoutedEventArgs args)
    {
        UpdateGreeting();
        PlayEntrance();
    }

    /// <summary>每次回到首页时调用：时段变化才更新文字，保证问候与背景场景一致。</summary>
    private void UpdateGreeting()
    {
        var (key, title, hint) = DateTime.Now.Hour switch
        {
            >= 5 and < 9 => ("morning", "早上好", "新的一天，去看看日出吧"),
            >= 9 and < 12 => ("forenoon", "上午好", "选择版本，开始冒险"),
            >= 12 and < 14 => ("noon", "中午好", "阳光正好，适合建造"),
            >= 14 and < 18 => ("afternoon", "下午好", "选择版本，开始冒险"),
            >= 18 and < 23 => ("evening", "晚上好", "夜幕降临，小心怪物"),
            _ => ("night", "夜深了", "注意休息，明天再继续"),
        };
        if (key == _greetingKey) return;
        _greetingKey = key;
        GreetingTitle.Text = title;
        GreetingHint.Text = hint;
    }

    /// <summary>问候语先出现，控制台延迟 70 毫秒跟上，形成由上至下的层次；系统关闭动画时直接显示。</summary>
    internal void PlayEntrance()
    {
        Motion.Reveal(Greeting, 18, Motion.Slow);
        Motion.Reveal(Deck, 24, Motion.Slow, TimeSpan.FromMilliseconds(70));
    }
}
