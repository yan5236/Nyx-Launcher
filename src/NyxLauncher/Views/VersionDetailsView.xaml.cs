using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using NyxLauncher.Services;
using Windows.Graphics.Imaging;

namespace NyxLauncher.Views;

/// <summary>单个真实版本的四 Tab 管理页；基础页提供改名、图标、实际游戏目录快捷入口与删除危险区，游戏设置提供版本独立内存和高级配置。</summary>
public sealed partial class VersionDetailsView : UserControl
{
    private GameFolderStore? _folders;
    private DownloadSettingsStore? _settings;
    private Func<bool>? _mutationBlocked;
    public InstalledVersion? Version { get; private set; }
    public event EventHandler? BackRequested;
    public event EventHandler? DeleteRequested;
    public event EventHandler? ModDownloadRequested;
    public event EventHandler<(ResourceProject Project, InstalledVersion Version)>? ModFilesRequested;

    /// <summary>初始化详情视图，不在构造时访问游戏文件。</summary>
    public VersionDetailsView()
    {
        InitializeComponent();
        ModsPanel.DownloadRequested += (_, _) => ModDownloadRequested?.Invoke(this, EventArgs.Empty);
        ModsPanel.ModFilesRequested += (_, request) => ModFilesRequested?.Invoke(this, request);
    }

    /// <summary>共享游戏目录、隔离策略与版本启动配置；委托用于在启动或安装进行时拒绝改名。</summary>
    public void Configure(GameFolderStore folders, DownloadSettingsStore settings, Func<bool> mutationBlocked)
    {
        _folders = folders; _settings = settings; _mutationBlocked = mutationBlocked;
        MemoryPanel.Configure(() => Version, () => folders.Selected, () => settings.Current.Isolation, true);
        AdvancedPanel.Configure(() => Version, true);
        ModsPanel.Configure(folders, settings, mutationBlocked);
    }

    /// <summary>打开选中版本并默认选择基础分类，取消旧版本导出，刷新名称、元数据和图标；错误清单保留具体原因。</summary>
    public void Open(InstalledVersion version)
    {
        ModsPanel.SetActive(false);
        SetExportActive(false);
        Version = version;
        RefreshVersion();
        Tabs.SelectedItem = Tabs.Items[0];
        ShowError(version.Error);
    }

    /// <summary>同步顶部与可编辑名称及共用图标；改名和图标落盘后调用，不重置当前 Tab。</summary>
    private void RefreshVersion()
    {
        VersionName.Text = NameInput.Text = Version!.Name;
        VersionInfo.Text = Version.Detail;
        ToolTipService.SetToolTip(VersionName, Version.Name);
        ToolTipService.SetToolTip(VersionInfo, Version.Detail);
        HeaderIcon.Version = PreviewIcon.Version = Version;
        HeaderIcon.Reload(); PreviewIcon.Reload();
    }

    /// <summary>详情隐藏时停止内存采样、模组请求与导出工作并释放导出树；显示时仅激活当前分类。</summary>
    public void SetActive(bool active)
    {
        MemoryPanel.SetActive(active && Tabs.SelectedItem == Tabs.Items[2]);
        AdvancedPanel.SetActive(active && Tabs.SelectedItem == Tabs.Items[2]);
        ModsPanel.SetActive(active && Tabs.SelectedItem == Tabs.Items[1], Version);
        SetExportActive(active && Tabs.SelectedItem == Tabs.Items[3]);
    }

    /// <summary>请求父页返回版本列表，保持搜索与文件夹选择。</summary>
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>请求父页为当前版本显示共用回收站确认框；基础区正在改名或处理图标时不允许发起删除。</summary>
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Version is not null && BasicPanel.IsEnabled) DeleteRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按分类显示基础、模组、游戏设置或导出并播放正文转场，同时停止隐藏分类的采样、网络请求与导出事务。</summary>
    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (BasicPanel is not null) BasicPanel.Visibility = sender.SelectedItem == sender.Items[0] ? Visibility.Visible : Visibility.Collapsed;
        if (GamePanel is not null)
        {
            bool active = sender.SelectedItem == sender.Items[2];
            GamePanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            MemoryPanel.SetActive(active);
            AdvancedPanel.SetActive(active);
        }
        if (Status is not null) Status.IsOpen = false;
        if (ExportHost is not null) SetExportActive(sender.SelectedItem == sender.Items[3]);
        if (ModsPanel is not null)
        {
            bool active = sender.SelectedItem == sender.Items[1];
            ModsPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            ModsPanel.SetActive(active, Version);
        }
        TabBody?.PlayTransition();
    }

    /// <summary>导出页按需创建并读取实际隔离目录；离开时取消工作并回收控件，不在其他标签保留文件树。</summary>
    private void SetExportActive(bool active)
    {
        ExportHost.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active)
        {
            (ExportHost.Content as ModpackExportView)?.Deactivate();
            ExportHost.Content = null;
        }
        else if (ExportHost.Content is null && Version is { } version && _folders?.Selected is { } folder && _settings is not null)
        {
            var view = new ModpackExportView();
            ExportHost.Content = view;
            view.Open(version, folder, _settings.Current, _mutationBlocked);
        }
    }

    /// <summary>删除当前版本的内存和高级启动覆盖，使后续读取持续继承全局；失败显示原因，并刷新两张卡片以反映实际保存状态。</summary>
    private void ResetGameSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Version is not { } version) return;
        try
        {
            MemorySettingsStore.Reset(version);
            AdvancedLaunchSettingsStore.Reset(version);
            ShowError(null);
        }
        catch (Exception ex) { ShowError("未能完全恢复全局设置：" + ex.Message); }
        finally
        {
            MemoryPanel.SetActive(true);
            AdvancedPanel.SetActive(true);
        }
    }

    /// <summary>输入框按 Enter 时使用与保存按钮相同的校验与改名流程，不在每次输入时移动目录。</summary>
    private void NameInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        Rename_Click(sender, e);
    }

    /// <summary>在后台同步改名目录及清单，操作期间禁用基础区；失败显示原因并保留输入，运行或安装中的版本不能移动。</summary>
    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Version is not { } version || _folders?.Selected is not { } root || !BasicPanel.IsEnabled) return;
        BasicPanel.IsEnabled = false;
        try
        {
            if (_mutationBlocked?.Invoke() == true) throw new IOException("请先停止游戏并等待安装任务结束，再修改版本名。");
            string name = NameInput.Text;
            var renamed = await Task.Run(() => VersionManagementService.Rename(root, version, name));
            if (Version != version) return;
            Version = renamed;
            RefreshVersion();
            ShowError(null);
        }
        catch (Exception ex) { ShowError("未修改版本名：" + ex.Message); }
        finally { BasicPanel.IsEnabled = true; }
    }

    /// <summary>通过原生选择器选择 PNG/JPEG/BMP，限制输入为 16 MiB、4096 像素边长；解码缩小为最长 256 像素 PNG 后保存，取消或失败保留原图标。</summary>
    private async void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Version is not { } version || _folders?.Selected is not { } root || !BasicPanel.IsEnabled) return;
        BasicPanel.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(extension);
            var selected = await picker.PickSingleFileAsync();
            if (selected is null || Version != version) return;
            using var input = File.OpenRead(selected.Path);
            if (input.Length > 16 * 1024 * 1024) throw new IOException("图标文件不能超过 16 MiB。");
            var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
            if (decoder.PixelWidth is 0 or > 4096 || decoder.PixelHeight is 0 or > 4096)
                throw new IOException("图标尺寸不能超过 4096 × 4096。");
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            using var output = new MemoryStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
            encoder.SetSoftwareBitmap(bitmap);
            double scale = Math.Min(1, 256d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            encoder.BitmapTransform.ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale));
            encoder.BitmapTransform.ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale));
            await encoder.FlushAsync();
            if (Version != version) return;
            VersionManagementService.SaveIcon(root, version, output.ToArray());
            RefreshVersion();
            ShowError(null);
        }
        catch (Exception ex) { ShowError("未修改图标：" + ex.Message); }
        finally { BasicPanel.IsEnabled = true; }
    }

    /// <summary>一次点击由资源管理器打开版本或实际隔离/共享游戏子目录；缺失子目录创建后打开，失败在当前页显示原因。</summary>
    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Version is null || _folders?.Selected is not { } root || sender is not Button { Tag: string category }) return;
        try
        {
            string path = VersionManagementService.GetQuickFolder(root, Version, category, _settings!.Current.Isolation);
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder)) throw new IOException("系统未能打开文件夹。");
            ShowError(null);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    /// <summary>显示当前目录、改名或图标操作失败原因，传入 null 时清除错误条。</summary>
    public void ShowError(string? error)
    {
        Status.Message = error ?? "";
        Status.IsOpen = error is not null;
    }
}
