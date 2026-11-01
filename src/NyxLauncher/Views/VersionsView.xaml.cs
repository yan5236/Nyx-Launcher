using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>管理已登记游戏目录中的真实版本，提供搜索、详情、回收站删除及下载导航。</summary>
public sealed partial class VersionsView : UserControl
{
    private GameFolderStore? _folders;
    private Func<bool>? _mutationBlocked;
    private IReadOnlyList<InstalledVersion> _versions = Array.Empty<InstalledVersion>();
    private CancellationTokenSource? _scan;
    private bool _dialogOpen;
    private Storyboard? _pageTransition;
    public event EventHandler? InstallRequested;
    public event EventHandler? ModDownloadRequested;
    public event EventHandler<(ResourceProject Project, InstalledVersion Version)>? ModFilesRequested;

    /// <summary>加载页面并连接详情返回与删除事件；业务目录由主页面在启动时注入。</summary>
    public VersionsView()
    {
        InitializeComponent();
        Details.BackRequested += Details_BackRequested;
        Details.DeleteRequested += Details_DeleteRequested;
        Details.ModDownloadRequested += (_, _) => ModDownloadRequested?.Invoke(this, EventArgs.Empty);
        Details.ModFilesRequested += (_, request) => ModFilesRequested?.Invoke(this, request);
        Unloaded += View_Unloaded;
    }

    /// <summary>共享目录、隔离偏好与改名/删除保护；保存状态检查委托，在实际修改前重新确认，初始化时显示目录错误。</summary>
    public void Configure(GameFolderStore folders, DownloadSettingsStore settings, Func<bool> mutationBlocked)
    {
        _folders = folders;
        _mutationBlocked = mutationBlocked;
        Details.Configure(folders, settings, mutationBlocked);
        RefreshPath();
        ShowError(folders.LoadError);
    }

    /// <summary>进入时重新扫描磁盘；离开时取消扫描和转场并恢复列表，旧回调不能覆盖后续页面。</summary>
    public void SetActive(bool active)
    {
        _scan?.Cancel();
        if (active) _ = RefreshAsync();
        else
        {
            StopPageTransition();
            Details.SetActive(false);
            Details.Visibility = Visibility.Collapsed;
            Catalog.Visibility = Visibility.Visible;
        }
    }

    /// <summary>更新可截断的路径按钮，悬停可查看完整路径；没有当前目录时提示用户选择。</summary>
    private void RefreshPath()
    {
        FolderPath.Text = _folders?.Selected ?? "选择文件夹";
        ToolTipService.SetToolTip(FolderButton, FolderPath.Text);
    }

    /// <summary>在后台读取当前目录，保留搜索条件；失败显示原因，空目录与无搜索结果分别提示。</summary>
    private async Task RefreshAsync()
    {
        _scan?.Cancel();
        using var request = new CancellationTokenSource();
        _scan = request;
        RefreshPath();
        _versions = Array.Empty<InstalledVersion>();
        ApplyFilter();
        ShowError(_folders?.LoadError);
        string? folder = _folders?.Selected;
        ScanProgress.IsActive = folder is not null;
        ScanProgress.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        if (folder is not null) Empty.Visibility = Visibility.Collapsed;
        try
        {
            if (folder is not null)
            {
                var versions = await Task.Run(() => InstalledVersionService.Scan(folder, request.Token), request.Token);
                request.Token.ThrowIfCancellationRequested();
                _versions = versions;
            }
            request.Token.ThrowIfCancellationRequested();
            ApplyFilter();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!request.IsCancellationRequested) { ShowError(ex.Message); Empty.Visibility = Visibility.Collapsed; } }
        finally
        {
            if (ReferenceEquals(_scan, request))
            {
                _scan = null;
                ScanProgress.IsActive = false;
                ScanProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>模态显示文件夹管理；选择成功立即关闭，关闭后同步已保存路径并刷新真实版本。</summary>
    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_folders is null || _dialogOpen) return;
        _dialogOpen = true;
        try { await new GameFolderDialog(XamlRoot, _folders).ShowThemedAsync(); }
        finally { _dialogOpen = false; FolderButton.Focus(FocusState.Programmatic); }
        await RefreshAsync();
    }

    /// <summary>请求主导航打开游戏下载分类，不在版本管理页启动网络下载。</summary>
    private void Install_Click(object sender, RoutedEventArgs e) => InstallRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>对本次扫描结果即时搜索，输入或清空均不重新读取磁盘。</summary>
    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    /// <summary>按名称、游戏版本和加载器过滤；空选择、空目录及搜索无结果使用对应文案。</summary>
    private void ApplyFilter()
    {
        if (VersionList is null) return;
        string query = Search.Text.Trim();
        var filtered = _versions.Where(x => x.AccessibleName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        VersionList.ItemsSource = filtered;
        Empty.Text = _folders?.Selected is null ? "请选择游戏文件夹" : query.Length > 0 ? "没有匹配的版本" : "此文件夹中还没有版本";
        Empty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>点击卡片或键盘激活直接进入该版本详情；删除为独立按钮，不触发此入口。</summary>
    private void Version_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledVersion version }) return;
        OpenDetails(version);
    }

    /// <summary>供首页和列表共用的详情入口，展示真实元数据并向左淡出列表、从右淡入详情；转场期间忽略重复请求。</summary>
    public void OpenDetails(InstalledVersion version)
    {
        if (_pageTransition is not null || _dialogOpen) return;
        Details.Open(version);
        SwitchPage(true);
    }

    /// <summary>反向转场返回列表并重新读取磁盘，保留原搜索条件；忽略转场和确认弹窗期间的重复操作。</summary>
    private void Details_BackRequested(object? sender, EventArgs e)
    {
        if (_pageTransition is not null || _dialogOpen) return;
        SwitchPage(false);
        _ = RefreshAsync();
    }

    /// <summary>详情危险区使用当前版本对象发起共享删除流程，改名后仍显示并校验最新目录。</summary>
    private async void Details_DeleteRequested(object? sender, EventArgs e)
    {
        if (Details.Version is { } version) await DeleteAsync(version);
    }

    /// <summary>以 220 毫秒水平位移和淡入淡出切换列表与详情；进入向左、返回向右，系统关闭动画时直接切换。</summary>
    private void SwitchPage(bool entering)
    {
        StopPageTransition();
        Details.SetActive(entering);
        FrameworkElement outgoing = entering ? Catalog : Details;
        FrameworkElement incoming = entering ? Details : Catalog;
        incoming.Visibility = Visibility.Visible;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            outgoing.Visibility = Visibility.Collapsed;
            return;
        }
        outgoing.IsHitTestVisible = incoming.IsHitTestVisible = false;
        double distance = entering ? 32 : -32;
        var transition = new Storyboard();
        AddPageAnimation(transition, outgoing, 1, 0, 0, -distance);
        AddPageAnimation(transition, incoming, 0, 1, distance, 0);
        transition.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_pageTransition, transition)) return;
            outgoing.Visibility = Visibility.Collapsed;
            StopPageTransition();
        };
        _pageTransition = transition;
        transition.Begin();
    }

    /// <summary>将页面透明度与水平位移加入同一个缓出转场；调用方负责结束时恢复透明度、变换和交互。</summary>
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

    /// <summary>停止在途转场并清除两个页面的临时视觉状态，离开模块或卸载后不会遗留透明页面或失效点击。</summary>
    private void StopPageTransition()
    {
        _pageTransition?.Stop();
        _pageTransition = null;
        foreach (FrameworkElement page in new FrameworkElement[] { Catalog, Details })
        {
            page.Opacity = 1;
            page.RenderTransform = null;
            page.IsHitTestVisible = true;
        }
    }

    /// <summary>视图卸载时取消扫描和动画，恢复下次进入的列表状态。</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e) => SetActive(false);

    /// <summary>只处理行尾的删除按钮，删除前必须显示版本名与完整路径供用户确认。</summary>
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledVersion version }) await DeleteAsync(version);
    }

    /// <summary>确认后将单个版本目录送入回收站；弹窗前及异步校验后检查运行和安装状态，忙碌时报告错误，取消或失败保留页面，不回退永久删除。</summary>
    private async Task DeleteAsync(InstalledVersion version)
    {
        if (_dialogOpen || _pageTransition is not null || _folders?.Selected is not { } folder) return;
        _dialogOpen = true;
        try
        {
            if (_mutationBlocked?.Invoke() == true) throw new IOException("请先停止游戏并等待游戏或模组安装任务结束，再删除版本。");
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "删除版本“" + version.Name + "”？",
                Content = "将以下版本文件夹移入回收站，其中的独立存档、模组和设置也会一起移入。共享资源和其他版本不受影响。\n\n" + version.DirectoryPath,
                PrimaryButtonText = "移入回收站", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary) return;
            string target = await Task.Run(() => InstalledVersionService.GetDeletionTarget(folder, version));
            if (_mutationBlocked?.Invoke() == true) throw new IOException("请先停止游戏并等待游戏或模组安装任务结束，再删除版本。");
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(target,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            if (Details.Visibility == Visibility.Visible) SwitchPage(false);
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException or System.Runtime.InteropServices.COMException)
        {
            if (Details.Visibility == Visibility.Visible) Details.ShowError("未删除版本：" + ex.Message);
            else ShowError("未删除版本：" + ex.Message);
        }
        finally { _dialogOpen = false; }
    }

    /// <summary>以页面内错误条报告可恢复失败，避免静默丢失目录或版本状态。</summary>
    private void ShowError(string? error)
    {
        Status.Message = error ?? "";
        Status.IsOpen = error is not null;
    }
}
