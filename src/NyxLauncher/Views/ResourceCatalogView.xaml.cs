using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NyxLauncher.Services;
using Windows.System;

namespace NyxLauncher.Views;

/// <summary>资源分类共用的搜索、筛选、卡片及分页；请求随页面生命周期取消，迟到结果不覆盖新页面。</summary>
public sealed partial class ResourceCatalogView : UserControl
{
    private readonly ModrinthCatalogService _service = new();
    private IReadOnlyList<ResourceGameVersion>? _versions;
    private IReadOnlyList<ResourceCategory>? _categories;
    private CancellationTokenSource? _request;
    private string _category = "mod";
    private string _query = "";
    private string _gameVersion = "";
    private int _offset;
    private bool _active;
    private bool _updating;
    public event EventHandler<ResourceProject>? ModRequested;
    /// <summary>已提交的精确版本筛选，供详情置顶；不读取编辑框尚未确认的草稿。</summary>
    public string SelectedGameVersion => _gameVersion;
    /// <summary>当前加载器的平台标识，空字符串表示全部，供详情置顶和标注。</summary>
    public string SelectedLoader => (LoaderFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    /// <summary>绑定共享版本列表源设置；每次请求读取最新值，不新增设置项。</summary>
    public void Configure(DownloadSettingsStore settings) => _service.Source = () => settings.Current.VersionSource;

    /// <summary>五类资源卡片进入共用下载详情，由资源类别决定版本、加载器和文件选择步骤。</summary>
    private void ProjectList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_active && e.ClickedItem is ResourceProject project)
            ModRequested?.Invoke(this, project);
    }

    /// <summary>初始化资源列表及“全部”版本项，绑定失败重试和卸载取消；未显示时不访问平台。</summary>
    public ResourceCatalogView()
    {
        InitializeComponent();
        _updating = true;
        GameVersionFilter.ItemsSource = new[] { "全部" };
        GameVersionFilter.SelectedIndex = 0;
        ResetCategoryFilter();
        _updating = false;
        LoadStatus.RetryRequested += RetryRequested;
        Unloaded += View_Unloaded;
    }

    /// <summary>切换资源类别时清空关键词及已提交版本，重置选择和手动输入文本，模组和整合包显示加载器；显示后才请求第一页。</summary>
    public void SetCategory(string category)
    {
        if (_category == category) return;
        CancelLoad();
        _category = category;
        _updating = true;
        SearchBox.Text = _query = "";
        _gameVersion = "";
        SearchBox.PlaceholderText = category switch
        { "modpack" => "搜索整合包", "resourcepack" => "搜索资源包", "datapack" => "搜索数据包", "shader" => "搜索光影", _ => "搜索模组" };
        GameVersionFilter.SelectedIndex = LoaderFilter.SelectedIndex = SourceFilter.SelectedIndex = 0;
        GameVersionFilter.Text = "全部";
        ResetCategoryFilter();
        LoaderControls.Visibility = (category is "mod" or "modpack") ? Visibility.Visible : Visibility.Collapsed;
        _offset = 0;
        _updating = false;
        if (_active) _ = LoadAsync();
    }

    /// <summary>显示时重新获取资源，离开分类或模块时取消搜索并释放结果卡片；保留筛选条件，不影响下载任务。</summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active) _ = LoadAsync();
        else { CancelLoad(); ProjectList.ItemsSource = null; }
    }

    /// <summary>点击独立搜索按钮后提交输入并回到第一页；输入过程不发请求。</summary>
    private void Search_Click(object sender, RoutedEventArgs e) => SubmitSearch();

    /// <summary>允许按 Enter 提交同一搜索动作，避免键盘用户依赖鼠标按钮。</summary>
    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        SubmitSearch();
    }

    /// <summary>保存本次明确提交的关键词；后续筛选和翻页使用相同关键词，不混入尚未搜索的草稿。</summary>
    private void SubmitSearch()
    {
        _query = SearchBox.Text.Trim();
        _offset = 0;
        if (_active) _ = LoadAsync();
    }

    /// <summary>用户更改筛选时立即查询第一页；初始化和分类重置产生的事件不重复请求。</summary>
    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || !_active) return;
        _offset = 0;
        _ = LoadAsync();
    }

    /// <summary>提交正式版选择或控件确认的自定义字符串；输入过程引起的选中项清空不触发请求。</summary>
    private void GameVersionFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && GameVersionFilter.SelectedItem is string version) CommitGameVersion(version);
    }

    /// <summary>按 Enter 或离开输入框时提交自定义 ID；沿用原生字符串编辑行为，不向正式版候选列表添加手动值。</summary>
    private void GameVersionFilter_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        if (_updating) return;
        CommitGameVersion(args.Text);
    }

    /// <summary>保存原始版本 ID 并查询第一页；相同提交不重复请求，其他筛选、重试及翻页均沿用该值。</summary>
    private void CommitGameVersion(string value)
    {
        value = value.Trim();
        value = value == "全部" ? "" : value;
        if (_gameVersion == value) return;
        _gameVersion = value;
        _offset = 0;
        if (_active) _ = LoadAsync();
    }

    /// <summary>返回前一页，按钮在第一页和请求期间禁用，偏移始终保持非负。</summary>
    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        _offset = Math.Max(0, _offset - ModrinthCatalogService.PageSize);
        _ = LoadAsync();
    }

    /// <summary>请求下一页；按钮仅在平台总数表明仍有结果时可用。</summary>
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _offset += ModrinthCatalogService.PageSize;
        _ = LoadAsync();
    }

    /// <summary>失败后重新执行当前页请求，同时补取此前失败的 Minecraft 版本列表。</summary>
    private void RetryRequested(object? sender, EventArgs e) => _ = LoadAsync();

    /// <summary>页面卸载时释放异步请求的界面所有权，避免后台更新已隐藏的视图。</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e) => SetActive(false);

    /// <summary>取消本轮请求并停止状态动画；异步请求自行释放令牌，旧结果无法重新显示。</summary>
    private void CancelLoad()
    {
        _request?.Cancel();
        _request = null;
        LoadStatus.Stop();
    }

    /// <summary>按当前资源类别展示中文类型；数据包使用平台模组标签，保留“全部”且不复用其他分类选择。</summary>
    private void ResetCategoryFilter()
    {
        string type = _category == "datapack" ? "mod" : _category;
        CategoryFilter.ItemsSource = new[] { new ResourceCategory("", "全部", type) }
            .Concat(_categories?.Where(x => x.ProjectType == type) ?? []).ToArray();
        CategoryFilter.SelectedIndex = 0;
    }

    /// <summary>加载当前筛选和页码，首次准备真实版本及中文类型；切页先清空旧卡片以取消图标，失败复用游戏页重试状态。</summary>
    private async Task LoadAsync()
    {
        CancelLoad();
        using var request = new CancellationTokenSource();
        _request = request;
        var token = request.Token;
        string category = _category;
        string game = _gameVersion;
        string loader = (LoaderFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        string resourceType = (CategoryFilter.SelectedItem as ResourceCategory)?.Id ?? "";
        int offset = _offset;
        ProjectList.ItemsSource = null;
        ProjectList.Visibility = EmptyHint.Visibility = Pagination.Visibility = Visibility.Collapsed;
        LoadStatus.Visibility = Visibility.Visible;
        LoadStatus.Start("资源");
        try
        {
            if (_versions is null)
            {
                var versions = await _service.FetchGameVersionsAsync(token);
                token.ThrowIfCancellationRequested();
                _versions = versions;
                _updating = true;
                GameVersionFilter.ItemsSource = new[] { "全部" }.Concat(versions.Select(x => x.Id)).ToArray();
                GameVersionFilter.SelectedItem = game.Length == 0 ? "全部" : game;
                GameVersionFilter.Text = game.Length == 0 ? "全部" : game;
                _updating = false;
            }
            if (_categories is null)
            {
                var categories = await _service.FetchCategoriesAsync(token);
                token.ThrowIfCancellationRequested();
                _categories = categories;
                _updating = true;
                ResetCategoryFilter();
                _updating = false;
            }
            var page = await _service.SearchAsync(category, _query, game, loader, offset, token, resourceType);
            token.ThrowIfCancellationRequested();
            LoadStatus.Stop();
            LoadStatus.Visibility = Visibility.Collapsed;
            ProjectList.ItemsSource = page.Projects;
            ProjectList.Visibility = Visibility.Visible;
            if (page.Projects.Count > 0) ProjectList.ScrollIntoView(page.Projects[0]);
            EmptyHint.Visibility = page.Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Pagination.Visibility = page.Total > ModrinthCatalogService.PageSize ? Visibility.Visible : Visibility.Collapsed;
            PreviousButton.IsEnabled = offset > 0;
            NextButton.IsEnabled = offset + ModrinthCatalogService.PageSize < page.Total;
            PageText.Text = $"{offset / ModrinthCatalogService.PageSize + 1} / {Math.Max(1, (int)Math.Ceiling(page.Total / (double)ModrinthCatalogService.PageSize))}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (HttpRequestException ex)
        {
            if (!token.IsCancellationRequested) LoadStatus.ShowError(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
        }
    }

}
