using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace NyxLauncher.Views;

/// <summary>下拉选择器：收起时把滚轮留给外层页面，避免滚动页面时误改选项。</summary>
public sealed class ScrollSafeComboBox : ComboBox
{
    /// <summary>
    /// 收起时不调用基类处理。ComboBox 默认会在滚轮时按方向改选（历史上多个 WinUI 版本如此），
    /// 跳过基类后事件继续冒泡，外层 ScrollViewer 仍按滚轮滚动页面；展开时保留下拉列表的默认滚轮行为。
    /// </summary>
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs args)
    {
        if (IsDropDownOpen) base.OnPointerWheelChanged(args);
    }
}
