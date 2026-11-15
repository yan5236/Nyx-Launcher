using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

public sealed partial class SettingsView : UserControl
{
    private DownloadSettingsStore? _store;
    private DownloadTaskManager? _tasks;
    private MultiplayerView? _multiplayer;

    /// <summary>加载设置控件；初始化完成前不写配置。</summary>
    public SettingsView() => InitializeComponent();

    /// <summary>连接通用设置中的陶瓦管理操作，复用联机页的模块路径和进程生命周期。</summary>
    public void ConfigureMultiplayer(MultiplayerView multiplayer)
    {
        _multiplayer = multiplayer;
        multiplayer.ModuleSettingsChanged += RefreshModuleSettings;
        RefreshModuleSettings();
    }

    /// <summary>显示当前模块路径；模块运行时禁用安装和替换，仍可导出日志。</summary>
    private void RefreshModuleSettings()
    {
        if (_multiplayer is null) return;
        ModulePath.Text = _multiplayer.ModulePath ?? "尚未安装陶瓦联机模块。";
        DownloadModule.IsEnabled = SelectModule.IsEnabled = _multiplayer.CanChangeModule;
        ExportModuleLog.IsEnabled = !_multiplayer.IsBusy;
    }

    /// <summary>从通用设置打开安装目录选择器，并在本卡片显示下载进度或错误。</summary>
    private async void DownloadModule_Click(object sender, RoutedEventArgs e)
    {
        if (_multiplayer is not null) await _multiplayer.DownloadAsync(XamlRoot, ModuleFeedback);
    }

    /// <summary>从通用设置选择兼容的陶瓦可执行文件；取消保留原有设置。</summary>
    private async void SelectModule_Click(object sender, RoutedEventArgs e)
    {
        if (_multiplayer is not null) await _multiplayer.SelectAsync(XamlRoot, ModuleFeedback);
    }

    /// <summary>从通用设置导出当前或最近一次陶瓦日志，并在本卡片报告结果。</summary>
    private async void ExportModuleLog_Click(object sender, RoutedEventArgs e)
    {
        if (_multiplayer is not null) await _multiplayer.ExportLogAsync(XamlRoot, ModuleFeedback);
    }

    /// <summary>注入设置存储，填充源策略、并行文件数、连接数、限速及隔离选项，初始化不写配置并展示读取错误。</summary>
    public void Configure(DownloadSettingsStore store)
    {
        var settings = store.Current;
        FileSource.SelectedIndex = (int)settings.FileSource;
        VersionSource.SelectedIndex = (int)settings.VersionSource;
        Connections.Value = settings.MaxConnections;
        ParallelDownloads.Value = settings.MaxParallelDownloads;
        Speed.Value = settings.SpeedLimitMiB;
        Isolation.SelectedIndex = (int)settings.Isolation;
        _store = store;
        UpdateLabels();
        SaveError.Message = store.LoadError ?? "";
        SaveError.IsOpen = store.LoadError is not null;
    }

    /// <summary>连接 Java 管理与关于页的共享下载任务，保持原有下载偏好独立持久化；关于页仍延迟创建。</summary>
    public void ConfigureJava(JavaRuntimeStore store, DownloadTaskManager tasks)
    {
        _tasks = tasks;
        JavaPanel.Configure(store, tasks);
        AboutPanel?.ConfigureUpdates(tasks);
    }

    /// <summary>设置页出现时激活当前 Java 或启动 Tab；仅尚未初始化的配置触发一次自动浅扫描。</summary>
    public void SetActive(bool active)
    {
        JavaPanel.SetActive(active && JavaPanel.Visibility == Visibility.Visible);
        MemoryPanel.SetActive(active && LaunchPanel.Visibility == Visibility.Visible);
        AdvancedPanel.SetActive(active && LaunchPanel.Visibility == Visibility.Visible);
    }

    /// <summary>连接全局内存预览与高级启动卡片；全局保存不写入版本目录。</summary>
    public void ConfigureMemory(GameFolderStore folders, Func<InstalledVersion?> version)
    {
        MemoryPanel.Configure(version, () => folders.Selected, () => _store!.Current.Isolation, false);
        AdvancedPanel.Configure(version, false);
    }

    /// <summary>主窗口退出时等待 Java 扫描结束并关闭安装弹窗。</summary>
    public Task StopAsync() => JavaPanel.StopAsync();

    /// <summary>切换通用、启动、Java、下载及关于分组并播放正文转场；关于页首次选择时创建，Java 与启动页沿用原有刷新规则。</summary>
    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (GeneralPanel is null || DownloadPanel is null || JavaPanel is null || LaunchPanel is null) return;
        string? tab = sender.SelectedItem?.Tag as string;
        if (tab == "about" && AboutPanel is null)
        {
            FindName(nameof(AboutPanel));
            if (_tasks is not null) AboutPanel!.ConfigureUpdates(_tasks);
        }
        if (AboutPanel is not null) AboutPanel.Visibility = tab == "about" ? Visibility.Visible : Visibility.Collapsed;
        GeneralPanel.Visibility = tab == "general" ? Visibility.Visible : Visibility.Collapsed;
        DownloadPanel.Visibility = tab == "download" ? Visibility.Visible : Visibility.Collapsed;
        JavaPanel.Visibility = tab == "java" ? Visibility.Visible : Visibility.Collapsed;
        JavaPanel.SetActive(tab == "java");
        LaunchPanel.Visibility = tab == "launch" ? Visibility.Visible : Visibility.Collapsed;
        MemoryPanel.SetActive(tab == "launch");
        AdvancedPanel.SetActive(tab == "launch");
        TabBody.PlayTransition();
    }

    /// <summary>源策略变化后保存，版本页下次打开时使用最新策略。</summary>
    private void Source_SelectionChanged(object sender, SelectionChangedEventArgs e) => Save();

    /// <summary>滑块变化时刷新单位及数值，并保存整数限制。</summary>
    private void Limit_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Save();

    /// <summary>显示并行文件上限、最大线程数和 MiB/s 速率；速率为零代表不限速。</summary>
    private void UpdateLabels()
    {
        ConnectionsLabel.Text = $"最大下载线程数：{(int)Connections.Value}";
        ParallelLabel.Text = $"并行下载文件数：{(int)ParallelDownloads.Value}";
        SpeedLabel.Text = Speed.Value == 0 ? "速率限制：不限速" : $"速率限制：{(int)Speed.Value} MiB/s";
    }

    /// <summary>保存全部下载与隔离偏好；失败时保留原配置、恢复并行滑块并显示未保存。</summary>
    private void Save()
    {
        if (_store is null) return;
        UpdateLabels();
        string? error = _store.Save(_store.Current with
        {
            FileSource = (SourceStrategy)FileSource.SelectedIndex,
            VersionSource = (SourceStrategy)VersionSource.SelectedIndex,
            MaxConnections = (int)Connections.Value,
            MaxParallelDownloads = (int)ParallelDownloads.Value,
            SpeedLimitMiB = (int)Speed.Value,
            Isolation = (IsolationMode)Isolation.SelectedIndex
        });
        if (error is not null)
        {
            var store = _store;
            _store = null;
            ParallelDownloads.Value = store.Current.MaxParallelDownloads;
            _store = store;
            UpdateLabels();
        }
        SaveError.Message = error ?? "";
        SaveError.IsOpen = error is not null;
    }
}
