using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>保留首页布局，接入账户与实际启动、窗口等待和停止生命周期。</summary>
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

    /// <summary>加载首页控件，服务通过主页面注入。</summary>
    public HomeView() => InitializeComponent();

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

    /// <summary>锁定版本和身份启动会话；资源检查在后台运行，认证保存留在 UI 上下文，再次点击停止。</summary>
    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null) { _session.Stop(); UpdateControls(); return; }
        if (_closing || _scanning || VersionList.SelectedItem is not InstalledVersion version ||
            _folders?.Selected is not string root || _accounts?.Selected is not GameAccount account) return;
        var session = new GameLaunchSession();
        _session = session;
        session.Changed += Session_Changed;
        Status.IsOpen = false; UpdateControls();
        var settings = _settings!.Current;
        try
        {
            _running = session.RunAsync(root, version, settings, account, AuthenticateAsync);
            await _running;
            ShowStatus(session.Status);
        }
        finally
        {
            session.Changed -= Session_Changed;
            _session = null; _running = null;
            UpdateControls(); Refresh();
        }
    }

    /// <summary>离线身份直接创建会话，微软身份静默刷新并原子保存；失败明确终止启动，不自动弹浏览器。</summary>
    private async Task<CmlLib.Core.Auth.MSession> AuthenticateAsync(GameAccount account, CancellationToken token)
    {
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
        ShowStatus(_session!.Status); UpdateControls();
    });

    /// <summary>显示真实阶段或失败原因，取消和正常退出作为普通状态处理。</summary>
    private void ShowStatus(LaunchStatus status)
    {
        Status.Message = status.Message;
        Status.Severity = status.Stage == LaunchStage.Failed ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        Status.IsOpen = !string.IsNullOrEmpty(status.Message);
    }

    /// <summary>正常启动复用全局绿色按钮资源，运行中使用红色停止状态；同步账户、版本锁定和窗口出现前后的文案。</summary>
    private void UpdateControls()
    {
        if (LaunchButton is null) return;
        bool busy = _session is not null;
        bool stopping = _session?.Status.Stage == LaunchStage.Stopping;
        string label = stopping ? "正在停止…" : busy ? (_session!.Status.Stage == LaunchStage.Running ? "停止运行" : "停止") : "启动游戏";
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
        CurrentAccount.Text = _accounts?.Selected is { } account ? "当前账户：" + account.Name + "（" + account.TypeLabel + "）" : "请先添加账户";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LaunchButton, label);
    }

    /// <summary>关闭时取消启动或停止游戏并等待回收，期间禁止刷新和再次启动。</summary>
    public async Task StopAsync()
    {
        _closing = true; _session?.Stop(); UpdateControls();
        if (_running is not null) await _running;
    }
}
