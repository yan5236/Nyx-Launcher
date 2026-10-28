using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher;

/// <summary>承载主导航、真实版本目录管理、下载配置及账户菜单。</summary>
public sealed partial class MainPage : Page
{
    private readonly DownloadTaskManager _tasks;

    /// <summary>供窗口关闭流程等待下载取消及临时资源清理，不删除已完成版本。</summary>
    public Task StopDownloadsAsync() => _tasks.ShutdownAsync();
    /// <summary>加载页面，共用下载设置并初始化游戏目录，连接安装新版本导航后选中首页。</summary>
    public MainPage()
    {
        InitializeComponent();
        var settings = new DownloadSettingsStore();
        var folders = new GameFolderStore();
        var tasks = new DownloadTaskManager(settings, action => DispatcherQueue.TryEnqueue(() => action()));
        _tasks = tasks;
        DownloadsContent.Configure(settings, tasks, folders);
        SettingsContent.Configure(settings);
        VersionsContent.Configure(folders);
        HomeContent.Configure(folders, settings);
        tasks.InstallationCompleted += (_, _) =>
        {
            if (VersionsScroll.Visibility == Visibility.Visible) VersionsContent.SetActive(true);
            HomeContent.Refresh();
        };
        VersionsContent.InstallRequested += Versions_InstallRequested;
        Navigation.SelectedItem = HomeItem;
    }

    /// <summary>从版本管理进入游戏下载列表，更新主导航选中态并重置下载分类。</summary>
    private void Versions_InstallRequested(object? sender, EventArgs e)
    {
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

    /// <summary>从底部弹层打开账户原型，并清除主导航的选中状态。</summary>
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

    /// <summary>显示指定页面；版本管理重新扫描本地目录，下载页加载清单，离开时取消各自在途工作。</summary>
    /// <param name="page">由本页导航提供的 home、versions、downloads、account 或 settings 标签。</param>
    private void ShowPage(string page)
    {
        HomeScroll.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        VersionsScroll.Visibility = page == "versions" ? Visibility.Visible : Visibility.Collapsed;
        DownloadsContent.Visibility = page == "downloads" ? Visibility.Visible : Visibility.Collapsed;
        AccountScroll.Visibility = page == "account" ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        DownloadsContent.SetActive(page == "downloads");
        VersionsContent.SetActive(page == "versions");
        if (page == "home") HomeContent.Refresh();
    }
}
