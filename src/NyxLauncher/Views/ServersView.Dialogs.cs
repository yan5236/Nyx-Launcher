using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

public sealed partial class ServersView
{
    private ServerEntry? _editorEntry;
    private ServerVersion? _editorVersion;
    private string? _editorVersionPath;
    private bool _editorOpen;

    /// <summary>只在已绑定路径所属的已登记游戏目录中查找实例；目录或实例不可用时返回空，让用户重新选择。</summary>
    private async Task<ServerVersion?> FindBoundVersionAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string? parent = Path.GetDirectoryName(path);
        string? folder = _folders?.Folders.FirstOrDefault(candidate =>
            string.Equals(Path.Combine(candidate, "versions"), parent, StringComparison.OrdinalIgnoreCase));
        if (folder is null) return null;
        try
        {
            var versions = await ServerVersion.ScanFolderAsync(folder);
            return versions.FirstOrDefault(version => string.Equals(version.Version.DirectoryPath, path, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>核对已绑定的实例后打开表单；新增服务器可直接填写名称和地址。</summary>
    private async Task EditAsync(ServerEntry? entry)
    {
        if (_store is null || _dialogBusy || _editorOpen) return;
        _dialogBusy = true;
        Feedback.IsOpen = false;
        try
        {
            var selected = await FindBoundVersionAsync(entry?.VersionPath);
            if (Visibility != Visibility.Visible || XamlRoot is null) return;
            ShowEditor(entry, selected);
        }
        catch (Exception ex) when (IsExpected(ex)) { ShowError(ex.Message); }
        finally { _dialogBusy = false; }
    }

    /// <summary>按新增或修改填入表单，保留已有默认版本路径和已核对的实例；精选服务器隐藏地址。</summary>
    private void ShowEditor(ServerEntry? entry, ServerVersion? selected)
    {
        _editorEntry = entry;
        _editorVersion = selected;
        _editorVersionPath = entry?.VersionPath;
        bool featured = entry?.Featured == true;
        EditorTitle.Text = entry is null ? "添加服务器" : "修改服务器";
        EditorName.Text = entry?.Name ?? "";
        EditorAddress.Visibility = featured ? Visibility.Collapsed : Visibility.Visible;
        EditorAddress.Text = featured ? "" : entry?.Address ?? "";
        EditorError.IsOpen = false;
        UpdateEditorVersion();
        AttachEditor();
        _editorOpen = true;
        EditorLayer.Visibility = Visibility.Visible;
        FitEditor();
        DispatcherQueue.TryEnqueue(() => EditorName.Focus(FocusState.Programmatic));
    }

    /// <summary>把表单移到窗口内容层，使遮罩同时盖住侧栏；含标题栏行时只覆盖内容行，窗口按钮保持可用。</summary>
    private void AttachEditor()
    {
        if (XamlRoot?.Content is not Panel root || ReferenceEquals(EditorLayer.Parent, root)) return;
        if (EditorLayer.Parent is Panel origin) origin.Children.Remove(EditorLayer);
        int rows = root is Grid grid ? grid.RowDefinitions.Count : 0;
        int contentRow = rows > 1 ? rows - 1 : 0;
        Grid.SetRow(EditorLayer, contentRow);
        Grid.SetRowSpan(EditorLayer, 1);
        root.Children.Add(EditorLayer);
    }

    /// <summary>窗口尺寸变化时重新限制表单宽度与滚动高度，避免最大化或缩窄后溢出。</summary>
    private void EditorLayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_editorOpen && XamlRoot is not null) FitEditor();
    }

    /// <summary>按表单所在区域限制面板和滚动区域；遮罩只覆盖内容行时高度按实际区域计算。</summary>
    private void FitEditor()
    {
        double availableWidth = EditorLayer.ActualWidth > 0 ? EditorLayer.ActualWidth : XamlRoot!.Size.Width;
        double availableHeight = EditorLayer.ActualHeight > 0 ? EditorLayer.ActualHeight : XamlRoot!.Size.Height;
        double width = Math.Clamp(availableWidth - 80, 300, 520);
        EditorPanel.Width = width;
        EditorScroll.MaxWidth = width;
        EditorScroll.MaxHeight = Math.Max(260, availableHeight - 96);
    }

    /// <summary>按表单草稿刷新可选版本卡片和清除按钮；已绑定版本不可用时提示启动时会重新选择。</summary>
    private void UpdateEditorVersion()
    {
        bool bound = !string.IsNullOrEmpty(_editorVersionPath);
        var selected = _editorVersion;
        EditorVersionIcon.Version = selected?.Version;
        EditorVersionName.Text = selected?.Version.Name ?? (bound ? "默认版本不可用" : "未选择启动版本");
        EditorVersionDetail.Text = selected?.Version.LaunchDetail ?? "启动时再选择";
        EditorClearVersion.Visibility = bound ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(EditorVersion, selected is null
            ? $"默认启动版本，可选，{EditorVersionName.Text}，启动时再选择"
            : $"默认启动版本，{selected.Version.AccessibleName}，来自 {selected.Folder}，点击更换");
        ToolTipService.SetToolTip(EditorVersion, selected is null ? "选择已安装的启动版本" : selected.Version.AccessibleName + "\n" + selected.Folder);
    }

    /// <summary>关闭表单并清理未保存的实例和版本路径，焦点回到当前可用的工具栏入口。</summary>
    private void CloseEditor()
    {
        if (!_editorOpen) return;
        _editorOpen = false;
        EditorLayer.Visibility = Visibility.Collapsed;
        EditorError.IsOpen = false;
        _editorEntry = null;
        _editorVersion = null;
        _editorVersionPath = null;
        if (Visibility == Visibility.Visible)
            (AddButton.Visibility == Visibility.Visible ? AddButton : RefreshButton).Focus(FocusState.Programmatic);
    }

    /// <summary>在版本入口下方显示本机存储或输入错误，不关闭表单也不丢已填内容。</summary>
    private void ShowEditorError(string message)
    {
        EditorError.Message = message;
        EditorError.IsOpen = true;
    }

    /// <summary>从当前游戏目录打开版本卡片弹窗；用户选中实例后才更新表单草稿。</summary>
    private async void EditorVersion_Click(object sender, RoutedEventArgs e)
    {
        if (!_editorOpen || _dialogBusy || XamlRoot is null || _folders is null) return;
        _dialogBusy = true;
        try
        {
            var dialog = new ServerVersionDialog(XamlRoot, _folders, _editorVersionPath);
            await dialog.ShowThemedAsync();
            if (dialog.Selected is null) return;
            _editorVersion = dialog.Selected;
            _editorVersionPath = dialog.Selected.Version.DirectoryPath;
            UpdateEditorVersion();
            EditorError.IsOpen = false;
        }
        catch (Exception ex) when (IsExpected(ex)) { ShowEditorError(ex.Message); }
        finally { _dialogBusy = false; }
    }

    /// <summary>清除表单草稿中的默认实例和路径并把焦点移回版本入口；点击保存后才更新本机记录。</summary>
    private void EditorClearVersion_Click(object sender, RoutedEventArgs e)
    {
        if (!_editorOpen || _dialogBusy) return;
        _editorVersion = null;
        _editorVersionPath = null;
        EditorVersion.Focus(FocusState.Programmatic);
        UpdateEditorVersion();
        EditorError.IsOpen = false;
    }

    /// <summary>保存按钮与 Enter 共用同一路径，表单内容不因校验失败而清空。</summary>
    private void EditorSave_Click(object sender, RoutedEventArgs e) => SaveEditor();

    /// <summary>校验并保存服务器，刷新新地址的状态；校验或写盘失败时留在表单内提示。</summary>
    private void SaveEditor()
    {
        if (_store is null) return;
        try
        {
            if (string.IsNullOrWhiteSpace(EditorName.Text)) throw new ArgumentException("请输入服务器名。");
            bool featured = _editorEntry?.Featured == true;
            var next = new ServerEntry(_editorEntry?.Id ?? Guid.NewGuid().ToString("N"), EditorName.Text.Trim(),
                featured ? _editorEntry!.Address : EditorAddress.Text.Trim(), _editorVersionPath, featured);
            _store.Save(next);
            CloseEditor();
            Render();
            AutoRefresh();
        }
        catch (Exception ex) when (IsExpected(ex)) { ShowEditorError(ex.Message); }
    }

    /// <summary>取消编辑并丢弃未保存的输入，已保存的服务器保持不变。</summary>
    private void EditorCancel_Click(object sender, RoutedEventArgs e) => CloseEditor();

    /// <summary>关闭按钮与取消按钮使用同一路径，不写回任何设置。</summary>
    private void EditorClose_Click(object sender, RoutedEventArgs e) => CloseEditor();

    /// <summary>Esc 只关闭表单；表单获得焦点且没有其他弹窗时生效。</summary>
    private void EditorEscape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CloseEditor();
    }

    /// <summary>名称和地址输入框中的 Enter 与保存按钮一致，不改变其他按钮的原生键盘激活。</summary>
    private void EditorField_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        SaveEditor();
    }

    /// <summary>显示服务器名称、地址和删除范围，默认取消；用户明确确认后才移除本机记录，写入失败时保留卡片并提示。</summary>
    private async Task DeleteAsync(ServerEntry entry)
    {
        if (_store is null || entry.Featured || _dialogBusy) return;
        _dialogBusy = true;
        Feedback.IsOpen = false;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "删除服务器？", PrimaryButtonText = "删除记录",
                Content = new TextBlock
                {
                    Text = $"确定从服务器列表移除“{entry.Name}”吗？\n\n服务器地址：{entry.Address}\n\n删除仅移除本机列表中的这条记录，已安装的游戏版本和文件会保留。",
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary) return;
            _store.Delete(entry);
            Render();
        }
        catch (Exception ex) when (IsExpected(ex)) { ShowError(ex.Message); }
        finally { _dialogBusy = false; }
    }

    /// <summary>直接启动有效的已绑定实例；补选或临时启动时从当前游戏目录打开卡片弹窗，选择只用于本次启动。</summary>
    private async Task StartAsync(ServerEntry entry, bool temporary)
    {
        if (_dialogBusy || _editorOpen || _launch is null || _folders is null) return;
        _dialogBusy = true;
        Feedback.IsOpen = false;
        Task? launched = null;
        try
        {
            var endpoint = ServerEndpoint.Parse(entry.Address);
            var selected = temporary ? null : await FindBoundVersionAsync(entry.VersionPath);
            if (Visibility != Visibility.Visible || XamlRoot is null) return;
            if (selected is null)
            {
                var dialog = new ServerVersionDialog(XamlRoot, _folders, entry.VersionPath);
                await dialog.ShowThemedAsync();
                selected = dialog.Selected;
                if (selected is null) return;
            }
            launched = _launch(selected, endpoint);
        }
        catch (Exception ex) when (IsExpected(ex)) { ShowError(ex.Message); }
        finally { _dialogBusy = false; }
        if (launched is not null)
        {
            try { await launched; }
            catch (Exception ex) when (IsExpected(ex)) { ShowError(ex.Message); }
        }
    }
}
