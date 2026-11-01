using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;
using NyxLauncher.Views;

namespace NyxLauncher;

/// <summary>在真实主窗口执行实网导航和失焦检查；只读取配置，不安装游戏或保存用户偏好。</summary>
public partial class App : Application
{
    private MainWindow? _window;
    private Window? _other;
    private readonly string _log = Path.Combine(AppContext.BaseDirectory, "memory-checks.log");
    private readonly bool _baseline = Environment.GetEnvironmentVariable("NYX_MEMORY_BASELINE") == "1";

    /// <summary>使用正式应用资源，测试入口仅替换启动流程。</summary>
    public App() => InitializeComponent();

    /// <summary>运行三轮真实资源详情与侧栏导航，记录内存及引用释放，再验证窗口失焦和恢复。</summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        File.WriteAllText(_log, "START\n");
        try
        {
            Themes.ThemeManager.Current.Initialize();
            _window = new MainWindow();
            _window.Activate();
            await Task.Delay(2500);
            var page = (MainPage)Field<Frame>(_window, "RootFrame").Content;
            Sample("startup");
            var detailReferences = new List<WeakReference>();
            ResourceProject? lastProject = null;
            Check(Field<object?>(page, "DownloadsContent") is null && Field<object?>(page, "VersionsContent") is null &&
                Field<object?>(page, "SettingsContent") is null && Field<object?>(page, "AccountContent") is null, "startup-home-only");
            for (int round = 1; round <= 3; round++)
            {
                if (round == 1) Invoke(page, "Versions_ModDownloadRequested", page, EventArgs.Empty);
                else Field<NavigationView>(page, "Navigation").SelectedItem = Field<NavigationViewItem>(page, "DownloadsItem");
                await Task.Delay(500);
                var downloads = Field<DownloadsView>(page, "DownloadsContent");
                var catalog = Field<ResourceCatalogView>(downloads, "ResourcesContent");
                if (round == 1) Check(Field<string>(downloads, "_category") == "mod", "first-cross-module-mod-tab");
                downloads.ShowMods();
                await WaitAsync(() => Field<ListView>(catalog, "ProjectList").Items.Count > 0, () => "resource catalog: " + Field<TextBlock>(Field<object>(catalog, "LoadStatus"), "ErrorText").Text + " active=" + Field<bool>(catalog, "_active"));
                var project = (ResourceProject)Field<ListView>(catalog, "ProjectList").Items[0];
                lastProject = project;
                Invoke(downloads, "Mod_Requested", catalog, project);
                var details = Field<ModDetailsView>(downloads, "ModDetailsContent");
                detailReferences.Add(new WeakReference(details));
                await WaitAsync(() => Field<IReadOnlyList<ModrinthModFile>>(details, "_files").Count > 0, () => "project files: " + Field<TextBlock>(Field<object>(details, "LoadStatus"), "ErrorText").Text);
                Sample($"detail-{round}");
                var files = new WeakReference(Field<object>(details, "_files"));
                if (round == 1)
                {
                    Invoke(details, "Dependency_Click", new Button { Tag = project }, new RoutedEventArgs());
                    await Task.Delay(300);
                    Invoke(details, "Back_Click", details, new RoutedEventArgs());
                    Check(ReferenceEquals(files.Target, Field<object>(details, "_files")), "dependency-back-preserves-files");
                }
                if (round == 3)
                    Invoke(details, "Dependency_Click", new Button { Tag = project }, new RoutedEventArgs());
                Field<NavigationView>(page, "Navigation").SelectedItem = Field<NavigationViewItem>(page, "HomeItem");
                details = null!;
                await Task.Delay(2000);
                Sample($"home-{round}");
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await Task.Delay(300);
                Log($"files-alive-{round}={files.IsAlive}");
                if (!_baseline && files.IsAlive) throw new Exception("离开详情后发布列表仍被引用");
                Sample($"home-collected-{round}");
            }
            Invoke(page, "ShowPage", "settings");
            var settings = Field<SettingsView>(page, "SettingsContent");
            await Task.Delay(300);
            var tabs = (SelectorBar)((Grid)settings.Content).Children[0];
            tabs.SelectedItem = tabs.Items[1];
            var memory = Field<MemorySettingsView>(settings, "MemoryPanel");
            await Task.Delay(500);
            Check(Field<DispatcherTimer>(memory, "_timer").IsEnabled, "foreground-timer");
            _other = new Window { Title = "Memory check focus target", Content = new TextBlock { Text = "后台内存检查" } };
            _other.Activate();
            await Task.Delay(6500);
            Sample("background");
            Check(detailReferences.All(reference => !reference.IsAlive), "background-hidden-details-collected");
            Check(!Field<DispatcherTimer>(memory, "_timer").IsEnabled, "background-timer-paused");
            _window.Activate();
            await Task.Delay(500);
            Check(Field<DispatcherTimer>(memory, "_timer").IsEnabled, "foreground-timer-resumed");
            await CheckNavigationAsync(page, lastProject!);
            Log("PASS");
            await page.StopDownloadsAsync();
            _other.Close();
            _window.Close();
            Exit();
        }
        catch (Exception ex) { Log(ex.ToString()); Environment.ExitCode = 1; _other?.Close(); _window?.Close(); Exit(); }
    }

    /// <summary>内存采样结束后检查首次账户导航、版本详情退场、隐藏采样及跨模块文件入口；只读取真实配置，无本地版本时使用不落盘的版本夹具。</summary>
    private async Task CheckNavigationAsync(MainPage page, ResourceProject project)
    {
        Invoke(page, "ShowPage", "account");
        Check(Field<AccountsView>(page, "AccountContent").Visibility == Visibility.Visible, "first-account-navigation");
        var home = Field<HomeView>(page, "HomeContent");
        var version = home.SelectedVersion ?? new InstalledVersion("memory-check", Path.Combine(AppContext.BaseDirectory, "missing-version"), "1.21.1", "Fabric", "", null);
        Invoke(page, "Home_ManageRequested", home, version);
        await Task.Delay(350);
        var versions = Field<VersionsView>(page, "VersionsContent");
        await WaitAsync(() => Field<object?>(versions, "_pageTransition") is null, () => "version enter transition");
        var reference = await ExerciseVersionDetailsAsync(versions, version);
        versions.OpenDetails(version);
        Check(Field<VersionDetailsView>(versions, "Details").Version == version &&
            ReferenceEquals(reference, Field<VersionDetailsView>(versions, "Details")), "version-details-reuse");
        Invoke(page, "Versions_ModFilesRequested", versions, (project, version));
        await Task.Delay(350);
        var downloads = Field<DownloadsView>(page, "DownloadsContent");
        var files = Field<ModDetailsView>(downloads, "ModDetailsContent");
        Check(Field<string>(files, "_game") == version.GameVersion && Field<ResourceProject>(files, "_project").Id == project.Id, "cross-module-file-filter");
        Field<NavigationView>(page, "Navigation").SelectedItem = Field<NavigationViewItem>(page, "HomeItem");
        await Task.Delay(350);
        Check(Field<IReadOnlyList<ModrinthModFile>>(files, "_files").Count == 0, "cancelled-files-do-not-repopulate");
    }

    /// <summary>操作真实版本详情并返回当前实例，检查返回时停止采样、再次打开时复用控件。</summary>
    private async Task<VersionDetailsView> ExerciseVersionDetailsAsync(VersionsView versions, InstalledVersion version)
    {
        var detail = Field<VersionDetailsView>(versions, "Details");
        Check(detail.Version == version && detail.Visibility == Visibility.Visible, "home-manage-opens-details");
        var tabs = Field<SelectorBar>(detail, "Tabs");
        tabs.SelectedItem = tabs.Items[2];
        var memory = Field<MemorySettingsView>(detail, "MemoryPanel");
        Check(Field<DispatcherTimer>(memory, "_timer").IsEnabled, "version-memory-preview-active");
        Invoke(versions, "Details_BackRequested", detail, EventArgs.Empty);
        await WaitAsync(() => detail.Visibility == Visibility.Collapsed, () => "version return transition");
        Check(!Field<DispatcherTimer>(memory, "_timer").IsEnabled && detail.Visibility == Visibility.Collapsed, "version-back-stops-preview");
        return detail;
    }

    /// <summary>等待实际请求完成；超时作为失败记录，避免空页面被当成验收通过。</summary>
    private static async Task WaitAsync(Func<bool> ready, Func<string> stage)
    {
        for (int i = 0; i < 120; i++) { if (ready()) return; await Task.Delay(500); }
        throw new TimeoutException(stage());
    }

    /// <summary>读取真实控件或私有数据以检查生命周期，不替换业务实现。</summary>
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(owner)!;

    /// <summary>调用真实导航事件入口；参数使用已加载控件和实网数据。</summary>
    private static void Invoke(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);

    /// <summary>记录工作集、私有提交和托管堆，采样本身不触发回收。</summary>
    private void Sample(string stage)
    {
        using var process = Process.GetCurrentProcess();
        Log($"{stage}: working={process.WorkingSet64 / 1048576d:F2} MiB private={process.PrivateMemorySize64 / 1048576d:F2} MiB managed={GC.GetTotalMemory(false) / 1048576d:F2} MiB");
    }

    /// <summary>基线只记录失败条件；优化版本要求每项断言成立。</summary>
    private void Check(bool condition, string name) { Log($"{name}={condition}"); if (!_baseline && !condition) throw new Exception(name); }

    /// <summary>逐步落盘验收证据，进程失败时保留已完成阶段。</summary>
    private void Log(string text) => File.AppendAllText(_log, text + "\n");
}
