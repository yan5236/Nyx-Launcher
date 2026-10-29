using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>使用同一个 WinUI 对话框选择类型、输入名称和显示保存错误。</summary>
public sealed partial class AddAccountDialog : ContentDialog
{
    private readonly AccountStore _accounts;
    private Storyboard? _transition;
    /// <summary>注入账户存储；初始只展示三种类型，未接入的类型不接受点击。</summary>
    public AddAccountDialog(AccountStore accounts)
    {
        InitializeComponent(); _accounts = accounts;
        Closed += (_, _) => _transition?.Stop();
    }

    /// <summary>进入离线输入步骤并移动键盘焦点，允许 Enter 确认和返回类型选择。</summary>
    private void Offline_Click(object sender, RoutedEventArgs e)
    {
        DialogTitle.Text = "添加离线账户"; Types.Visibility = Visibility.Collapsed; AccountName.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        SecondaryButtonText = "确定"; DefaultButton = ContentDialogButton.Secondary;
        AnimateStep(true);
        AccountName.Focus(FocusState.Programmatic);
    }

    /// <summary>返回类型列表而不关闭弹窗，保留已输入名称但清除旧错误。</summary>
    private void Back_Click(object sender, RoutedEventArgs args)
    {
        DialogTitle.Text = "添加账户"; Types.Visibility = Visibility.Visible; AccountName.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        SecondaryButtonText = ""; DefaultButton = ContentDialogButton.None; Error.IsOpen = false;
        AnimateStep(false);
        OfflineButton.Focus(FocusState.Programmatic);
    }

    /// <summary>新步骤按进入或返回方向平移并淡入，复用下载页 220 毫秒节奏；尊重系统动画开关，快速往返先停止上次动画。</summary>
    private void AnimateStep(bool entering)
    {
        _transition?.Stop();
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var opacity = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220) };
        var translation = new DoubleAnimation
        {
            From = entering ? 24 : -24, To = 0, Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacity, StepContent); Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(translation, StepTranslation); Storyboard.SetTargetProperty(translation, "X");
        _transition = new Storyboard();
        _transition.Children.Add(opacity); _transition.Children.Add(translation);
        _transition.Begin();
    }

    /// <summary>只有账户成功写入后才关闭；名称或 I/O 错误留在当前输入步骤。</summary>
    private void Confirm_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try { _accounts.Add(AccountName.Text); }
        catch (Exception ex) { args.Cancel = true; Error.Message = ex.Message; Error.IsOpen = true; }
    }
}
