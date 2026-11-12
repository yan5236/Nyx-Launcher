using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>首页启动台：世界画面、版本选择器与启动进度；窗口缩放只改变画面尺寸，控件保持固定像素。</summary>
public sealed partial class HomeView : UserControl
{
    public InstalledVersion? SelectedVersion => VersionList.SelectedItem as InstalledVersion;
    private GameFolderStore? _folders;
    private DownloadSettingsStore? _settings;
    private AccountStore? _accounts;
    private JavaRuntimeStore? _java;
    private GameLaunchSession? _session;
    private Task? _running;
    private int _refresh;
    private bool _scanning;
    private bool _closing;
    private Storyboard? _progressAnimation;
    private int _progressGeneration;
    private bool _progressEnding;
    public event EventHandler<InstalledVersion>? ManageRequested;
    public event EventHandler<bool>? CrashAnalysisChanged;
    public bool IsLaunching => _session is not null;

    /// <summary>加载首页并在挂载时监听图片设置，卸载时解除订阅；游戏服务由主页面注入。</summary>
    public HomeView()
    {
        InitializeComponent();
        Loaded += HomeImage_Loaded;
        Unloaded += HomeImage_Unloaded;
    }

    /// <summary>按内容区可用宽高设定启动画面与控制台尺寸；控件、字体和间距不随窗口放大，最大化后仅画面变大。</summary>
    public void ResizeLayout(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        Root.MinHeight = height;
        double cardWidth = Math.Min(width, 1600);
        // 内容区顶部已有 20 像素留白，上下各留 30 像素时画面在视觉上居中。
        double cardHeight = Math.Max(340, Math.Min(height - 40, Math.Max(360, cardWidth * 0.78)));
        StageCard.Width = cardWidth;
        StageCard.Height = cardHeight;
        Deck.Width = Math.Min(800, Math.Max(280, cardWidth - 44));
        ManageButton.Visibility = cardWidth >= 620 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>共用目录、隔离、账户与 Java 状态；切换选择只影响下一次启动。</summary>
    public void Configure(GameFolderStore folders, DownloadSettingsStore settings, AccountStore accounts, JavaRuntimeStore java)
    {
        _folders = folders; _settings = settings; _accounts = accounts; _java = java;
        accounts.Changed += (_, _) => UpdateControls();
        Refresh();
    }

    /// <summary>扫描当前目录并恢复已保存的版本选择，无效选择回退首项；运行期间不替换列表，扫描代数与目录校验隔离迟到结果。</summary>
    public async void Refresh()
    {
        if (_session is not null || _closing) return;
        int generation = ++_refresh;
        _scanning = true; UpdateControls();
        string? folder = _folders?.Selected;
        try
        {
            var versions = folder is null ? [] : await Task.Run(() => InstalledVersionService.Scan(folder).Where(x => x.Error is null).ToArray());
            if (generation != _refresh || folder != _folders?.Selected) return;
            VersionList.ItemsSource = versions;
            VersionList.SelectedItem = _folders?.RestoreVersion(versions);
        }
        catch (Exception ex)
        {
            if (generation != _refresh) return;
            VersionList.ItemsSource = null;
            Status.Message = ex.Message; Status.Severity = InfoBarSeverity.Error; Status.IsOpen = true;
        }
        finally { if (generation == _refresh) { _scanning = false; UpdateControls(); } }
    }

    /// <summary>用户切换版本时立即保存到当前目录配置；扫描重绑及空选择不写盘，失败通过首页提示，仍刷新启动可用性。</summary>
    private void Version_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_scanning && VersionList.SelectedItem is InstalledVersion version)
        {
            try { _folders?.SelectVersion(version.Name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                Status.Message = "版本选择未保存：" + ex.Message;
                Status.Severity = InfoBarSeverity.Error; Status.IsOpen = true;
            }
        }
        UpdateControls();
    }

    /// <summary>请求主页面打开当前所选版本的详情；未选版本时不导航。</summary>
    private void Manage_Click(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is InstalledVersion version) ManageRequested?.Invoke(this, version);
    }

    /// <summary>读取系统动画开关，不使用额外设置项。</summary>
    private static bool AnimationsEnabled() => new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    /// <summary>锁定版本、身份和 Java 快照启动会话；再次点击停止，失败在会话回收后弹出原因与日志入口。</summary>
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
            _running = session.RunAsync(root, version, settings, account, AuthenticateAsync, _java?.Current);
            await _running;
            ShowProgress(session.Status);
            if (!_closing && session.Status.Stage == LaunchStage.Failed)
                await ShowFailureAsync(session);
        }
        finally
        {
            session.Changed -= Session_Changed;
            _session = null; _running = null;
            UpdateControls(); Refresh();
        }
    }

    /// <summary>启动在其他页面失败时等待已有弹窗关闭，再显示原因；避免 WinUI 同一窗口叠加模态弹窗导致异常，关闭启动器时放弃等待。</summary>
    private async Task ShowFailureAsync(GameLaunchSession session)
    {
        if (session.Crashed) CrashAnalysisChanged?.Invoke(this, true);
        try
        {
            // 给 Toast 一帧呈现时间，再在后台读取日志。
            await Task.Delay(150);
            var report = await Task.Run(() => GameCrashReport.Collect(session));
            while (!_closing && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(popup => popup.Child is ContentDialog))
                await Task.Delay(100);
            CrashAnalysisChanged?.Invoke(this, false);
            if (!_closing) await new LaunchFailureDialog(XamlRoot, session, report).ShowThemedAsync();
        }
        finally { CrashAnalysisChanged?.Invoke(this, false); }
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

    /// <summary>启动时让选择器淡出、进度淡入；每次启动重置进度代数，旧退出动画和延迟不能隐藏新会话。</summary>
    private void BeginProgress()
    {
        _progressGeneration++; _progressEnding = false;
        StageLabel.Text = "准备启动"; StageProgress.Value = 0; ProgressPercent.Text = "0%";
        VersionList.Visibility = Visibility.Collapsed;
        LaunchProgress.Visibility = Visibility.Visible;
        AnimateProgress(true);
    }

    /// <summary>显示真实阶段、进度和百分比；等待窗口停留在 94%，窗口出现后填满，停止或退出直接收起。</summary>
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
        ProgressPercent.Text = Math.Round(status.Progress) + "%";
    }

    /// <summary>成功时短暂停留在完成进度，再向下淡出；快速重启或关闭时通过代数丢弃旧延迟。</summary>
    private async Task EndProgressAsync(bool completed)
    {
        _progressEnding = true;
        int generation = _progressGeneration;
        if (completed)
        {
            StageProgress.Value = 100; StageLabel.Text = "启动完成"; ProgressPercent.Text = "100%";
            await Task.Delay(300);
        }
        if (generation != _progressGeneration) return;
        AnimateProgress(false);
    }

    /// <summary>复用现有 220 毫秒平移淡入节奏，离场向下淡出；结束时把位置交还版本选择器，尊重系统动画开关。</summary>
    private void AnimateProgress(bool entering)
    {
        _progressAnimation?.Stop();
        if (!AnimationsEnabled())
        {
            LaunchProgress.Opacity = entering ? 1 : 0;
            LaunchProgress.Visibility = entering ? Visibility.Visible : Visibility.Collapsed;
            ProgressTranslation.Y = 0;
            if (!entering) VersionList.Visibility = Visibility.Visible;
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
            if (entering || generation != _progressGeneration) return;
            LaunchProgress.Visibility = Visibility.Collapsed;
            VersionList.Visibility = Visibility.Visible;
        };
        _progressAnimation = animation;
        animation.Begin();
    }

    /// <summary>正常启动复用全局绿色按钮资源，运行中使用红色停止状态；同步启动可用性、选择器和按钮文字。</summary>
    private void UpdateControls()
    {
        if (LaunchButton is null) return;
        bool busy = _session is not null;
        bool stopping = _session?.Status.Stage == LaunchStage.Stopping;
        string label = stopping ? "正在停止…" : busy ? (_session!.Status.Stage == LaunchStage.Running ? "停止运行" : "停止") : "启动游戏";
        LaunchLabel.Text = label;
        LaunchIcon.Glyph = busy ? "\uE71A" : "\uE768";
        var stopBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 183, 52, 52));
        LaunchButton.Background = busy ? stopBrush : (SolidColorBrush)Application.Current.Resources["AccentButtonBackground"];
        LaunchButton.BorderBrush = LaunchButton.Background;
        var foreground = busy ? new SolidColorBrush(Microsoft.UI.Colors.White) : (SolidColorBrush)Application.Current.Resources["AccentButtonForeground"];
        LaunchLabel.Foreground = LaunchIcon.Foreground = foreground;
        foreach (string key in new[] { "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
            "AccentButtonBorderBrushPointerOver", "AccentButtonBorderBrushPressed", "AccentButtonBackgroundDisabled" })
        {
            if (busy) LaunchButton.Resources[key] = stopBrush;
            else LaunchButton.Resources.Remove(key);
        }
        LaunchButton.IsEnabled = !_closing && (busy ? !stopping : !_scanning && _accounts?.Selected is not null && VersionList.SelectedItem is InstalledVersion);
        VersionList.IsEnabled = !busy && !_closing && !_scanning;
        ManageButton.IsEnabled = !_closing && VersionList.SelectedItem is InstalledVersion;
        AutomationProperties.SetName(LaunchButton, label);
    }

    /// <summary>关闭时取消启动或停止游戏并等待回收，期间禁止刷新和再次启动。</summary>
    public async Task StopAsync()
    {
        _closing = true; _session?.Stop(); UpdateControls();
        if (_running is not null) await _running;
    }
}


