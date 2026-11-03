using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>游戏页获取真实清单，其他分类委托资源目录；共用下载任务入口并按页面状态取消请求。</summary>
public sealed partial class DownloadsView : UserControl
{
    private IReadOnlyList<DownloadEntry> _entries = Array.Empty<DownloadEntry>();
    private readonly VersionCatalogService _service = new();
    private DownloadSettingsStore? _settings;
    private DownloadTaskManager? _tasks;
    private GameFolderStore? _folders;
    private int? _pendingCategory;
    private CancellationTokenSource? _request;
    private Storyboard? _transition;
    private Storyboard? _pageTransition;
    private CancellationTokenSource? _pageRequest;
    private string _category = "game";
    private bool _active;
    private bool _busy;
    private bool _showTasks;

    /// <summary>创建下载页并注册重试、安装配置返回和卸载清理；尚未打开下载页时不发网络请求。</summary>
    public DownloadsView()
    {
        InitializeComponent();
        LoadStatus.RetryRequested += RetryRequested;
        ResourcesContent.ModRequested += Mod_Requested;
        Loaded += View_Loaded;
        Unloaded += View_Unloaded;
    }

    /// <summary>注入主页面共用设置，使重新进入下载页立即采用最新源策略。</summary>
    public void Configure(DownloadSettingsStore settings, DownloadTaskManager tasks, GameFolderStore folders)
    {
        _settings = settings;
        _tasks = tasks;
        _folders = folders;
        ResourcesContent.Configure(settings);
        ResourceIcon.Configure(settings);
        TasksContent.Configure(tasks);
        tasks.TaskAdded += (_, _) => TaskBadge.Visibility = _showTasks && _active ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>首次进入安装详情才创建控件并连接共享任务，避免仅浏览资源时构造加载器设置界面。</summary>
    private void EnsureInstallation()
    {
        if (InstallationContent is not null) return;
        var detail = (InstallationView)FindName(nameof(InstallationContent));
        detail.Configure(_tasks!, _folders!);
        detail.BackRequested += Installation_BackRequested;
        detail.TaskCreated += Installation_TaskCreated;
    }

    /// <summary>首次打开或后台卸载后创建资源详情；前台切换复用控件，下载任务始终由共享服务持有。</summary>
    private void EnsureModDetails()
    {
        if (ModDetailsContent is not null) return;
        var detail = (ModDetailsView)FindName(nameof(ModDetailsContent));
        detail.Configure(_tasks!, _folders!, _settings!);
        detail.BackRequested += Mod_BackRequested;
    }

    /// <summary>取消指定详情的请求并卸载整个视觉树；只接受当前实例，避免旧转场回调卸载新页面。</summary>
    private void ReleaseDetail(FrameworkElement? detail)
    {
        if (detail is null) return;
        if (ReferenceEquals(detail, InstallationContent)) InstallationContent.Close();
        else if (ReferenceEquals(detail, ModDetailsContent)) ModDetailsContent.Release();
        else return;
        UnloadObject(detail);
    }

    /// <summary>持续后台空闲时卸载隐藏详情；前台切换只清空数据并复用控件，避免反复创建原生模板造成分配峰值。</summary>
    public void ReleaseUnusedDetails()
    {
        if (InstallationContent?.Visibility == Visibility.Collapsed) ReleaseDetail(InstallationContent);
        if (ModDetailsContent?.Visibility == Visibility.Collapsed) ReleaseDetail(ModDetailsContent);
    }

    /// <summary>任务创建后立即恢复下载列表；不切到任务页，保留右上角未读提示。</summary>
    private void Installation_TaskCreated(object? sender, EventArgs e)
    {
        InstallationContent.Close();
        InstallationContent.Visibility = Visibility.Collapsed;
        CatalogPanel.Visibility = Visibility.Visible;
        ResetPage(CatalogPanel);
        ResetPage(InstallationContent);
    }

    /// <summary>右侧独立 Tab 与左侧分类互斥，进入任务页清除未读提示、停止清单请求并播放正文转场。</summary>
    private void TaskSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is null || TasksContent is null) return;
        _showTasks = true;
        ContentSelector.SelectedItem = null;
        CancelLoad();
        ResourcesContent.SetActive(false);
        SearchControls.Visibility = ContentGrid.Visibility = ResourcesContent.Visibility = Visibility.Collapsed;
        TasksContent.Visibility = Visibility.Visible;
        TaskBadge.Visibility = Visibility.Collapsed;
        TabBody.PlayTransition();
    }

    /// <summary>安装新版本入口使用游戏分类；分类切换时沿用现有筛选重置与加载流程。</summary>
    public void ShowGames() => SelectCategory(0);

    /// <summary>从版本管理直接选中下载页的模组分类，复用现有资源目录加载与筛选。</summary>
    public void ShowMods() => SelectCategory(1);

    /// <summary>尚未加载时记住跨模块要求的分类，模板加载后再选中，防止初始游戏 Tab 覆盖模组入口。</summary>
    private void SelectCategory(int index)
    {
        if (!IsLoaded) _pendingCategory = index;
        ContentSelector.SelectedItem = ContentSelector.Items[index];
    }

    /// <summary>首次模板加载完成后应用跨模块分类请求；普通重新显示沿用用户当前选择。</summary>
    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_pendingCategory is not { } index) return;
        _pendingCategory = null;
        ContentSelector.SelectedItem = ContentSelector.Items[index];
    }

    /// <summary>已安装模组入口跳过版本与加载器选择，进入精确匹配的文件列表；导航离开时沿用详情取消与转场清理。</summary>
    public async Task ShowModFilesAsync(ResourceProject project, InstalledVersion version)
    {
        if (!_active || _pageRequest is not null) return;
        EnsureModDetails();
        ModDetailsContent.OpenFiles(project, version.GameVersion, version.Loader.Split(':')[0].Trim().ToLowerInvariant());
        await SwitchPageAsync(true, ModDetailsContent);
    }

    /// <summary>页面显示时获取当前分类；离开时取消游戏和资源清单、两类详情请求与双向转场，并恢复列表层级和交互。</summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active)
        {
            if (_showTasks) TaskBadge.Visibility = Visibility.Collapsed;
            else _ = LoadCategoryAsync();
        }
        else
        {
            ResourcesContent.SetActive(false);
            CancelLoad();
            _pageRequest?.Cancel();
            _pageRequest = null;
            _pageTransition?.Stop();
            _pageTransition = null;
            ResetPage(CatalogPanel);
            if (InstallationContent is not null) ResetPage(InstallationContent);
            if (ModDetailsContent is not null) ResetPage(ModDetailsContent);
            InstallationContent?.Close();
            ModDetailsContent?.Release();
            if (InstallationContent is not null) InstallationContent.Visibility = Visibility.Collapsed;
            if (ModDetailsContent is not null) ModDetailsContent.Visibility = Visibility.Collapsed;
            _entries = [];
            EntryList.ItemsSource = null;
            CatalogPanel.Visibility = Visibility.Visible;
        }
    }

    /// <summary>点击真实游戏条目开始探测并向左转场；忽略非游戏分类和转场期间的重复输入。</summary>
    private async void EntryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (!_active || _busy || _pageRequest is not null || _category != "game" || e.ClickedItem is not DownloadEntry entry) return;
        EnsureInstallation();
        InstallationContent.Open(entry, _settings?.Current.VersionSource ?? SourceStrategy.OfficialWithFallback);
        await SwitchPageAsync(true);
    }

    /// <summary>向右转场回已有列表，保留搜索、筛选和滚动位置；完成后恢复键盘焦点。</summary>
    private async void Installation_BackRequested(object? sender, EventArgs e)
    {
        if (_pageRequest is not null) return;
        InstallationContent.Close();
        await SwitchPageAsync(false);
        if (_active && CatalogPanel.Visibility == Visibility.Visible) EntryList.Focus(FocusState.Programmatic);
    }

    /// <summary>携带目录当前版本和加载器筛选打开模组详情并淡入淡出；保留原列表、分页及滚动位置，转场中忽略重复操作。</summary>
    private async void Mod_Requested(object? sender, ResourceProject project)
    {
        if (!_active || _pageRequest is not null) return;
        EnsureModDetails();
        ModDetailsContent.Open(project, ResourcesContent.SelectedGameVersion, ResourcesContent.SelectedLoader);
        await SwitchPageAsync(true, ModDetailsContent);
    }

    /// <summary>关闭详情请求和弹窗，返回保留的目录后释放详情数据并恢复焦点；被侧栏取消的转场不得清空随后打开的新详情。</summary>
    private async void Mod_BackRequested(object? sender, EventArgs e)
    {
        if (_pageRequest is not null) return;
        var detail = ModDetailsContent;
        ModDetailsContent.Close();
        await SwitchPageAsync(false, ModDetailsContent);
        if (ReferenceEquals(detail, ModDetailsContent) && detail.Visibility == Visibility.Collapsed) detail.Release();
        if (_active) ResourcesContent.Focus(FocusState.Programmatic);
    }

    /// <summary>游戏详情平移并淡入淡出，模组详情仅淡入淡出；共用既有转场生命周期，遵循系统动画开关，取消后不覆盖新页面。</summary>
    private async Task SwitchPageAsync(bool entering, FrameworkElement? detailPage = null)
    {
        detailPage ??= InstallationContent;
        using var request = new CancellationTokenSource();
        _pageRequest = request;
        FrameworkElement outgoing = entering ? CatalogPanel : detailPage;
        FrameworkElement incoming = entering ? detailPage : CatalogPanel;
        outgoing.IsHitTestVisible = incoming.IsHitTestVisible = false;
        incoming.Visibility = Visibility.Visible;
        try
        {
            if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                double distance = detailPage == ModDetailsContent ? 0 : entering ? 32 : -32;
                _pageTransition = new Storyboard();
                AddPageAnimation(_pageTransition, outgoing, 1, 0, 0, -distance);
                AddPageAnimation(_pageTransition, incoming, 0, 1, distance, 0);
                var completed = new TaskCompletionSource();
                _pageTransition.Completed += (_, _) => completed.TrySetResult();
                using var cancellation = request.Token.Register(() => completed.TrySetCanceled(request.Token));
                _pageTransition.Begin();
                await completed.Task;
                request.Token.ThrowIfCancellationRequested();
            }
            outgoing.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_pageRequest, request))
            {
                _pageTransition?.Stop();
                _pageTransition = null;
                ResetPage(outgoing);
                ResetPage(incoming);
                _pageRequest = null;
            }
        }
    }

    /// <summary>将页面透明度和水平位移加入同一个 220 毫秒缓出动画，进入与返回共用相同节奏。</summary>
    private static void AddPageAnimation(Storyboard storyboard, FrameworkElement page,
        double fromOpacity, double toOpacity, double fromX, double toX)
    {
        page.Opacity = fromOpacity;
        var transform = new TranslateTransform { X = fromX };
        page.RenderTransform = transform;
        var opacity = new DoubleAnimation
        {
            From = fromOpacity, To = toOpacity, Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var translation = new DoubleAnimation
        {
            From = fromX, To = toX, Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacity, page);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(translation, transform);
        Storyboard.SetTargetProperty(translation, "X");
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(translation);
    }

    /// <summary>清除转场的临时位移和透明度并恢复交互，用于完成转场或离开下载模块。</summary>
    private static void ResetPage(FrameworkElement page)
    {
        page.Opacity = 1;
        page.RenderTransform = null;
        page.IsHitTestVisible = true;
    }

    /// <summary>页面卸载时取消在途请求，停止无限循环动画。</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e) => SetActive(false);

    /// <summary>失败后按当前设置重新请求，并从旋转状态重新开始。</summary>
    private void RetryRequested(object? sender, EventArgs e) => _ = LoadCategoryAsync();

    /// <summary>切换分类时重置游戏筛选并播放正文转场；资源目录接收独立分类，仅当前可见目录保持请求。</summary>
    private void ContentSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (LoadStatus is null || sender.SelectedItem?.Tag is not string category) return;
        _showTasks = false;
        if (TaskSelector is not null) TaskSelector.SelectedItem = null;
        if (TasksContent is not null) TasksContent.Visibility = Visibility.Collapsed;
        _category = category;
        bool game = category == "game";
        SearchControls.Visibility = ContentGrid.Visibility = game ? Visibility.Visible : Visibility.Collapsed;
        ResourcesContent.Visibility = game ? Visibility.Collapsed : Visibility.Visible;
        if (!game) ResourcesContent.SetCategory(category);
        ResourcesContent.SetActive(_active && !game);
        SearchBox.Text = "";
        TypeFilter.SelectedIndex = 1;
        if (_active) _ = LoadCategoryAsync();
        TabBody.PlayTransition();
    }

    /// <summary>文本变化（含清除按钮）时即时过滤已加载条目。</summary>
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    /// <summary>类型变化时重新过滤，愚人节类型已在清单解析时确定。</summary>
    private void TypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    /// <summary>撤销上一轮请求的界面所有权，并清理动画；异步方法负责释放自己的令牌。</summary>
    private void CancelLoad()
    {
        _request?.Cancel();
        _request = null;
        _transition?.Stop();
        LoadStatus.Stop();
    }

    /// <summary>游戏分类获取真实清单，资源分类交给独立目录；取消不显示错误，网络失败提供动画反馈和重试。</summary>
    private async Task LoadCategoryAsync()
    {
        CancelLoad();
        if (_category != "game")
        {
            ResourcesContent.SetActive(_active && !_showTasks);
            return;
        }
        using var request = new CancellationTokenSource();
        _request = request;
        var token = request.Token;
        _entries = Array.Empty<DownloadEntry>();
        _busy = true;
        EntryList.ItemsSource = null;
        EntryList.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        LoadStatus.Visibility = Visibility.Visible;
        LoadStatus.Opacity = 1;
        try
        {
            LoadStatus.Start();
            var entries = await _service.FetchAsync(_settings?.Current.VersionSource ??
                SourceStrategy.OfficialWithFallback, token);
            token.ThrowIfCancellationRequested();
            _entries = entries;
            Fade(LoadStatus, 1, 0);
            await Task.Delay(180, token);
            token.ThrowIfCancellationRequested();
            LoadStatus.Stop();
            LoadStatus.Visibility = Visibility.Collapsed;
            _busy = false;
            EntryList.Visibility = Visibility.Visible;
            ApplyFilter();
            Fade(EntryList, 0, 1);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (HttpRequestException ex)
        {
            if (token.IsCancellationRequested) return;
            LoadStatus.ShowError(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
        }
    }

    /// <summary>使用原生缓出透明度动画衔接状态与列表，不阻塞界面线程。</summary>
    private void Fade(UIElement target, double from, double to)
    {
        _transition?.Stop();
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        _transition = new Storyboard();
        _transition.Children.Add(animation);
        _transition.Begin();
    }

    /// <summary>按标题和类型筛选，未选中类型时默认正式版；加载与失败期间不展示空结果提示。</summary>
    private void ApplyFilter()
    {
        if (EntryList is null || EmptyHint is null || SearchBox is null || TypeFilter is null || _busy) return;
        string query = SearchBox.Text.Trim();
        string type = (TypeFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "正式版";
        var filtered = _entries.Where(entry =>
            (type == "全部类型" || entry.Kind == type) &&
            (query.Length == 0 || entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        EntryList.ItemsSource = filtered;
        EmptyHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
