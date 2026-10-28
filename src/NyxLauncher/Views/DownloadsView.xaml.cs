using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>游戏页获取真实清单；其他分类暂保留演示，支持取消、重试和筛选。</summary>
public sealed partial class DownloadsView : UserControl
{
    private IReadOnlyList<DownloadEntry> _entries = Array.Empty<DownloadEntry>();
    private readonly VersionCatalogService _service = new();
    private DownloadSettingsStore? _settings;
    private CancellationTokenSource? _request;
    private Storyboard? _transition;
    private string _category = "game";
    private bool _active;
    private bool _busy;

    /// <summary>创建下载页并注册重试和卸载清理；尚未打开下载页时不发网络请求。</summary>
    public DownloadsView()
    {
        InitializeComponent();
        LoadStatus.RetryRequested += RetryRequested;
        Unloaded += View_Unloaded;
    }

    /// <summary>注入主页面共用设置，使重新进入下载页立即采用最新源策略。</summary>
    public void Configure(DownloadSettingsStore settings) => _settings = settings;

    /// <summary>页面显示时获取当前分类；离开时取消请求与动画，避免后台结果污染界面。</summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active) _ = LoadCategoryAsync();
        else CancelLoad();
    }

    /// <summary>页面卸载时取消在途请求，停止无限循环动画。</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e) => SetActive(false);

    /// <summary>失败后按当前设置重新请求，并从旋转状态重新开始。</summary>
    private void RetryRequested(object? sender, EventArgs e) => _ = LoadCategoryAsync();

    /// <summary>切换分类，重置过滤条件并取消旧分类请求。</summary>
    private void ContentSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (LoadStatus is null || sender.SelectedItem?.Tag is not string category) return;
        _category = category;
        SearchBox.PlaceholderText = DownloadCatalog.GetSearchHint(category);
        SearchBox.Text = "";
        TypeFilter.SelectedIndex = 0;
        if (_active) _ = LoadCategoryAsync();
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

    /// <summary>获取真实游戏清单或本地演示；取消不显示错误，网络失败提供动画反馈和重试。</summary>
    private async Task LoadCategoryAsync()
    {
        CancelLoad();
        using var request = new CancellationTokenSource();
        _request = request;
        var token = request.Token;
        _entries = Array.Empty<DownloadEntry>();
        _busy = true;
        EntryList.ItemsSource = null;
        EntryList.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        DemoHint.Visibility = _category == "game" ? Visibility.Collapsed : Visibility.Visible;
        LoadStatus.Visibility = _category == "game" ? Visibility.Visible : Visibility.Collapsed;
        LoadStatus.Opacity = 1;
        try
        {
            if (_category == "game")
            {
                LoadStatus.Start();
                var entries = await _service.FetchAsync(_settings?.Current.VersionSource ??
                    SourceStrategy.OfficialWithFallback, token);
                token.ThrowIfCancellationRequested();
                _entries = entries;
                Fade(LoadStatus, 1, 0);
                await Task.Delay(180, token);
            }
            else _entries = DownloadCatalog.GetEntries(_category);
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

    /// <summary>按标题和类型筛选；加载与失败期间不展示“没有匹配内容”。</summary>
    private void ApplyFilter()
    {
        if (EntryList is null || EmptyHint is null || SearchBox is null || TypeFilter is null || _busy) return;
        string query = SearchBox.Text.Trim();
        string type = (TypeFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部类型";
        var filtered = _entries.Where(entry =>
            (type == "全部类型" || entry.Kind == type) &&
            (query.Length == 0 || entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        EntryList.ItemsSource = filtered;
        EmptyHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
