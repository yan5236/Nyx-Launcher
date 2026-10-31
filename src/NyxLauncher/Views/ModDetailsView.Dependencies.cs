using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

public sealed partial class ModDetailsView
{
    private readonly Stack<DetailState> _history = new();
    private string _preferredGame = "";
    private string _preferredLoader = "";
    private IReadOnlyList<ResourceProject>? _dependencies;
    private CancellationTokenSource? _dependencyRequest;

    /// <summary>保存依赖跳转来源及其文件阶段和滚动位置；不持有弹窗或已结束请求。</summary>
    private sealed record DetailState(ResourceProject Project, IReadOnlyList<ModrinthModFile> Files,
        string Game, string Loader, string PreferredGame, string PreferredLoader,
        IReadOnlyList<ResourceProject>? Dependencies, double Offset);

    /// <summary>取消旧阶段的前置查询；请求自行释放令牌，已取消的结果不得写回新项目。</summary>
    private void CancelDependencies()
    {
        _dependencyRequest?.Cancel();
        _dependencyRequest = null;
    }

    /// <summary>仅在文件阶段显示前置区域；前置卡片和分隔线位于 ListView 头部，与文件共用滚动容器。</summary>
    private void RefreshDependencies()
    {
        CancelDependencies();
        DependencySection.Visibility = Visibility.Collapsed;
        DependencyCards.ItemsSource = null;
        DependencyRetry.Visibility = DependencyStatus.Visibility = Visibility.Collapsed;
        if (_project?.Category != "mod" || _game.Length == 0 || _loader.Length == 0) return;
        if (_dependencies is not null) { ShowDependencies(); return; }
        var files = _files.Where(x => x.GameVersions.Contains(_game) && x.Loaders.Contains(_loader)).ToArray();
        if (!files.Any(x => x.Dependencies.Count > 0)) return;
        DependencySection.Visibility = DependencyStatus.Visibility = Visibility.Visible;
        DependencyStatus.Text = "正在加载前置资源…";
        _ = LoadDependenciesAsync(files);
    }

    /// <summary>单独加载当前兼容文件的必需前置，并检查前置自身的游戏和加载器兼容性；失败只显示前置重试，不阻止文件选择和下载。</summary>
    private async Task LoadDependenciesAsync(IReadOnlyList<ModrinthModFile> files)
    {
        using var request = new CancellationTokenSource();
        _dependencyRequest = request;
        try
        {
            var projects = await _service.FetchRequiredProjectsAsync(files, _game, _loader, request.Token);
            request.Token.ThrowIfCancellationRequested();
            _dependencies = projects.Where(x => x.Id != _project?.Id).ToArray();
            ShowDependencies();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (HttpRequestException ex)
        {
            if (request.IsCancellationRequested) return;
            DependencyStatus.Text = ex.Message;
            DependencyRetry.Visibility = Visibility.Visible;
        }
        finally { if (ReferenceEquals(_dependencyRequest, request)) _dependencyRequest = null; }
    }

    /// <summary>绑定已加载的真实项目信息；无必需前置时同时隐藏标题和分隔线。</summary>
    private void ShowDependencies()
    {
        DependencyCards.ItemsSource = _dependencies;
        DependencySection.Visibility = _dependencies?.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DependencyStatus.Visibility = DependencyRetry.Visibility = Visibility.Collapsed;
    }

    /// <summary>在当前项目和阶段重试前置请求，沿用独立的取消与错误反馈。</summary>
    private void DependencyRetry_Click(object sender, RoutedEventArgs e) => RefreshDependencies();

    /// <summary>保存来源详情后进入前置项目，继承当前游戏和加载器直接列文件；支持多层和循环跳转逐层返回。</summary>
    private void Dependency_Click(object sender, RoutedEventArgs e)
    {
        if (!_active || _dialog is not null || _project is null || sender is not Button { Tag: ResourceProject project }) return;
        _history.Push(new(_project, _files, _game, _loader, _preferredGame, _preferredLoader,
            _dependencies, FindScrollViewer(Choices)?.VerticalOffset ?? 0));
        _preferredGame = _game;
        _preferredLoader = _loader;
        ShowProject(project, _game, _loader);
    }

    /// <summary>返回按钮明确标识上一层模组；根详情保持原有返回目录语义，键盘和读屏名称同步更新。</summary>
    private void UpdateBackLabel()
    {
        string label = _history.TryPeek(out var previous) ? "返回 " + previous.Project.Title : _project?.Category == "modpack" ? "返回整合包列表" : IsResource ? "返回资源列表" : "返回模组列表";
        ToolTipService.SetToolTip(TopBackButton, label);
        AutomationProperties.SetName(TopBackButton, label);
    }

    /// <summary>沿实际控件模板寻找列表自带的滚动容器，用于置顶与依赖返回时恢复位置；未加载模板时返回 null。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
