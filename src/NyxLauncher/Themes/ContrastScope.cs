using Microsoft.UI.Xaml;

namespace NyxLauncher.Themes;

/// <summary>标记自带底色或自管文字颜色的子树；背景取样器跳过其中的文字，不再按背景图片改写黑白前景。</summary>
public static class ContrastScope
{
    /// <summary>为真时该元素及其后代的前景由自身主题决定，例如玻璃面板、首页控制台。</summary>
    public static readonly DependencyProperty IsExcludedProperty = DependencyProperty.RegisterAttached(
        "IsExcluded", typeof(bool), typeof(ContrastScope), new PropertyMetadata(false));

    /// <summary>读取元素是否被排除在背景文字自适应之外。</summary>
    public static bool GetIsExcluded(DependencyObject element) => (bool)element.GetValue(IsExcludedProperty);

    /// <summary>设置元素是否被排除在背景文字自适应之外；切换后由取样器在下一次布局时恢复或重新计算。</summary>
    public static void SetIsExcluded(DependencyObject element, bool value) => element.SetValue(IsExcludedProperty, value);
}
