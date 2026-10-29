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
    private readonly GameAccount? _previous;
    private CancellationTokenSource? _loginCancellation;
    private Task? _loginTask;
    private bool _closed;

    /// <summary>注入账户存储和可选重登身份；显示测试配置提示，配置失败仅禁用微软入口，关闭会取消登录。</summary>
    public AddAccountDialog(AccountStore accounts, GameAccount? previous = null)
    {
        InitializeComponent(); _accounts = accounts; _previous = previous;
        try { MicrosoftHint.Text = MicrosoftLoginSettings.Load().IsTest ? "测试应用 · 仅供开发验证" : "在系统浏览器中登录"; }
        catch (Exception ex) { MicrosoftButton.IsEnabled = false; Error.Message = ex.Message; Error.IsOpen = true; }
        Closed += (_, _) => { _closed = true; _transition?.Stop(); _loginCancellation?.Cancel(); };
        if (previous is not null)
        {
            OfflineButton.IsEnabled = false;
            Loaded += (_, _) => { if (MicrosoftButton.IsEnabled && _loginTask is null) _loginTask = LoginAsync(); };
        }
    }

    /// <summary>发起一次系统浏览器登录；任务保留供窗口关闭等待，重复点击不会创建竞争的本机回调监听。</summary>
    private void Microsoft_Click(object sender, RoutedEventArgs e)
    { if (_loginCancellation is null) _loginTask = LoginAsync(); }

    /// <summary>展示认证阶段并在完成全部认证后保存账户；取消、错误及关闭均保留原账户且释放回调监听。</summary>
    private async Task LoginAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _loginCancellation = cancellation;
        Types.Visibility = Visibility.Collapsed; LoginProgress.Visibility = Visibility.Visible; LoginRing.IsActive = true;
        Error.IsOpen = false; DialogTitle.Text = _previous is null ? "微软登录" : "重新登录微软账户";
        var progress = new Progress<string>(text => { if (!_closed && _loginCancellation == cancellation) LoginStatus.Text = text; });
        try
        {
            var result = await MicrosoftLoginService.AuthenticateAsync(_previous, true, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            _accounts.SaveMicrosoft(result.Account, _previous);
            Hide();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        { if (!_closed) { Error.Message = ex.Message; Error.IsOpen = true; } }
        finally
        {
            _loginCancellation = null;
            if (!_closed)
            {
                LoginRing.IsActive = false; LoginProgress.Visibility = Visibility.Collapsed; Types.Visibility = Visibility.Visible;
                DialogTitle.Text = _previous is null ? "添加账户" : "重新登录微软账户";
            }
        }
    }

    /// <summary>窗口关闭时取消浏览器认证并等待回收；禁止迟到认证结果保存到本地账户。</summary>
    public async Task StopAsync()
    {
        _closed = true; _loginCancellation?.Cancel(); Hide();
        if (_loginTask is not null) await _loginTask;
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
