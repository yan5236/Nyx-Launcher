using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>首页展示当前目录中的真实安装，并以明确标示的离线账户启动。</summary>
public sealed partial class HomeView : UserControl
{
    private GameFolderStore? _folders;
    private DownloadSettingsStore? _settings;
    private int _refresh;

    /// <summary>加载首页控件，依赖通过主页面注入。</summary>
    public HomeView() => InitializeComponent();

    /// <summary>共用目录和隔离设置，初始化真实可启动列表。</summary>
    public void Configure(GameFolderStore folders, DownloadSettingsStore settings)
    { _folders = folders; _settings = settings; Refresh(); }

    /// <summary>进入首页或安装完成后重新扫描，保留选中名称；新扫描覆盖旧扫描，错误不会保留过期版本。</summary>
    public async void Refresh()
    {
        int generation = ++_refresh;
        string? folder = _folders?.Selected;
        string? selected = (VersionList.SelectedItem as InstalledVersion)?.Name;
        try
        {
            var versions = folder is null ? [] : await Task.Run(() => InstalledVersionService.Scan(folder).Where(x => x.Error is null).ToArray());
            if (generation != _refresh) return;
            VersionList.ItemsSource = versions;
            VersionList.SelectedItem = versions.FirstOrDefault(x => x.Name == selected) ?? versions.FirstOrDefault();
            LaunchButton.IsEnabled = versions.Length > 0;
        }
        catch (Exception ex)
        {
            if (generation != _refresh) return;
            VersionList.ItemsSource = null; LaunchButton.IsEnabled = false;
            Status.Message = ex.Message; Status.Severity = InfoBarSeverity.Error; Status.IsOpen = true;
        }
    }

    /// <summary>使用当前隔离策略启动选中版本；启动构造失败明确报告，不把进程创建等同于游戏已进入菜单。</summary>
    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is not InstalledVersion version || _folders?.Selected is not string root) return;
        LaunchButton.IsEnabled = false;
        try
        {
            using var process = await GameLaunchService.BuildAsync(root, version, _settings!.Current.Isolation);
            if (!process.Start()) throw new IOException("系统未能创建游戏进程。");
            Status.Severity = InfoBarSeverity.Informational;
            Status.Message = "已启动游戏进程 · Steve（离线）";
            Status.IsOpen = true;
        }
        catch (Exception ex) { Status.Message = ex.Message; Status.Severity = InfoBarSeverity.Error; Status.IsOpen = true; }
        finally { LaunchButton.IsEnabled = true; }
    }
}
