using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using NyxLauncher.Services;
using Windows.ApplicationModel.DataTransfer;

namespace NyxLauncher.Views;

/// <summary>使用同一个 WinUI 对话框选择类型、输入名称和显示保存错误。</summary>
public sealed partial class AddAccountDialog : ContentDialog
{
    private readonly AccountStore _accounts;
    private readonly bool _restricted;
    private Storyboard? _transition;
    private readonly GameAccount? _previous;
    private CancellationTokenSource? _loginCancellation;
    private Task? _loginTask;
    private bool _closed;
    private Uri? _authorizationUri;
    private bool _openingBrowser;

    /// <summary>注入账户存储和可选重登身份；首次空账户位于大陆以外时禁用离线与外置入口，配置失败仅禁用微软入口。</summary>
    public AddAccountDialog(AccountStore accounts, GameAccount? previous = null, bool restrictLocalAccounts = false)
    {
        InitializeComponent(); _accounts = accounts; _previous = previous; _restricted = restrictLocalAccounts && previous is null;
        try { MicrosoftLoginSettings.Load(); MicrosoftHint.Text = "在系统浏览器中登录"; }
        catch (Exception ex) { MicrosoftButton.IsEnabled = false; Error.Message = ex.Message; Error.IsOpen = true; }
        Closed += (_, _) => { _authorizationUri = null; _externalTokens = null; ExternalPassword.Password = ""; _closed = true; _transition?.Stop(); _loginCancellation?.Cancel(); };
        if (_restricted)
        {
            OfflineButton.IsEnabled = false;
            ExternalButton.IsEnabled = false;
            RegionNotice.IsOpen = true;
            ToolTipService.SetToolTip(OfflineButton, "添加微软账户后可用。");
            ToolTipService.SetToolTip(ExternalButton, "添加微软账户后可用。");
        }
        if (previous?.IsExternal == true)
        {
            Loaded += (_, _) => ShowCredentials(new ExternalServer(previous.AuthServerName!, previous.AuthServer!));
        }
        else if (previous is not null)
        {
            OfflineButton.IsEnabled = false;
            Loaded += (_, _) => { if (MicrosoftButton.IsEnabled && _loginTask is null) _loginTask = LoginAsync(); };
        }
    }

    /// <summary>发起一次系统浏览器登录；任务保留供窗口关闭等待，重复点击不会创建竞争的本机回调监听。</summary>
    private void Microsoft_Click(object sender, RoutedEventArgs e)
    { if (_loginCancellation is null) _loginTask = LoginAsync(); }

    /// <summary>展示认证阶段和可重试的浏览器操作，链接仅在回调等待期间可用；全部认证成功才保存账户，取消及关闭释放监听。</summary>
    private async Task LoginAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _loginCancellation = cancellation;
        Types.Visibility = Visibility.Collapsed; LoginProgress.Visibility = Visibility.Visible; LoginRing.IsActive = true;
        Error.IsOpen = false; DialogTitle.Text = _previous is null ? "微软登录" : "重新登录微软账户";
        PrimaryButtonText = "打开浏览器"; SecondaryButtonText = "复制链接"; CloseButtonText = "取消";
        IsPrimaryButtonEnabled = false; IsSecondaryButtonEnabled = false;
        LoginStatus.Text = "正在准备微软登录…"; BrowserHint.Visibility = Visibility.Visible;
        var progress = new Progress<string>(text => { if (!_closed && _loginCancellation == cancellation) LoginStatus.Text = text; });
        var authorization = new Progress<Uri?>(uri =>
        {
            if (_closed || _loginCancellation != cancellation) return;
            _authorizationUri = uri;
            IsPrimaryButtonEnabled = uri is not null && !_openingBrowser;
            IsSecondaryButtonEnabled = uri is not null;
            BrowserHint.Visibility = uri is null ? Visibility.Collapsed : Visibility.Visible;
            if (uri is not null) _ = OpenLoginBrowserAsync();
            else Error.IsOpen = false;
        });
        try
        {
            var result = await MicrosoftLoginService.AuthenticateAsync(_previous, true, progress, cancellation.Token, authorization);
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
            _authorizationUri = null;
            if (!_closed)
            {
                LoginRing.IsActive = false; LoginProgress.Visibility = Visibility.Collapsed; Types.Visibility = Visibility.Visible;
                DialogTitle.Text = _previous is null ? "添加账户" : "重新登录微软账户";
                PrimaryButtonText = "取消"; SecondaryButtonText = ""; CloseButtonText = "";
                IsPrimaryButtonEnabled = true; IsSecondaryButtonEnabled = true;
            }
        }
    }

    /// <summary>微软等待步骤点击主按钮只重开本次授权链接并保留弹窗；其他步骤仍由原生主按钮执行取消。</summary>
    private void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_loginCancellation is null) return;
        args.Cancel = true;
        _ = OpenLoginBrowserAsync();
    }

    /// <summary>尝试用系统浏览器打开当前授权链接；失败保留回调和复制入口，完成后恢复重试，忽略关闭或过期链接的迟到结果。</summary>
    private async Task OpenLoginBrowserAsync()
    {
        if (_closed || _authorizationUri is not { } uri || _openingBrowser) return;
        _openingBrowser = true; IsPrimaryButtonEnabled = false;
        bool opened;
        try { opened = await Windows.System.Launcher.LaunchUriAsync(uri); }
        catch (Exception) { opened = false; }
        finally
        {
            _openingBrowser = false;
            if (!_closed) IsPrimaryButtonEnabled = _loginCancellation is null || _authorizationUri is not null;
        }
        if (_closed || _authorizationUri != uri) return;
        Error.IsOpen = !opened;
        if (!opened) Error.Message = "无法打开系统浏览器，请再次点击“打开浏览器”，或复制链接到本机浏览器继续验证。";
    }

    /// <summary>将当前有效授权链接复制为文本；剪贴板失败只提示重试，不中断认证，不写入日志或账户文件。</summary>
    private void CopyLoginLink()
    {
        if (_closed || _authorizationUri is not { } uri) return;
        try
        {
            var content = new DataPackage(); content.SetText(uri.AbsoluteUri); Clipboard.SetContent(content);
            Error.IsOpen = false;
            LoginStatus.Text = "链接已复制，请在本机浏览器中打开并完成微软登录…";
        }
        catch (Exception) { Error.Message = "无法复制链接，请重试或点击“打开浏览器”。"; Error.IsOpen = true; }
    }

    /// <summary>窗口关闭时取消浏览器认证并等待回收；禁止迟到认证结果保存到本地账户。</summary>
    public async Task StopAsync()
    {
        _closed = true; _loginCancellation?.Cancel(); Hide();
        if (_loginTask is not null) await _loginTask;
    }

    /// <summary>进入离线输入步骤并移动键盘焦点，允许 Enter 确认和返回类型选择；区域限制期间不进入。</summary>
    private void Offline_Click(object sender, RoutedEventArgs e)
    {
        if (_restricted) return;
        DialogTitle.Text = "添加离线账户"; Types.Visibility = Visibility.Collapsed; AccountName.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        SecondaryButtonText = "确定"; DefaultButton = ContentDialogButton.Secondary;
        AnimateStep(true);
        AccountName.Focus(FocusState.Programmatic);
    }

    /// <summary>返回类型列表而不关闭弹窗，保留已输入名称但清除旧错误。</summary>
    private void Back_Click(object sender, RoutedEventArgs args)
    {
        if (_loginCancellation is not null) return;
        if (ExternalPanel.Visibility == Visibility.Visible && BackExternal()) return;
        ExternalPanel.Visibility = Visibility.Collapsed; ExternalPassword.Password = ""; _externalTokens = null;
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

    /// <summary>微软步骤复制链接并保持弹窗，外置步骤转交异步确认；离线账户成功写入后关闭，名称或 I/O 错误留在当前步骤。</summary>
    private void Confirm_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_loginCancellation is not null) { args.Cancel = true; CopyLoginLink(); return; }
        if (ExternalPanel.Visibility == Visibility.Visible)
        {
            args.Cancel = true;
            ConfirmExternal();
            return;
        }
        try { _accounts.Add(AccountName.Text); }
        catch (Exception ex) { args.Cancel = true; Error.Message = ex.Message; Error.IsOpen = true; }
    }
}
