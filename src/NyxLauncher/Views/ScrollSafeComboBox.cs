using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace NyxLauncher.Views;

/// <summary>下拉框收起时将滚轮留给外层页面，避免滚动设置时意外改选。</summary>
public sealed class ScrollSafeComboBox : ComboBox
{
    /// <summary>收起时跳过 ComboBox 默认的滚轮选项切换，展开时保留下拉列表的默认滚轮行为。</summary>
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs args)
    {
        if (IsDropDownOpen) base.OnPointerWheelChanged(args);
    }
}
