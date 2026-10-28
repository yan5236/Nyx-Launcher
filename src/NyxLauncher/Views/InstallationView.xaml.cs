using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>游戏安装配置页；版本名称仅在本页编辑，加载器行仅展示实时可用性。</summary>
public sealed partial class InstallationView : UserControl
{
    private readonly LoaderAvailabilityService _service = new();
    private CancellationTokenSource? _request;
    private string _gameVersion = "";
    public event EventHandler? BackRequested;

    /// <summary>创建页面；卸载时取消探测，避免异步结果更新已离开的页面。</summary>
    public InstallationView()
    {
        InitializeComponent();
        Unloaded += View_Unloaded;
    }

    /// <summary>用选中的原始游戏 ID 初始化显示名称并开始检测；编辑名称不会改变检测目标。</summary>
    public void Open(DownloadEntry entry)
    {
        _gameVersion = entry.Title;
        VersionName.Text = entry.Title;
        _ = DetectAsync();
    }

    /// <summary>放弃当前探测的界面所有权并取消网络请求；令牌由异步方法释放。</summary>
    public void Close()
    {
        _request?.Cancel();
        _request = null;
    }

    /// <summary>控件卸载后停止网络工作，不保留后台探测。</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e) => Close();

    /// <summary>通知下载页请求返回，由父页在接受转场时取消探测并恢复原列表状态。</summary>
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>并行检测三个加载器，独立刷新各行；新请求或离开页面后旧结果失效。</summary>
    private async Task DetectAsync()
    {
        Close();
        using var request = new CancellationTokenSource();
        _request = request;
        try
        {
            await Task.WhenAll(
                DetectLoaderAsync(LoaderKind.Forge, ForgeStatus, _gameVersion, request.Token),
                DetectLoaderAsync(LoaderKind.Fabric, FabricStatus, _gameVersion, request.Token),
                DetectLoaderAsync(LoaderKind.NeoForge, NeoForgeStatus, _gameVersion, request.Token));
        }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
        }
    }

    /// <summary>将单个加载器结果映射为同一行灰色状态；失败提供悬停错误提示，取消不显示错误。</summary>
    private async Task DetectLoaderAsync(LoaderKind loader, TextBlock status, string gameVersion, CancellationToken token)
    {
        status.Text = "正在检测…";
        ToolTipService.SetToolTip(status, null);
        try
        {
            bool available = await _service.CheckAsync(loader, gameVersion, token);
            token.ThrowIfCancellationRequested();
            status.Text = available ? "可以添加" : "暂无可用版本";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (HttpRequestException ex)
        {
            if (token.IsCancellationRequested) return;
            status.Text = "检测失败";
            ToolTipService.SetToolTip(status, ex.Message);
        }
    }
}
