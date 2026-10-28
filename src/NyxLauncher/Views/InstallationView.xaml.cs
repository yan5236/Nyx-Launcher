using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>游戏安装配置页；名称仅在本页编辑，真实加载器清单支持互斥选择与取消。</summary>
public sealed partial class InstallationView : UserControl
{
    private readonly LoaderAvailabilityService _service = new();
    private readonly Dictionary<LoaderKind, IReadOnlyList<LoaderVersion>> _versions = new();
    private readonly Dictionary<LoaderKind, string> _status = new();
    private CancellationTokenSource? _request;
    private LoaderVersionDialog? _dialog;
    private bool _active;
    private string _game = "";
    private DownloadTaskManager? _tasks;
    private GameFolderStore? _folders;
    public LoaderVersion? SelectedLoader { get; private set; }
    public event EventHandler? BackRequested;
    public event EventHandler? TaskCreated;

    /// <summary>注入共享任务及游戏目录，点击安装时锁定当前目录，不随之后切换目录改变目标。</summary>
    public void Configure(DownloadTaskManager tasks, GameFolderStore folders) { _tasks = tasks; _folders = folders; }

    /// <summary>创建真实后台任务；成功立即请求返回列表，输入或目录错误留在当前页明确显示。</summary>
    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!_active || _tasks is null) return;
        InstallButton.IsEnabled = false;
        try
        {
            _tasks.AddGame(_folders?.Selected ?? "", VersionName.Text.Trim(), _game, SelectedLoader);
            TaskCreated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { InstallError.Message = ex.Message; InstallError.IsOpen = true; }
        finally { InstallButton.IsEnabled = true; }
    }

    /// <summary>创建页面；卸载时取消请求和关闭弹窗，避免旧结果更新已离开的页面。</summary>
    public InstallationView()
    {
        InitializeComponent();
        Unloaded += View_Unloaded;
    }

    /// <summary>用原始游戏 ID 初始化并拉取真实清单；切换游戏清空选择，编辑名称不会改变清单目标或源策略。</summary>
    public void Open(DownloadEntry entry, SourceStrategy strategy)
    {
        Close();
        _active = true;
        SelectedLoader = null;
        _versions.Clear();
        _status.Clear();
        VersionName.Text = entry.Title;
        _game = entry.Title;
        InstallError.IsOpen = false;
        _ = DetectAsync(entry.Title, strategy);
    }

    /// <summary>放弃界面所有权并取消请求、关闭弹窗；令牌由异步方法释放，迟到结果不得更新页面。</summary>
    public void Close()
    {
        _active = false;
        _request?.Cancel();
        _request = null;
        _dialog?.Hide();
        _dialog = null;
    }

    /// <summary>控件卸载后停止网络工作，不保留后台请求。</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e) => Close();

    /// <summary>通知下载页返回，由父页取消请求并恢复原列表状态。</summary>
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>三个加载器独立拉取列表，失败互不影响；新请求或离开页面后旧结果失效。</summary>
    private async Task DetectAsync(string gameVersion, SourceStrategy strategy)
    {
        using var request = new CancellationTokenSource();
        _request = request;
        try
        {
            await Task.WhenAll(Enum.GetValues<LoaderKind>().Select(loader =>
                DetectLoaderAsync(loader, gameVersion, strategy, request.Token)));
        }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
        }
    }

    /// <summary>保存可供弹窗选择的真实列表；错误显示原因，取消不显示错误，刷新时保留其他行已选状态。</summary>
    private async Task DetectLoaderAsync(LoaderKind loader, string gameVersion, SourceStrategy strategy, CancellationToken token)
    {
        _status[loader] = "正在检测…";
        RefreshRows();
        try
        {
            var versions = await _service.FetchAsync(loader, gameVersion, strategy, token);
            token.ThrowIfCancellationRequested();
            _versions[loader] = versions;
            _status[loader] = versions.Count > 0 ? "可以添加" : "暂无可用版本";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (HttpRequestException ex)
        {
            if (token.IsCancellationRequested) return;
            _status[loader] = "检测失败：" + ex.Message;
        }
        RefreshRows();
    }

    /// <summary>统一更新文本、箭头和互斥禁用；点叉取消后恢复各加载器真实检测状态，不允许未取得清单的行被选择。</summary>
    private void RefreshRows()
    {
        foreach (var (loader, button, status, icon) in new[]
        {
            (LoaderKind.Forge, ForgeButton, ForgeStatus, ForgeAction),
            (LoaderKind.Fabric, FabricButton, FabricStatus, FabricAction),
            (LoaderKind.NeoForge, NeoForgeButton, NeoForgeStatus, NeoForgeAction)
        })
        {
            bool selected = SelectedLoader?.Loader == loader;
            string message = _status.GetValueOrDefault(loader, "正在检测…");
            status.Text = selected ? "已选择 " + SelectedLoader!.Version :
                message.StartsWith("检测失败：", StringComparison.Ordinal) ? "检测失败" : message;
            icon.Glyph = selected ? "\uE711" : "\uE76C";
            button.IsEnabled = _versions.TryGetValue(loader, out var versions) && versions.Count > 0 &&
                (SelectedLoader is null || selected);
            button.Opacity = SelectedLoader is not null && !selected ? 0.45 : 1;
            ToolTipService.SetToolTip(status, selected ? SelectedLoader!.Version : message);
            AutomationProperties.SetName(button, selected ? $"取消 {loader} {SelectedLoader!.Version}" : $"选择 {loader} 版本");
        }
    }

    /// <summary>可用行打开单一模态列表，选中项关闭后应用互斥状态；已选行直接取消，退出页面后丢弃弹窗结果。</summary>
    private async void Loader_Click(object sender, RoutedEventArgs e)
    {
        if (!_active || _dialog is not null || sender is not Button button ||
            !Enum.TryParse<LoaderKind>(button.Tag?.ToString(), out var loader)) return;
        if (SelectedLoader?.Loader == loader)
        {
            SelectedLoader = null;
            RefreshRows();
            return;
        }
        if (SelectedLoader is not null || !_versions.TryGetValue(loader, out var versions) || versions.Count == 0) return;
        var dialog = new LoaderVersionDialog(XamlRoot, versions);
        _dialog = dialog;
        try
        {
            await dialog.ShowAsync();
            if (_active && ReferenceEquals(_dialog, dialog) && dialog.SelectedVersion is not null)
            {
                SelectedLoader = dialog.SelectedVersion;
                RefreshRows();
            }
        }
        finally
        {
            if (ReferenceEquals(_dialog, dialog))
            {
                _dialog = null;
                button.Focus(FocusState.Programmatic);
            }
        }
    }
}
