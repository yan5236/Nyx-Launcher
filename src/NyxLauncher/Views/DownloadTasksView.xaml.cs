using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;
using System.Collections.ObjectModel;

namespace NyxLauncher.Views;

public sealed partial class DownloadTasksView : UserControl
{
    private DownloadTaskManager? _tasks;
    private bool _dialogOpen;
    private readonly ObservableCollection<DownloadTaskItem> _visible = [];

    /// <summary>创建紧凑任务列表；任务生命周期由主页面共享服务持有。</summary>
    public DownloadTasksView() { InitializeComponent(); TaskList.ItemsSource = _visible; }

    /// <summary>订阅任务集合和状态变化；页面隐藏时下载继续运行。</summary>
    public void Configure(DownloadTaskManager tasks) { _tasks = tasks; tasks.Changed += Tasks_Changed; Refresh(); }

    /// <summary>任务状态变化只重算可见成员，仍可见的卡片保持原实例，不重放入场动画。</summary>
    private void Tasks_Changed(object? sender, EventArgs args) => Refresh();

    /// <summary>将下拉项映射到真实状态并增量更新集合；不重新绑定 ItemsSource，暂停、继续、完成不会重建原卡片。</summary>
    private void Refresh()
    {
        if (_tasks is null || TaskList is null) return;
        var items = _tasks.Tasks.Where(x => Filter.SelectedIndex switch
        {
            1 => x.IsBusy, 2 => x.State == DownloadState.Paused, 3 => x.State == DownloadState.Completed,
            4 => x.State == DownloadState.Failed, 5 => x.State == DownloadState.Cancelled, _ => true
        }).ToArray();
        for (int i = _visible.Count - 1; i >= 0; i--)
            if (!items.Contains(_visible[i])) _visible.RemoveAt(i);
        for (int i = 0; i < items.Length; i++)
        {
            if (i < _visible.Count && ReferenceEquals(_visible[i], items[i])) continue;
            int existing = _visible.IndexOf(items[i]);
            if (existing >= 0) _visible.Move(existing, i);
            else _visible.Insert(i, items[i]);
        }
        Empty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>用户切换状态过滤时保留任务，不停止任何后台工作。</summary>
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => Refresh();

    /// <summary>在当前任务页打开链接/目录弹窗；防止重复打开模态窗口。</summary>
    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_tasks is null || _dialogOpen) return;
        _dialogOpen = true;
        try { await new CreateDownloadDialog(XamlRoot, _tasks).ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    /// <summary>暂停或继续按钮只作用于所在卡片；任务管理器负责取消和重试的并发边界。</summary>
    private void Pause_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: DownloadTaskItem item }) _tasks?.PauseOrResume(item); }

    /// <summary>下载中取消任务；完成或取消清理结束后删除列表记录，保留磁盘文件与共享游戏资源。</summary>
    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadTaskItem item }) return;
        if (item.CanRemove) _tasks?.RemoveFinished(item);
        else _tasks?.Cancel(item);
    }

    /// <summary>通过系统打开文件、游戏或 Java 的实际目标目录；安装未发布时打开父目录，失败在页面中显示。</summary>
    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadTaskItem item }) return;
        try
        {
            string path = (item.IsGame || item.IsJava) && Directory.Exists(item.Destination) ? item.Destination : Path.GetDirectoryName(item.Destination)!;
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder)) throw new IOException("无法打开下载目录。");
        }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
    }
}
