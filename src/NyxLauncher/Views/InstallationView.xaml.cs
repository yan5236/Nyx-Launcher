using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>游戏安装配置页；名称仅在本页编辑，真实加载器清单支持互斥选择与取消。</summary>
public sealed partial class InstallationView : UserControl
{
    private readonly LoaderAvailabilityService _service = new();
    private readonly FabricApiService _apiService = new();
    private IReadOnlyList<FabricApiVersion> _apiVersions = [];
    private FabricApiVersion? _selectedApi;
    private string _apiStatus = "正在检测…";
    private readonly Dictionary<LoaderKind, IReadOnlyList<LoaderVersion>> _versions = new();
    private readonly Dictionary<LoaderKind, string> _status = new();
    private CancellationTokenSource? _request;
    private LoaderVersionDialog? _dialog;
    private bool _active;
    private string _game = "";
    private SourceStrategy _strategy;
    private DownloadTaskManager? _tasks;
    private GameFolderStore? _folders;
    public LoaderVersion? SelectedLoader { get; private set; }
    public event EventHandler? BackRequested;
    public event EventHandler? TaskCreated;

    /// <summary>注入共享任务及游戏目录，点击安装时锁定当前目录，不随之后切换目录改变目标。</summary>
    public void Configure(DownloadTaskManager tasks, GameFolderStore folders) { _tasks = tasks; _folders = folders; }

    /// <summary>创建包含所选加载器及 API 的真实任务；成功返回列表，输入或目录错误留在当前页显示。</summary>
    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!_active || _tasks is null) return;
        InstallButton.IsEnabled = false;
        try
        {
            _tasks.AddGame(_folders?.Selected ?? "", VersionName.Text.Trim(), _game, SelectedLoader, _selectedApi);
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

    /// <summary>用原始游戏 ID 拉取加载器与 API 清单；切换游戏清空所有选择，编辑名称不改变兼容版本范围。</summary>
    public void Open(DownloadEntry entry, SourceStrategy strategy)
    {
        Close();
        _active = true;
        SelectedLoader = null;
        _selectedApi = null;
        _apiVersions = [];
        _apiStatus = "正在检测…";
        _versions.Clear();
        _status.Clear();
        VersionName.Text = entry.Title;
        _game = entry.Title;
        _strategy = strategy;
        _request = new CancellationTokenSource();
        InstallError.IsOpen = false;
        _ = DetectAsync(entry.Title, strategy, _request.Token);
    }

    /// <summary>放弃界面所有权，取消检测、关闭弹窗并释放加载器与 API 清单；迟到结果不得更新页面。</summary>
    public void Close()
    {
        _active = false;
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
        _dialog?.Hide();
        _dialog = null;
        _versions.Clear();
        _status.Clear();
        _apiVersions = [];
        _selectedApi = null;
        SelectedLoader = null;
    }

    /// <summary>控件卸载后停止网络工作，不保留后台请求。</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e) => Close();

    /// <summary>通知下载页返回，由父页取消请求并恢复原列表状态。</summary>
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>加载器和 API 独立拉取列表，失败互不影响；离开页面取消全部请求，防止旧游戏结果回填。</summary>
    private Task DetectAsync(string gameVersion, SourceStrategy strategy, CancellationToken token)
    {
        return Task.WhenAll(Enum.GetValues<LoaderKind>().Select(loader =>
            DetectLoaderAsync(loader, gameVersion, strategy, token)).Append(DetectApiAsync(gameVersion, token)));
    }

    /// <summary>按当前页面版本列表源策略读取指定游戏的 API 发布；检测中禁用重复请求，失败允许重试，取消后丢弃旧结果。</summary>
    private async Task DetectApiAsync(string game, CancellationToken token)
    {
        _apiStatus = "正在检测…";
        RefreshRows();
        try
        {
            var versions = await _apiService.FetchAsync(game, token, _strategy);
            token.ThrowIfCancellationRequested();
            _apiVersions = versions;
            _apiStatus = versions.Count > 0 ? "可以添加" : "暂无可用版本";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (HttpRequestException ex)
        {
            if (token.IsCancellationRequested) return;
            _apiStatus = "检测失败：" + ex.Message;
        }
        RefreshRows();
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

    /// <summary>更新加载器互斥状态与 API 前置条件；API 只在选中 Fabric 后可用，取消加载器同时清除 API。</summary>
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
                message.StartsWith("检测失败：", StringComparison.Ordinal) ? "检测失败，点击重试" : message;
            icon.Glyph = selected ? "\uE711" : "\uE76C";
            button.IsEnabled = (message.StartsWith("检测失败：", StringComparison.Ordinal) ||
                _versions.TryGetValue(loader, out var versions) && versions.Count > 0) &&
                (SelectedLoader is null || selected);
            button.Opacity = SelectedLoader is not null && !selected ? 0.45 : 1;
            ToolTipService.SetToolTip(status, selected ? SelectedLoader!.Version : message);
            AutomationProperties.SetName(button, selected ? $"取消 {loader} {SelectedLoader!.Version}" : $"选择 {loader} 版本");
        }
        bool fabric = SelectedLoader?.Loader == LoaderKind.Fabric;
        if (!fabric) _selectedApi = null;
        ApiStatus.Text = _selectedApi is not null ? "已选择 " + _selectedApi.Version :
            _apiStatus.StartsWith("检测失败：", StringComparison.Ordinal) ? "检测失败，点击重试" :
            _apiVersions.Count > 0 && !fabric ? "请先选择 Fabric" : _apiStatus;
        ApiAction.Glyph = _selectedApi is null ? "\uE76C" : "\uE711";
        ApiButton.IsEnabled = fabric && (_apiVersions.Count > 0 || _apiStatus.StartsWith("检测失败：", StringComparison.Ordinal));
        ApiButton.Opacity = fabric ? 1 : 0.45;
        ToolTipService.SetToolTip(ApiStatus, _selectedApi?.Version ?? _apiStatus);
        AutomationProperties.SetName(ApiButton, _selectedApi is null ? "选择 Fabric API 版本" : "取消 Fabric API " + _selectedApi.Version);
    }

    /// <summary>失败行重试成功后打开版本列表，检测中禁止重复请求；已选行取消，退出或切换游戏后丢弃迟到结果。</summary>
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
        if (SelectedLoader is not null) return;
        if (_status.GetValueOrDefault(loader, "").StartsWith("检测失败：", StringComparison.Ordinal))
        {
            var token = _request!.Token;
            await DetectLoaderAsync(loader, _game, _strategy, token);
            if (token.IsCancellationRequested || !_active || _dialog is not null || SelectedLoader is not null) return;
        }
        if (!_versions.TryGetValue(loader, out var versions) || versions.Count == 0) return;
        var dialog = new LoaderVersionDialog(XamlRoot, versions);
        _dialog = dialog;
        try
        {
            await dialog.ShowThemedAsync();
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

    /// <summary>复用版本弹窗选择 API；检测失败可点击重试，已选项点击取消，离开页面或取消 Fabric 后不弹出迟到列表。</summary>
    private async void Api_Click(object sender, RoutedEventArgs e)
    {
        if (!_active || _dialog is not null || SelectedLoader?.Loader != LoaderKind.Fabric) return;
        if (_apiStatus.StartsWith("检测失败：", StringComparison.Ordinal))
        {
            var token = _request!.Token;
            await DetectApiAsync(_game, token);
            if (token.IsCancellationRequested || !_active || _dialog is not null || SelectedLoader?.Loader != LoaderKind.Fabric) return;
        }
        if (_apiVersions.Count == 0) return;
        if (_selectedApi is not null) { _selectedApi = null; RefreshRows(); return; }
        var dialog = new LoaderVersionDialog(XamlRoot, _apiVersions);
        _dialog = dialog;
        try
        {
            await dialog.ShowThemedAsync();
            if (_active && ReferenceEquals(_dialog, dialog) && dialog.SelectedApi is not null)
            { _selectedApi = dialog.SelectedApi; RefreshRows(); }
        }
        finally
        {
            if (ReferenceEquals(_dialog, dialog)) { _dialog = null; ApiButton.Focus(FocusState.Programmatic); }
        }
    }
}
