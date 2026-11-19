using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher;

/// <summary>承载主导航、真实版本目录管理、下载配置及账户菜单。</summary>
public sealed partial class MainPage : Page
{
    private readonly DownloadTaskManager _tasks;
    private readonly AccountStore _accounts;
    private readonly GameFolderStore _folders;
    private readonly DownloadSettingsStore _settings;
    private readonly JavaRuntimeStore _java;
    private bool _importing;
    private string _currentPage = "home";

    /// <summary>下载或游戏启动期间避免主动压缩托管堆，减少后台业务被全量回收暂停的机会。</summary>
    public bool HasMemorySensitiveWork => _tasks.Tasks.Any(task => task.IsBusy) || HomeContent.IsLaunching || _importing ||
        MultiplayerContent?.IsBusy == true;

    /// <summary>后台回收前卸载已经隐藏的详情控件，不创建尚未访问的模块，不影响可见页面和共享任务。</summary>
    public void ReleaseUnusedViews()
    {
        DownloadsContent?.ReleaseUnusedDetails();
    }

    /// <summary>关闭时停止下载、游戏和已创建页面的后台工作，包括陶瓦进程。</summary>
    public Task StopDownloadsAsync() => Task.WhenAll(_tasks.ShutdownAsync(), HomeContent.StopAsync(),
        AccountContent?.StopAsync() ?? Task.CompletedTask, SettingsContent?.StopAsync() ?? Task.CompletedTask,
        WardrobeContent?.StopAsync() ?? Task.CompletedTask, MultiplayerContent?.StopAsync() ?? Task.CompletedTask);
    /// <summary>只创建首页及共享业务状态，其他模块首次导航时才创建；后台安装完成仍可刷新首页。</summary>
    public MainPage()
    {
        InitializeComponent();
        var settings = new DownloadSettingsStore();
        var folders = new GameFolderStore();
        _folders = folders;
        _settings = settings;
        _accounts = new AccountStore();
        var tasks = new DownloadTaskManager(settings, action => DispatcherQueue.TryEnqueue(() => action()));
        _tasks = tasks;
        _java = new JavaRuntimeStore();
        HomeContent.Configure(folders, settings, _accounts, _java);
        HomeContent.ManageRequested += Home_ManageRequested;
        HomeContent.CrashAnalysisChanged += (_, analyzing) => CrashToast.IsOpen = analyzing;
        _accounts.Changed += (_, _) => RefreshAccount();
        RefreshAccount();
        tasks.InstallationCompleted += (_, _) =>
        {
            if (VersionsScroll?.Visibility == Visibility.Visible) VersionsContent.SetActive(true);
            HomeContent.Refresh();
        };
        Navigation.SelectedItem = HomeItem;
    }

    /// <summary>首次进入指定模块时显式加载其 XAML 并注入共享服务；每个模块只配置一次，已创建页面保留筛选和后台业务状态。</summary>
    private void EnsurePage(string page)
    {
        if ((page is "multiplayer" or "settings") && MultiplayerContent is null)
        {
            FindName(nameof(MultiplayerContent));
            MultiplayerContent!.Configure(() => HomeContent.IsGameRunning, () => _accounts.Selected?.Name);
        }
        if (page == "servers" && ServersContent is null)
        {
            FindName(nameof(ServersContent));
            ServersContent!.Configure(_folders, LaunchServerAsync);
        }
        if (page == "wardrobe" && WardrobeContent is null)
        {
            FindName(nameof(WardrobeContent));
            WardrobeContent!.Configure(_accounts);
        }
        if (page == "downloads" && DownloadsContent is null)
        {
            FindName(nameof(DownloadsContent));
            DownloadsContent!.Configure(_settings, _tasks, _folders);
        }
        else if (page == "versions" && VersionsScroll is null)
        {
            FindName(nameof(VersionsScroll));
            VersionsContent.Configure(_folders, _settings, () => HomeContent.IsLaunching || _tasks.HasActiveVersionTasks);
            VersionsContent.ConfigureModpacks(_tasks);
            VersionsContent.InstallRequested += Versions_InstallRequested;
            VersionsContent.ModDownloadRequested += Versions_ModDownloadRequested;
            VersionsContent.ModFilesRequested += Versions_ModFilesRequested;
        }
        else if (page == "settings" && SettingsScroll is null)
        {
            FindName(nameof(SettingsScroll));
            SettingsContent.Configure(_settings);
            SettingsContent.ConfigureJava(_java, _tasks);
            SettingsContent.ConfigureMemory(_folders, () => HomeContent.SelectedVersion);
            SettingsContent.ConfigureMultiplayer(MultiplayerContent!);
        }
        else if (page == "account" && AccountContent is null)
        {
            FindName(nameof(AccountContent));
            AccountContent!.Configure(_accounts, new AccountRegionPolicy());
        }
    }

    /// <summary>检查共享游戏会话及账户，切回首页展示服务器启动进度；无法启动时向服务器页返回原因。</summary>
    private Task LaunchServerAsync(ServerVersion version, ServerEndpoint endpoint)
    {
        if (HomeContent.IsLaunching) throw new InvalidOperationException("请先停止当前游戏。");
        if (_accounts.Selected is not GameAccount account) throw new InvalidOperationException("请先添加并选择账户。");
        Navigation.SelectedItem = HomeItem;
        return HomeContent.StartGameAsync(version.Folder, version.Version, account, endpoint);
    }

    /// <summary>模组管理下载入口先选中模组分类，再切换主导航，避免加载游戏列表。</summary>
    private void Versions_ModDownloadRequested(object? sender, EventArgs e)
    {
        EnsurePage("downloads");
        DownloadsContent.ShowMods();
        Navigation.SelectedItem = DownloadsItem;
    }

    /// <summary>从已安装模组详情进入下载页，并携带精确游戏版本与加载器直接显示该项目文件列表。</summary>
    private async void Versions_ModFilesRequested(object? sender, (ResourceProject Project, InstalledVersion Version) request)
    {
        EnsurePage("downloads");
        DownloadsContent.ShowMods();
        Navigation.SelectedItem = DownloadsItem;
        await DownloadsContent.ShowModFilesAsync(request.Project, request.Version);
    }

    /// <summary>主窗口接受认证文本和本地资源文件的复制拖拽；放下后验证格式并要求用户确认。</summary>
    private void AuthServer_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text) ||
            e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    /// <summary>读取资源文件列表或认证文本后立即结束系统拖放，再打开相应确认弹窗；拖放延迟不覆盖网络请求或弹窗生命周期，读取失败同样释放。</summary>
    private async void AuthServer_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_importing) return;
        _importing = true;
        try
        {
            string text = "";
            var paths = new List<string>();
            var deferral = e.GetDeferral();
            try
            {
                if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                {
                    var files = await e.DataView.GetStorageItemsAsync();
                    foreach (var item in files)
                        if (item is Windows.Storage.StorageFile file) paths.Add(file.Path);
                        else throw new IOException("请拖入资源文件，不支持文件夹。");
                }
                else if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                    text = await e.DataView.GetTextAsync();
            }
            finally { deferral.Complete(); }
            if (paths.Count > 0)
            {
                foreach (string path in paths) await ImportResourceAsync(path);
                return;
            }
            if (!text.StartsWith("authlib-injector:yggdrasil-server:", StringComparison.OrdinalIgnoreCase)) return;
            Navigation.SelectedItem = null; ShowPage("account");
            await AccountContent.ImportServerAsync(text);
        }
        catch (Exception ex)
        {
            await new ContentDialog { XamlRoot = XamlRoot, Title = "导入失败", Content = ex.Message, CloseButtonText = "确定" }.ShowThemedAsync();
        }
        finally { _importing = false; }
    }

    /// <summary>把首页视口尺寸交给首页布局，窗口缩放时同步调整图片和启动控件，不影响其他页面。</summary>
    private void Home_SizeChanged(object sender, SizeChangedEventArgs e) =>
        HomeContent?.ResizeLayout(e.NewSize.Width, e.NewSize.Height);

    /// <summary>从首页切换到版本管理并直接打开所选版本详情，保持主导航选中态一致。</summary>
    private void Home_ManageRequested(object? sender, InstalledVersion version)
    {
        Navigation.SelectedItem = VersionsItem;
        VersionsContent.OpenDetails(version);
    }

    /// <summary>账户保存后同步侧栏名称与共用头像控件；微软真实皮肤异步加载，空账户显示中性图标。</summary>
    private void RefreshAccount()
    {
        var account = _accounts.Selected;
        AccountName.Text = account?.Name ?? "未添加账户";
        AccountAvatar.Account = account;
        AccountAvatar.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyAccountIcon.Visibility = account is null ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AccountMenuButton, AccountName.Text + "，打开账户菜单");
    }

    /// <summary>从版本管理进入游戏下载列表，更新主导航选中态并重置下载分类。</summary>
    private void Versions_InstallRequested(object? sender, EventArgs e)
    {
        EnsurePage("downloads");
        DownloadsContent.ShowGames();
        Navigation.SelectedItem = DownloadsItem;
    }

    /// <summary>根据选中导航项的标签切换可见页面；清除选择时保留当前内容。</summary>
    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string page })
        {
            ShowPage(page);
        }
    }

    /// <summary>从底部弹层打开账户管理，并清除主导航的选中状态。</summary>
    private void Account_Click(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedItem = null;
        ShowPage("account");
    }

    /// <summary>从底部弹层打开设置页，并清除主导航的选中状态。</summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedItem = null;
        ShowPage("settings");
    }

    /// <summary>账户菜单使用独立的不透明主题底色，并在弹出前同步窗口实际明暗，避免弹层回退到系统主题。</summary>
    private void AccountMenu_Opening(object sender, object args)
    {
        var style = new Style(typeof(MenuFlyoutPresenter))
        {
            BasedOn = (Style)Application.Current.Resources["OpaqueMenuPresenterStyle"]
        };
        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ActualTheme));
        ((MenuFlyout)sender).MenuFlyoutPresenterStyle = style;
    }

    /// <summary>切换侧栏展开状态，供窗口标题栏调用，不改变当前选中页面。</summary>
    public void ToggleNavigationPane() => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    /// <summary>展开左侧栏时恢复账户名称和下拉标记；保留当前页面及账户菜单实例。</summary>
    private void Navigation_PaneOpening(NavigationView sender, object args) => UpdateAccountPane(true);

    /// <summary>收起左侧栏时仅显示账户图标，使窄侧栏中的账户与设置入口仍可点击。</summary>
    private void Navigation_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args) => UpdateAccountPane(false);

    /// <summary>按侧栏状态切换账户入口布局；收起时移除隐藏列间距并居中头像，菜单向窗口内侧展开，展开时恢复名称布局。</summary>
    private void UpdateAccountPane(bool open)
    {
        if (AccountName is null || AccountChevron is null) return;
        foreach (var item in new[] { HomeItem, VersionsItem, DownloadsItem, WardrobeItem, ServersItem, MultiplayerItem })
            item.Margin = open ? new Thickness(8, 2, 8, 2) : new Thickness(0, 2, 0, 2);
        AccountName.Visibility = AccountChevron.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AccountMenuLayout.ColumnSpacing = open ? 10 : 0;
        AccountMenuLayout.ColumnDefinitions[1].Width = open ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        AccountMenuLayout.ColumnDefinitions[2].Width = open ? GridLength.Auto : new GridLength(0);
        AccountMenuButton.Width = open ? double.NaN : Navigation.CompactPaneLength;
        AccountMenuButton.HorizontalContentAlignment = open ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        AccountMenuButton.Flyout.Placement = open
            ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft
            : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedBottom;
        AccountMenuButton.Padding = new Thickness(open ? 10 : 0);
        AccountMenuButton.Margin = open ? new Thickness(8, 8, 8, 12) : new Thickness(0, 8, 0, 12);
    }

    /// <summary>页面卸载时停止转场，恢复内容层的默认透明度和位置，避免重新挂载时残留动画状态。</summary>
    private void Page_Unloaded(object sender, RoutedEventArgs e) => PageEntrance.Stop();

    /// <summary>非首页显示玻璃面板，首页隐藏面板让背景场景完整呈现；面板以合成线程淡入淡出，系统关闭动画时直接切换。</summary>
    private void UpdateSheet(bool visible)
    {
        SheetContent.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Sheet.OpacityTransition = IsLoaded && Themes.Motion.Enabled ? new ScalarTransition { Duration = Themes.Motion.Normal } : null;
        Sheet.Opacity = visible ? 1 : 0;
    }

    /// <summary>按需创建并激活目标页；页面变化时以淡入和轻微上移过渡，快速导航替换在途动画，重复进入不重播；系统关闭动画或首次加载时直接显示。</summary>
    /// <param name="page">由本页导航提供的 home、versions、downloads、wardrobe、account 或 settings 标签。</param>
    private void ShowPage(string page)
    {
        EnsurePage(page);
        bool changed = _currentPage != page;
        if (changed) PageEntrance.Stop();
        _currentPage = page;
        UpdateSheet(page != "home");
        HomeScroll.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        if (VersionsScroll is not null) VersionsScroll.Visibility = page == "versions" ? Visibility.Visible : Visibility.Collapsed;
        if (DownloadsContent is not null) DownloadsContent.Visibility = page == "downloads" ? Visibility.Visible : Visibility.Collapsed;
        if (AccountContent is not null) AccountContent.Visibility = page == "account" ? Visibility.Visible : Visibility.Collapsed;
        if (WardrobeContent is not null) WardrobeContent.Visibility = page == "wardrobe" ? Visibility.Visible : Visibility.Collapsed;
        if (ServersContent is not null) ServersContent.Visibility = page == "servers" ? Visibility.Visible : Visibility.Collapsed;
        if (MultiplayerContent is not null) MultiplayerContent.Visibility = page == "multiplayer" ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsScroll is not null) SettingsScroll.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        DownloadsContent?.SetActive(page == "downloads");
        VersionsContent?.SetActive(page == "versions");
        SettingsContent?.SetActive(page == "settings");
        WardrobeContent?.SetActive(page == "wardrobe");
        ServersContent?.SetActive(page == "servers");
        MultiplayerContent?.SetActive(page == "multiplayer");
        if (page == "home") HomeContent.Refresh();
        if (changed && IsLoaded && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            PageEntrance.Begin();
    }
}
