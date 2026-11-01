using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

public sealed partial class ModManagementView
{
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private bool _filtering;

    /// <summary>把卡片内部的原生勾选框双向绑定到列表项选择状态；复用列表键盘多选和虚拟化，不创建第二份选择状态。</summary>
    private void SelectionBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;
        DependencyObject? parent = box;
        while (parent is not null && parent is not ListViewItem) parent = VisualTreeHelper.GetParent(parent);
        if (parent is ListViewItem item)
            box.SetBinding(CheckBox.IsCheckedProperty, new Binding { Source = item, Path = new PropertyPath("IsSelected"), Mode = BindingMode.TwoWay });
    }

    /// <summary>记录勾选文件路径；在线摘要刷新后恢复选择，搜索或状态筛选隐藏的项目不参与批量操作。</summary>
    private void Mods_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filtering) return;
        foreach (var mod in e.RemovedItems.OfType<InstalledMod>()) _selected.Remove(mod.Path);
        foreach (var mod in e.AddedItems.OfType<InstalledMod>()) _selected.Add(mod.Path);
        RefreshSelection();
    }

    /// <summary>从当前可见列表截取所选文件快照，防止批量操作期间选择变化扩大作用范围。</summary>
    private InstalledMod[] SelectedMods() => ModList.Items.OfType<InstalledMod>().Where(x => _selected.Contains(x.Path)).ToArray();

    /// <summary>有选择才显示批量操作栏；更新按钮仅在所选文件存在更新时显示，提示列出各自目标版本。</summary>
    private void RefreshSelection()
    {
        if (BatchPanel is null) return;
        var selected = SelectedMods();
        BatchPanel.Visibility = selected.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectAllBox.IsChecked = selected.Length > 0 && selected.Length == ModList.Items.Count;
        var updates = selected.Where(x => x.Update is not null).ToArray();
        BatchUpdateButton.Visibility = updates.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(BatchUpdateButton, string.Join("\n", updates.Select(x => x.Title + " → " + x.Update!.VersionNumber)));
    }

    /// <summary>全选只作用于当前搜索和状态筛选后的可见文件；再次点击清空选择。</summary>
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (SelectAllBox.IsChecked == true) ModList.SelectAll();
        else ModList.SelectedItems.Clear();
    }

    /// <summary>批量只更新有兼容新版的所选模组，其余文件不变。</summary>
    private async void BatchUpdate_Click(object sender, RoutedEventArgs e) =>
        await RunBatchAsync(SelectedMods().Where(x => x.Update is not null).ToArray(), UpdateModAsync);

    /// <summary>只启用当前禁用的所选文件，已启用文件保持原状。</summary>
    private async void BatchEnable_Click(object sender, RoutedEventArgs e) =>
        await RunBatchAsync(SelectedMods().Where(x => x.Disabled).ToArray(), ToggleModAsync);

    /// <summary>只禁用当前启用的所选文件，已禁用文件保持原状。</summary>
    private async void BatchDisable_Click(object sender, RoutedEventArgs e) =>
        await RunBatchAsync(SelectedMods().Where(x => !x.Disabled).ToArray(), ToggleModAsync);

    /// <summary>复用单项启停的哈希与不覆盖校验，返回完成任务供串行批量调度。</summary>
    private Task ToggleModAsync(InstalledMod mod, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ModManagementService.Toggle(DirectoryPath(), mod);
        return Task.CompletedTask;
    }

    /// <summary>一次确认所选文件数量和名称后逐个移入回收站；确认期间的状态变化仍会在每项操作前拒绝。</summary>
    private async void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMods();
        await RunBatchAsync(selected, (mod, _) =>
        {
            ModManagementService.Validate(DirectoryPath(), mod);
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(mod.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            return Task.CompletedTask;
        }, confirmDelete: true);
    }

    /// <summary>在同一操作锁中逐项处理快照；单项失败继续其余文件并汇总具体文件名，取消则停止，最后统一刷新实际结果。</summary>
    private async Task RunBatchAsync(InstalledMod[] selected, Func<InstalledMod, CancellationToken, Task> action, bool confirmDelete = false)
    {
        if (selected.Length == 0) return;
        await RunAsync(async token =>
        {
            if (confirmDelete)
            {
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = $"删除 {selected.Length} 个模组？",
                    Content = new ScrollViewer { MaxHeight = 320, Content = new TextBlock { Text = string.Join("\n", selected.Select(x => x.FileName)), TextWrapping = TextWrapping.Wrap } },
                    PrimaryButtonText = "移入回收站", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            var failures = new List<string>();
            foreach (var mod in selected)
            {
                EnsureWritable();
                try { await action(mod, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures.Add(mod.FileName + "：" + ex.Message); }
            }
            if (failures.Count > 0) throw new IOException(string.Join("\n", failures));
        });
    }
}
