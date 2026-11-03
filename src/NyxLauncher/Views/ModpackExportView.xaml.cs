using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>版本详情中的整合包导出页，管理文件树选择、导出取消和可定位的成功通知。</summary>
public sealed partial class ModpackExportView : UserControl
{
    private InstalledVersion? _version;
    private string? _root;
    private IReadOnlyList<ModpackExportEntry> _entries = [];
    private readonly Dictionary<ModpackExportEntry, CheckBox> _checks = [];
    private readonly ModrinthCatalogService _catalog = new();
    private CancellationTokenSource? _operation;
    private Func<bool>? _blocked;
    private bool _syncing;
    private bool _busy;
    private string? _exportedPath;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    /// <summary>初始化草图表单和通知定时器；强调按钮引用主题管理器持有的交互画刷，防止原生模板回退到系统蓝色。</summary>
    public ModpackExportView()
    {
        InitializeComponent();
        foreach (string key in new[] { "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
            "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed",
            "AccentButtonBorderBrush", "AccentButtonBorderBrushPointerOver", "AccentButtonBorderBrushPressed" })
            Export.Resources[key] = Application.Current.Resources[key];
        _toastTimer.Tick += Toast_Elapsed;
    }

    /// <summary>首次打开或切换版本时后台扫描实际游戏目录；取消和迟到结果不覆盖新页面。</summary>
    public async void Open(InstalledVersion version, string folder, DownloadSettings settings, Func<bool>? blocked)
    {
        Deactivate();
        var operation = _operation = new CancellationTokenSource();
        _version = version; _blocked = blocked; _root = null;
        PackName.Text = version.Name;
        _catalog.Source = () => settings.FileSource;
        Status.IsOpen = false; Export.IsEnabled = false; Progress.Text = "正在读取文件…";
        try
        {
            var result = await Task.Run(() =>
            {
                string root = ResourceInstallationService.ResolveRoot(folder, version, version.GameVersion, settings.Isolation);
                return (Root: root, Entries: ModpackExportFiles.Scan(root, operation.Token));
            }, operation.Token);
            if (_operation != operation) return;
            _root = result.Root; _entries = result.Entries;
            foreach (var entry in _entries) FileTree.Children.Add(CreateEntry(entry));
            Export.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_operation == operation) ShowError(ex.Message); }
        finally
        {
            if (_operation == operation) { Progress.Text = ""; _operation = null; }
            operation.Dispose();
        }
    }

    /// <summary>离开标签或版本时取消当前工作并释放文件树控件；导出事务负责清理未发布文件。</summary>
    public void Deactivate()
    {
        _operation?.Cancel(); _operation = null;
        _toastTimer.Stop(); SuccessToast.IsOpen = false;
        _entries = []; _checks.Clear(); FileTree.Children.Clear();
        _root = null; _version = null;
        SetBusy(false);
    }

    /// <summary>按需创建文件卡片和紧贴排列的子项；目录标题点击仅展开，独立勾选框选择子树，文件整行可选择并同步父级半选状态。</summary>
    private FrameworkElement CreateEntry(ModpackExportEntry entry)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        label.Children.Add(new FontIcon { Glyph = FileGlyph(entry), FontSize = 18, Width = 20, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Text = entry.Label, VerticalAlignment = VerticalAlignment.Center });
        var check = new CheckBox { Content = label, IsChecked = entry.Selected, IsEnabled = !_busy, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, entry.Label);
        _checks[entry] = check;
        check.Checked += Entry_Changed; check.Unchecked += Entry_Changed; check.Indeterminate += Entry_Changed;
        check.Tag = entry;
        if (!entry.IsDirectory)
        {
            check.VerticalContentAlignment = VerticalAlignment.Center;
            check.MinHeight = 46;
            return new Border { Style = (Style)Application.Current.Resources["CardStyle"], Padding = new Thickness(16, 0, 16, 0), Child = check };
        }
        check.Content = null;
        check.HorizontalAlignment = HorizontalAlignment.Left;
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(check);
        Grid.SetColumn(label, 1);
        header.Children.Add(label);
        var children = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
        var expander = new Expander { Header = header, Tag = entry, Content = children, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, entry.Label);
        expander.Expanding += (_, _) =>
        {
            if (children.Children.Count == 0)
                foreach (var child in entry.Children) children.Children.Add(CreateEntry(child));
        };
        return expander;
    }

    /// <summary>目录统一显示文件夹；按实际文件扩展名区分模组、图片、配置、压缩包、音视频及普通文件，禁用模组保留模组图标。</summary>
    private static string FileGlyph(ModpackExportEntry entry)
    {
        if (entry.IsDirectory) return "\uE838";
        string path = entry.RelativePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? entry.RelativePath[..^9] : entry.RelativePath;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jar" => "\uEA86",
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".ico" => "\uEB9F",
            ".zip" or ".7z" or ".rar" or ".mrpack" => "\uF012",
            ".json" or ".toml" or ".yaml" or ".yml" or ".ini" or ".cfg" or ".properties" => "\uE713",
            ".js" or ".zs" or ".lua" or ".mcfunction" => "\uE943",
            ".litematic" or ".schematic" or ".schem" or ".nbt" => "\uE7C3",
            ".ogg" or ".mp3" or ".wav" or ".flac" => "\uE8D6",
            ".mp4" or ".webm" or ".mkv" => "\uE714",
            ".txt" or ".log" => "\uE8A5",
            _ => "\uE8A5"
        };
    }

    /// <summary>将用户选择应用到整个子树并更新已创建的复选框；同步期间忽略控件事件，防止父子反复覆盖。</summary>
    private void Entry_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not CheckBox { Tag: ModpackExportEntry entry } check) return;
        entry.Select(check.IsChecked == true);
        _syncing = true;
        try { foreach (var pair in _checks) pair.Value.IsChecked = pair.Key.Selected; }
        finally { _syncing = false; }
    }

    /// <summary>切换格式时约束资源策略，保留所选输出文件夹；MCBBS 与 MultiMC 原生导出始终内置文件。</summary>
    private void Format_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Bundle is null) return;
        bool native = Format.SelectedIndex != 0;
        Bundle.IsEnabled = !native;
        Bundle.IsChecked = native;
        FormatHint.Visibility = native ? Visibility.Visible : Visibility.Collapsed;
        Bundle_Changed(sender, new RoutedEventArgs());
    }

    /// <summary>内置资源时即时展示协议警告，清除并禁用仅平台资源选项。</summary>
    private void Bundle_Changed(object sender, RoutedEventArgs e)
    {
        if (OnlyModrinth is null || BundleWarning is null) return;
        bool bundle = Bundle.IsChecked == true;
        BundleWarning.IsOpen = bundle;
        OnlyModrinth.IsEnabled = !bundle && Format.SelectedIndex == 0;
        if (!OnlyModrinth.IsEnabled) OnlyModrinth.IsChecked = false;
    }

    /// <summary>点击路径区域打开原生文件夹选择器；取消保留原目录，选择期间禁用入口避免重复打开。</summary>
    private async void Destination_Click(object sender, RoutedEventArgs e)
    {
        Destination.IsEnabled = false;
        var version = _version;
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { CommitButtonText = "选择导出文件夹" };
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null && _version == version) DestinationPath.Text = folder.Path;
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { Destination.IsEnabled = !_busy; }
    }

    /// <summary>按包名和格式在所选目录生成文件名，再冻结表单后台导出；非法文件名或运行中拒绝导出，成功显示可定位的通知。</summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _version is not { } version || _root is not { } root) return;
        if (_blocked?.Invoke() == true) { ShowError("请先停止游戏并等待安装任务结束，再导出整合包。"); return; }
        if (string.IsNullOrWhiteSpace(DestinationPath.Text)) { ShowError("请选择导出文件夹。"); return; }
        string destination;
        try
        {
            string fileName = string.Concat(PackName.Text.Trim().Select(x => Path.GetInvalidFileNameChars().Contains(x) ? '_' : x)).TrimEnd('.', ' ');
            GameInstallationService.ValidateName(fileName);
            destination = Path.Combine(DestinationPath.Text, fileName + (Format.SelectedIndex == 0 ? ".mrpack" : ".zip"));
        }
        catch (Exception ex) { ShowError(ex.Message); return; }
        var options = new ModpackExportOptions(PackName.Text.Trim(), Author.Text.Trim(), PackVersion.Text.Trim(), destination,
            (ModpackExportFormat)Format.SelectedIndex, Bundle.IsChecked == true, OnlyModrinth.IsChecked == true);
        var entries = _entries;
        var operation = _operation = new CancellationTokenSource();
        SetBusy(true); Status.IsOpen = false; SuccessToast.IsOpen = false;
        var progress = new Progress<string>(message => { if (_operation == operation) Progress.Text = message; });
        try
        {
            int skipped = await Task.Run(() => ModpackExportService.ExportAsync(root, version, options, entries, _catalog, progress, operation.Token));
            if (_operation != operation) return;
            _exportedPath = Path.GetFullPath(options.Destination);
            SuccessToast.Message = "整合包导出成功，点击定位文件。" + (skipped > 0 ? $"已跳过 {skipped} 个未被 Modrinth 收录的资源。" : "");
            SuccessToast.IsOpen = true; _toastTimer.Stop(); _toastTimer.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_operation == operation) ShowError(ex.Message); }
        finally
        {
            if (_operation == operation) { _operation = null; SetBusy(false); Progress.Text = ""; }
            operation.Dispose();
        }
    }

    /// <summary>锁定输入和文件树，但保留取消按钮；恢复时重新应用格式约束。</summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (Control control in new Control[] { PackName, Author, PackVersion, Format, Destination }) control.IsEnabled = !busy;
        foreach (var check in _checks.Values) check.IsEnabled = !busy;
        Bundle.IsEnabled = !busy && Format.SelectedIndex == 0;
        OnlyModrinth.IsEnabled = !busy && Format.SelectedIndex == 0 && Bundle.IsChecked != true;
        Export.IsEnabled = !busy; Cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>取消当前导出，事务完成清理后恢复表单。</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();

    /// <summary>显示可恢复的导出或定位错误，不将失败伪装为成功。</summary>
    private void ShowError(string message) { Status.Severity = InfoBarSeverity.Error; Status.Message = message; Status.IsOpen = true; }

    /// <summary>十秒后关闭成功通知；关闭通知不会删除或移动导出文件。</summary>
    private void Toast_Elapsed(object? sender, object e) { _toastTimer.Stop(); SuccessToast.IsOpen = false; }

    /// <summary>点击整条成功通知定位文件；忽略关闭按钮冒泡的点击，避免关闭通知时打开目录。</summary>
    private void Toast_Tapped(object sender, TappedRoutedEventArgs e)
    {
        for (var current = e.OriginalSource as DependencyObject; current is not null && current != SuccessToast;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
            if (current is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return;
        e.Handled = true;
        Locate();
    }

    /// <summary>通知自身获得键盘焦点时支持回车或空格定位；关闭按钮的键盘事件不触发定位。</summary>
    private void Toast_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SuccessToast) || e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)) return;
        e.Handled = true;
        Locate();
    }

    /// <summary>使用系统文件夹启动接口选中已导出的文件；文件被移动时显示错误。</summary>
    private async void Locate()
    {
        if (_exportedPath is null) return;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_exportedPath);
            var folder = await file.GetParentAsync();
            var options = new Windows.System.FolderLauncherOptions(); options.ItemsToSelect.Add(file);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder, options)) throw new IOException("系统未能定位导出文件。");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
