using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>首页版本选择、详情导航及启动进度；窗口出现后收起反馈，仅保留停止入口。</summary>
public sealed partial class HomeView : UserControl
{
    private GameFolderStore? _folders;
    private DownloadSettingsStore? _settings;
    private AccountStore? _accounts;
    private GameLaunchSession? _session;
    private Task? _running;
    private int _refresh;
    private bool _scanning;
    private bool _closing;
    private Storyboard? _progressAnimation;
    private int _progressGeneration;
    private bool _progressEnding;
    public event EventHandler<InstalledVersion>? ManageRequested;

    /// <summary>加载首页控件，服务通过主页面注入。</summary>
    public HomeView() => InitializeComponent();

    /// <summary>按首页可用宽高调整图片和三列启动区；控件等宽伸展，限制字体放大幅度，窄窗口仍可垂直滚动。</summary>
    public void ResizeLayout(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        double scale = Math.Clamp(width / 775, 1, 1.4);
        HeroImage.Height = Math.Max(220, Math.Min(width * 0.5, height * 0.62));
        VersionList.Height = LaunchButton.Height = ManageButton.Height = 72 * scale;
        LaunchArea.ColumnSpacing = 16 * scale;
        ProgressSpace.Height = 64 * scale;
        VersionList.FontSize = 15 * scale;
        LaunchLabel.FontSize = ManageLabel.FontSize = LaunchIcon.FontSize = ManageIcon.FontSize = 16 * scale;
        StageLabel.FontSize = LaunchVersionLabel.FontSize = 12 * scale;
    }

    /// <summary>同步缩放版本模板内的图标和两行文字；只处理本页固定的图标与文本布局，选中项和下拉项使用同一比例。</summary>
    private void VersionCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var card = (Grid)sender;
        double scale = VersionList.FontSize / 15;
        ((FontIcon)card.Children[0]).FontSize = 26 * scale;
        var labels = (StackPanel)card.Children[1];
        ((TextBlock)labels.Children[0]).FontSize = 15 * scale;
        ((TextBlock)labels.Children[1]).FontSize = 11 * scale;
    }

    /// <summary>共用目录、隔离和账户状态；切换账户只影响下一次启动。</summary>
    public void Configure(GameFolderStore folders, DownloadSettingsStore settings, AccountStore accounts)
    {
        _folders = folders; _settings = settings; _accounts = accounts;
        accounts.Changed += (_, _) => UpdateControls();
        Refresh();
    }

    /// <summary>扫描当前目录并保留选择；运行期间不替换列表，扫描代数隔离迟到结果。</summary>
    public async void Refresh()
    {
        if (_session is not null || _closing) return;
        int generation = ++_refresh;
        _scanning = true; UpdateControls();
        string? folder = _folders?.Selected;
        string? selected = (VersionList.SelectedItem as InstalledVersion)?.Name;
        try
        {
            var versions = folder is null ? [] : await Task.Run(() => InstalledVersionService.Scan(folder).Where(x => x.Error is null).ToArray());
            if (generation != _refresh) return;
            VersionList.ItemsSource = versions;
            VersionList.SelectedItem = versions.FirstOrDefault(x => x.Name == selected) ?? versions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            if (generation != _refresh) return;
            VersionList.ItemsSource = null;
            Status.Message = ex.Message; Status.Severity = InfoBarSeverity.Error; Status.IsOpen = true;
        }
        finally { if (generation == _refresh) { _scanning = false; UpdateControls(); } }
    }

    /// <summary>刷新版本选择对应的启动可用性，不创建进程。</summary>
    private void Version_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();

    /// <summary>请求主页面打开当前所选版本的详情；未选版本时不导航。</summary>
    private void Manage_Click(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is InstalledVersion version) ManageRequested?.Invoke(this, version);
    }

    /// <summary>锁定版本和身份启动会话并滑入阶段进度；再次点击停止，失败在会话回收后弹出原因与日志入口。</summary>
    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null) { _session.Stop(); UpdateControls(); return; }
        if (_closing || _scanning || VersionList.SelectedItem is not InstalledVersion version ||
            _folders?.Selected is not string root || _accounts?.Selected is not GameAccount account) return;
        var session = new GameLaunchSession();
        _session = session;
        session.Changed += Session_Changed;
        Status.IsOpen = false; BeginProgress(); UpdateControls();
        var settings = _settings!.Current;
        try
        {
            _running = session.RunAsync(root, version, settings, account, AuthenticateAsync);
            await _running;
            ShowProgress(session.Status);
        }
        finally
        {
            session.Changed -= Session_Changed;
            _session = null; _running = null;
            UpdateControls(); Refresh();
        }
        if (!_closing && session.Status.Stage == LaunchStage.Failed)
            await ShowFailureAsync(session);
    }

    /// <summary>启动在其他页面失败时等待已有弹窗关闭，再显示原因；避免 WinUI 同一窗口叠加模态弹窗导致异常，关闭启动器时放弃等待。</summary>
    private async Task ShowFailureAsync(GameLaunchSession session)
    {
        while (!_closing && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(popup => popup.Child is ContentDialog))
            await Task.Delay(100);
        if (!_closing) await new LaunchFailureDialog(XamlRoot, session).ShowAsync();
    }

    /// <summary>离线身份直接创建会话，微软或外置身份验证及刷新后原子保存；失败明确终止启动，不自动弹浏览器。</summary>
    private async Task<CmlLib.Core.Auth.MSession> AuthenticateAsync(GameAccount account, CancellationToken token)
    {
        if (account.IsExternal)
        {
            using var login = new ExternalLoginService();
            var external = await login.RefreshAsync(account, token);
            token.ThrowIfCancellationRequested();
            _accounts!.SaveExternal(external.Account, account, select: false);
            return external.Session;
        }
        if (!account.IsMicrosoft) return GameLaunchService.CreateSession(account);
        var result = await MicrosoftLoginService.AuthenticateAsync(account, false, null, token);
        token.ThrowIfCancellationRequested();
        _accounts!.SaveMicrosoft(result.Account, account, select: false);
        return result.Session;
    }

    /// <summary>后台通知排队刷新当前会话最新快照，迟到回调不能覆盖新会话。</summary>
    private void Session_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (sender != _session) return;
        ShowProgress(_session!.Status); UpdateControls();
    });

    /// <summary>每次启动重置进度代数并从上方滑入；旧退出动画和延迟不能隐藏新会话。</summary>
    private void BeginProgress()
    {
        _progressGeneration++; _progressEnding = false;
        StageLabel.Text = "准备启动"; StageProgress.Value = 0;
        LaunchProgress.Visibility = Visibility.Visible;
        AnimateProgress(true);
    }

    /// <summary>显示真实阶段和阶段里程碑；等待窗口停留在 94%，窗口出现后填满，停止或退出直接收起。</summary>
    private void ShowProgress(LaunchStatus status)
    {
        if (status.Stage is LaunchStage.Running or LaunchStage.Stopping or LaunchStage.Exited or LaunchStage.Failed)
        {
            if (!_progressEnding) _ = EndProgressAsync(status.Stage == LaunchStage.Running);
            return;
        }
        if (_progressEnding) return;
        StageLabel.Text = status.Message;
        ToolTipService.SetToolTip(StageLabel, status.Message);
        StageProgress.Value = status.Progress;
    }

    /// <summary>成功时短暂停留在完成进度，再向下淡出；快速重启或关闭时通过代数丢弃旧延迟。</summary>
    private async Task EndProgressAsync(bool completed)
    {
        _progressEnding = true;
        int generation = _progressGeneration;
        if (completed)
        {
            StageProgress.Value = 100; StageLabel.Text = "启动完成";
            await Task.Delay(300);
        }
        if (generation != _progressGeneration) return;
        AnimateProgress(false);
    }

    /// <summary>复用现有 220 毫秒平移淡入节奏，离场向下淡出且保持预留空间；尊重系统动画开关。</summary>
    private void AnimateProgress(bool entering)
    {
        _progressAnimation?.Stop();
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            LaunchProgress.Opacity = entering ? 1 : 0;
            LaunchProgress.Visibility = entering ? Visibility.Visible : Visibility.Collapsed;
            ProgressTranslation.Y = 0;
            return;
        }
        var opacity = new DoubleAnimation { From = entering ? 0 : 1, To = entering ? 1 : 0, Duration = TimeSpan.FromMilliseconds(220) };
        var translation = new DoubleAnimation
        {
            From = entering ? -14 : 0, To = entering ? 0 : 14, Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacity, LaunchProgress); Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(translation, ProgressTranslation); Storyboard.SetTargetProperty(translation, "Y");
        var animation = new Storyboard();
        animation.Children.Add(opacity); animation.Children.Add(translation);
        int generation = _progressGeneration;
        animation.Completed += (_, _) =>
        {
            if (!entering && generation == _progressGeneration) LaunchProgress.Visibility = Visibility.Collapsed;
        };
        _progressAnimation = animation;
        animation.Begin();
    }

    /// <summary>正常启动复用全局绿色按钮资源，运行中使用红色停止状态；同步启动可用性、版本锁定和两行按钮文字，副标题始终显示当前选择或本次运行版本。</summary>
    private void UpdateControls()
    {
        if (LaunchButton is null) return;
        bool busy = _session is not null;
        bool stopping = _session?.Status.Stage == LaunchStage.Stopping;
        string label = stopping ? "正在停止…" : busy ? (_session!.Status.Stage == LaunchStage.Running ? "停止运行" : "停止") : "启动游戏";
        LaunchVersionLabel.Text = (VersionList.SelectedItem as InstalledVersion)?.Name ?? "请先选择版本";
        ToolTipService.SetToolTip(LaunchButton, LaunchVersionLabel.Text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(LaunchButton, LaunchVersionLabel.Text);
        LaunchLabel.Text = label; LaunchIcon.Glyph = busy ? "\uE71A" : "\uE768";
        var stopBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 183, 52, 52));
        LaunchButton.Background = busy ? stopBrush : (SolidColorBrush)Application.Current.Resources["AccentButtonBackground"];
        LaunchButton.BorderBrush = LaunchButton.Background;
        foreach (string key in new[] { "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
            "AccentButtonBorderBrushPointerOver", "AccentButtonBorderBrushPressed", "AccentButtonBackgroundDisabled" })
        {
            if (busy) LaunchButton.Resources[key] = stopBrush;
            else LaunchButton.Resources.Remove(key);
        }
        LaunchButton.IsEnabled = !_closing && (busy ? !stopping : !_scanning && _accounts?.Selected is not null && VersionList.SelectedItem is InstalledVersion);
        VersionList.IsEnabled = !busy && !_closing;
        ManageButton.IsEnabled = !_closing && VersionList.SelectedItem is InstalledVersion;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LaunchButton, label);
    }

    /// <summary>关闭时取消启动或停止游戏并等待回收，期间禁止刷新和再次启动。</summary>
    public async Task StopAsync()
    {
        _closing = true; _session?.Stop(); UpdateControls();
        if (_running is not null) await _running;
    }
}
