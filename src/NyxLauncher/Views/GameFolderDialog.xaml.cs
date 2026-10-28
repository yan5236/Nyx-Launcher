using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>真实目录选择器；列表点选即关闭，行尾 SVG 仅移除登记，底部调用系统文件夹选择器。</summary>
public sealed partial class GameFolderDialog : ContentDialog
{
    private readonly GameFolderStore _store;

    /// <summary>绑定共享持久化服务并限制弹窗尺寸，长路径自动换行、长列表可滚动。</summary>
    public GameFolderDialog(XamlRoot root, GameFolderStore store)
    {
        InitializeComponent();
        XamlRoot = root;
        _store = store;
        DialogContent.Width = Math.Clamp(root.Size.Width - 100, 240, 420);
        DialogContent.Height = Math.Clamp(root.Size.Height - 180, 180, 460);
        Refresh();
        ShowError(store.LoadError);
    }

    /// <summary>重新绑定已落盘的目录集合；空列表仍保留添加入口。</summary>
    private void Refresh()
    {
        Folders.ItemsSource = _store.Folders.ToArray();
        Empty.Visibility = _store.Folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>成功保存当前选择后立即关闭；路径失效或持久化失败时保持弹窗并提示，不伪装选择成功。</summary>
    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        try { _store.Select(path); Hide(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { ShowError(ex.Message); }
    }

    /// <summary>只移除路径记录并保持弹窗打开；独立按钮避免同时触发选择，任何磁盘游戏文件均不修改。</summary>
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        try { _store.Remove(path); Refresh(); ShowError(null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { ShowError(ex.Message); }
    }

    /// <summary>使用绑定当前窗口的原生选择器添加真实目录，取消不改状态；禁用重复打开，成功后仍由用户点选目录关闭。</summary>
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        AddButton.IsEnabled = false;
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { CommitButtonText = "添加文件夹" };
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            _store.Add(folder.Path);
            Refresh();
            ShowError(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        { ShowError("无法添加文件夹：" + ex.Message); }
        finally { AddButton.IsEnabled = true; }
    }

    /// <summary>关闭弹窗而不改变选择；已成功执行的添加或移除操作保持保存。</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>在弹窗中显示目录访问或保存失败，不再叠加第二个模态窗口。</summary>
    private void ShowError(string? error)
    {
        Status.Message = error ?? "";
        Status.IsOpen = error is not null;
    }
}
