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
    private bool _importing;

    /// <summary>窗口关闭时停止下载、游戏、Java 扫描及账户认证，等待后台回收，不删除已完成版本。</summary>
    public Task StopDownloadsAsync() => Task.WhenAll(_tasks.ShutdownAsync(), HomeContent.StopAsync(), AccountContent.StopAsync(), SettingsContent.StopAsync());
    /// <summary>加载页面，共享下载和 Java 状态，初始化目录，连接安装完成与首页导航后选中首页。</summary>
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
        DownloadsContent.Configure(settings, tasks, folders);
        SettingsContent.Configure(settings);
        var java = new JavaRuntimeStore();
        SettingsContent.ConfigureJava(java, tasks);
        VersionsContent.Configure(folders, settings, () => HomeContent.IsLaunching || tasks.HasActiveVersionTasks);
        VersionsContent.ConfigureModpacks(tasks);
        HomeContent.Configure(folders, settings, _accounts, java);
        SettingsContent.ConfigureMemory(folders, () => HomeContent.SelectedVersion);
        HomeContent.ManageRequested += Home_ManageRequested;
        HomeContent.CrashAnalysisChanged += (_, analyzing) => CrashToast.IsOpen = analyzing;
        AccountContent.Configure(_accounts);
        _accounts.Changed += (_, _) => RefreshAccount();
        RefreshAccount();
        tasks.InstallationCompleted += (_, _) =>
        {
            if (VersionsScroll.Visibility == Visibility.Visible) VersionsContent.SetActive(true);
            HomeContent.Refresh();
        };
        VersionsContent.InstallRequested += Versions_InstallRequested;
        VersionsContent.ModDownloadRequested += Versions_ModDownloadRequested;
        Navigation.SelectedItem = HomeItem;
    }

    /// <summary>模组管理下载入口先选中模组分类，再切换主导航，避免加载游戏列表。</summary>
    private void Versions_ModDownloadRequested(object? sender, EventArgs e)
    {
        DownloadsContent.ShowMods();
        Navigation.SelectedItem = DownloadsItem;
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
            await new ContentDialog { XamlRoot = XamlRoot, Title = "导入失败", Content = ex.Message, CloseButtonText = "确定" }.ShowAsync();
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

    /// <summary>显示指定页面；版本管理扫描目录，下载页加载清单，设置页激活 Java 管理，首页刷新选择。</summary>
    /// <param name="page">由本页导航提供的 home、versions、downloads、account 或 settings 标签。</param>
    private void ShowPage(string page)
    {
        ContentHost.MaxWidth = page == "home" ? double.PositiveInfinity : 1280;
        HomeScroll.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        VersionsScroll.Visibility = page == "versions" ? Visibility.Visible : Visibility.Collapsed;
        DownloadsContent.Visibility = page == "downloads" ? Visibility.Visible : Visibility.Collapsed;
        AccountContent.Visibility = page == "account" ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        DownloadsContent.SetActive(page == "downloads");
        VersionsContent.SetActive(page == "versions");
        SettingsContent.SetActive(page == "settings");
        if (page == "home") HomeContent.Refresh();
    }
}
