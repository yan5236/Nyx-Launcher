using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>保留 Java 卡片实例，通过属性通知同步持久化选择，无需重新绑定整张列表。</summary>
public sealed class JavaRuntimeChoice(JavaRuntime runtime) : INotifyPropertyChanged
{
    public JavaRuntime Runtime { get; } = runtime;
    public bool IsSelected { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>同步单选圆圈；即使值未变化也通知，以便保存失败时纠正控件已自行切换的状态。</summary>
    public void SetSelected(bool selected)
    {
        IsSelected = selected;
        PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
    }
}

/// <summary>Java 列表、版本覆盖提示与扫描/导入入口；后台下载始终交给全局任务管理器。</summary>
public sealed partial class JavaSettingsView : UserControl
{
    private JavaRuntimeStore? _store;
    private DownloadTaskManager? _tasks;
    private CancellationTokenSource? _scanCancellation;
    private Task? _scan;
    private InstallJavaDialog? _dialog;
    private bool _refreshing;
    private bool _closing;
    private JavaRuntimeChoice[] _choices = [];

    /// <summary>创建 Java 设置控件，实际数据通过主页面共享服务注入。</summary>
    public JavaSettingsView() => InitializeComponent();

    /// <summary>订阅持久化状态；安装任务可能在后台完成，统一调度回 UI 线程后刷新列表。</summary>
    public void Configure(JavaRuntimeStore store, DownloadTaskManager tasks)
    {
        _store = store; _tasks = tasks;
        store.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => { if (!_closing) Refresh(); });
        Refresh();
        if (store.LoadError is not null) ShowStatus(store.LoadError, true);
    }

    /// <summary>仅未初始化的配置自动浅扫描一次，扫描前持久化标记；后续进入或重启只刷新列表，写入失败显示原因。</summary>
    public void SetActive(bool active)
    {
        if (!active || _closing) return;
        Refresh();
        try { if (_store?.TryBeginAutomaticScan() == true) _scan = ScanAsync(); }
        catch (Exception ex) { ShowStatus(ex.Message, true); }
    }

    /// <summary>窗口关闭时取消扫描、关闭安装弹窗并等待扫描回收；已创建的下载由全局管理器处理。</summary>
    public async Task StopAsync()
    {
        _closing = true; _scanCancellation?.Cancel(); _dialog?.Hide();
        if (_scan is not null) await _scan;
    }

    /// <summary>仅 Java 内容变化时重绑定卡片及版本覆盖；选择变化原位同步圆圈，刷新期间不触发保存。</summary>
    private void Refresh()
    {
        if (_store is null) return;
        _refreshing = true;
        try
        {
            var state = _store.Current;
            if (JavaList.ItemsSource is null || !_choices.Select(x => x.Runtime).SequenceEqual(state.Runtimes))
            {
                _choices = state.Runtimes.Select(x => new JavaRuntimeChoice(x)).ToArray();
                JavaList.ItemsSource = _choices;
                Coverage.ItemsSource = new[] { 8, 17, 21, 25 }.Select(major => $"Java {major} · " +
                    (state.Runtimes.Any(x => x.Major == major && x.Architecture == "x64" && x.Available) ? "已拥有" : "缺失"));
            }
            Automatic.IsChecked = state.SelectedHome is null;
            foreach (var choice in _choices)
                choice.SetSelected(string.Equals(choice.Runtime.Home, state.SelectedHome, StringComparison.OrdinalIgnoreCase));
            Empty.Visibility = state.Runtimes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _refreshing = false; }
    }

    /// <summary>切换自动模式并持久化；写入失败恢复原选择并提示。</summary>
    private void Automatic_Checked(object sender, RoutedEventArgs e) { if (!_refreshing) SaveSelection(null); }

    /// <summary>选择卡片后将其用于下一次启动；失败保持原配置，不静默忽略用户选择。</summary>
    private void Java_Checked(object sender, RoutedEventArgs e)
    {
        if (!_refreshing && sender is RadioButton { Tag: string home } &&
            !string.Equals(_store?.Current.SelectedHome, home, StringComparison.OrdinalIgnoreCase)) SaveSelection(home);
    }

    /// <summary>集中保存单选状态并原位同步圆圈；失败恢复持久化选择，保留卡片实例与滚动位置。</summary>
    private void SaveSelection(string? home)
    {
        try { _store?.Select(home); }
        catch (Exception ex) { ShowStatus(ex.Message, true); }
        Refresh();
    }

    /// <summary>扫描按钮在运行中变为停止，重复点击不建立并发扫描。</summary>
    private void Scan_Click(object sender, RoutedEventArgs e)
    { if (_scanCancellation is not null) _scanCancellation.Cancel(); else _scan = ScanAsync(); }

    /// <summary>后台浅搜索常见位置、已登记路径和各盘符；完成合并结果，停止时安静收起进度，失败仍显示原因。</summary>
    private async Task ScanAsync()
    {
        if (_store is null || _scanCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation; ScanButton.Content = "停止扫描"; Scanning.Visibility = Visibility.Visible;
        ScanStatus.Text = "正在扫描…"; Status.IsOpen = false;
        var known = _store.Current.Runtimes.Select(x => x.Home).ToArray();
        var progress = new Progress<string>(message => { if (!_closing && _scanCancellation == cancellation) ScanStatus.Text = message; });
        try
        {
            var result = await Task.Run(() => JavaDiscovery.Scan(JavaDiscovery.SearchRoots(known), progress, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closing) return;
            _store.Add(result.Runtimes);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closing) ShowStatus("扫描失败：" + ex.Message, true); }
        finally
        {
            _scanCancellation = null;
            if (!_closing) { ScanButton.Content = "扫描 Java"; Scanning.Visibility = Visibility.Collapsed; Refresh(); }
        }
    }

    /// <summary>使用窗口绑定的原生目录选择器导入 JDK/JRE 根目录或 bin；无效目录不保存，取消无副作用。</summary>
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        ImportButton.IsEnabled = false;
        try
        {
            var folder = await new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { CommitButtonText = "导入 Java" }.PickSingleFolderAsync();
            if (folder is null || _closing) return;
            var java = JavaRuntime.Inspect(folder.Path) ?? throw new IOException("所选文件夹不是有效的 Java 安装目录，请选择包含 bin/java.exe 的文件夹。");
            _store!.Add([java]); ShowStatus("已导入 " + java.Title, false);
        }
        catch (Exception ex) { if (!_closing) ShowStatus(ex.Message, true); }
        finally { if (!_closing) ImportButton.IsEnabled = true; }
    }

    /// <summary>通过系统打开卡片对应的安装目录；只打开文件夹，不执行 Java。</summary>
    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync((string)((Button)sender).Tag);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder)) throw new IOException("无法打开 Java 文件夹。");
        }
        catch (Exception ex) { ShowStatus(ex.Message, true); }
    }

    /// <summary>仅移除当前行的配置登记，不触碰 Java 文件；移除当前选择自动回退到自动模式。</summary>
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        try { _store?.Remove((string)((Button)sender).Tag); }
        catch (Exception ex) { ShowStatus(ex.Message, true); }
    }

    /// <summary>打开三步安装弹窗；确认后创建共享下载任务且不显示底部成功提示，关闭弹窗不取消任务。</summary>
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || _tasks is null || _dialog is not null) return;
        InstallButton.IsEnabled = false;
        try
        {
            _dialog = new InstallJavaDialog(XamlRoot, _tasks, _store);
            await _dialog.ShowThemedAsync();
        }
        catch (Exception ex) { if (!_closing) ShowStatus(ex.Message, true); }
        finally { _dialog = null; if (!_closing) InstallButton.IsEnabled = true; }
    }

    /// <summary>将扫描结果与可恢复错误显示在当前设置页。</summary>
    private void ShowStatus(string message, bool error)
    { Status.Message = message; Status.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational; Status.IsOpen = true; }
}
