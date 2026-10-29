using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>管理已登记游戏目录中的真实版本，提供搜索、详情、回收站删除及下载导航。</summary>
public sealed partial class VersionsView : UserControl
{
    private GameFolderStore? _folders;
    private IReadOnlyList<InstalledVersion> _versions = Array.Empty<InstalledVersion>();
    private CancellationTokenSource? _scan;
    private bool _dialogOpen;
    public event EventHandler? InstallRequested;

    /// <summary>加载页面并连接详情返回与删除事件；业务目录由主页面在启动时注入。</summary>
    public VersionsView()
    {
        InitializeComponent();
        Details.BackRequested += Details_BackRequested;
        Details.DeleteRequested += Details_DeleteRequested;
    }

    /// <summary>共享启动时建立的目录设置，立即显示当前路径及初始化失败信息。</summary>
    public void Configure(GameFolderStore folders)
    {
        _folders = folders;
        RefreshPath();
        ShowError(folders.LoadError);
    }

    /// <summary>进入时重新扫描磁盘；离开时取消扫描并回到列表，旧异步结果不能覆盖后续页面。</summary>
    public void SetActive(bool active)
    {
        _scan?.Cancel();
        if (active) _ = RefreshAsync();
        else
        {
            Details.Visibility = Visibility.Collapsed;
            Catalog.Visibility = Visibility.Visible;
        }
    }

    /// <summary>更新可截断的路径按钮，悬停可查看完整路径；没有当前目录时提示用户选择。</summary>
    private void RefreshPath()
    {
        FolderPath.Text = _folders?.Selected ?? "选择文件夹";
        ToolTipService.SetToolTip(FolderButton, FolderPath.Text);
    }

    /// <summary>在后台读取当前目录，保留搜索条件；失败显示原因，空目录与无搜索结果分别提示。</summary>
    private async Task RefreshAsync()
    {
        _scan?.Cancel();
        using var request = new CancellationTokenSource();
        _scan = request;
        RefreshPath();
        _versions = Array.Empty<InstalledVersion>();
        ApplyFilter();
        ShowError(_folders?.LoadError);
        string? folder = _folders?.Selected;
        ScanProgress.IsActive = folder is not null;
        ScanProgress.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        if (folder is not null) Empty.Visibility = Visibility.Collapsed;
        try
        {
            if (folder is not null)
            {
                var versions = await Task.Run(() => InstalledVersionService.Scan(folder, request.Token), request.Token);
                request.Token.ThrowIfCancellationRequested();
                _versions = versions;
            }
            request.Token.ThrowIfCancellationRequested();
            ApplyFilter();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!request.IsCancellationRequested) { ShowError(ex.Message); Empty.Visibility = Visibility.Collapsed; } }
        finally
        {
            if (ReferenceEquals(_scan, request))
            {
                _scan = null;
                ScanProgress.IsActive = false;
                ScanProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>模态显示文件夹管理；选择成功立即关闭，关闭后同步已保存路径并刷新真实版本。</summary>
    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_folders is null || _dialogOpen) return;
        _dialogOpen = true;
        try { await new GameFolderDialog(XamlRoot, _folders).ShowAsync(); }
        finally { _dialogOpen = false; FolderButton.Focus(FocusState.Programmatic); }
        await RefreshAsync();
    }

    /// <summary>请求主导航打开游戏下载分类，不在版本管理页启动网络下载。</summary>
    private void Install_Click(object sender, RoutedEventArgs e) => InstallRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>对本次扫描结果即时搜索，输入或清空均不重新读取磁盘。</summary>
    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    /// <summary>按名称、游戏版本和加载器过滤；空选择、空目录及搜索无结果使用对应文案。</summary>
    private void ApplyFilter()
    {
        if (VersionList is null) return;
        string query = Search.Text.Trim();
        var filtered = _versions.Where(x => x.AccessibleName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        VersionList.ItemsSource = filtered;
        Empty.Text = _folders?.Selected is null ? "请选择游戏文件夹" : query.Length > 0 ? "没有匹配的版本" : "此文件夹中还没有版本";
        Empty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>点击卡片或键盘激活直接进入该版本详情；删除为独立按钮，不触发此入口。</summary>
    private void Version_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledVersion version }) return;
        OpenDetails(version);
    }

    /// <summary>供首页和列表共用的详情入口，展示传入版本的真实元数据与目录。</summary>
    public void OpenDetails(InstalledVersion version)
    {
        Details.Open(version);
        Catalog.Visibility = Visibility.Collapsed;
        Details.Visibility = Visibility.Visible;
    }

    /// <summary>返回列表并重新读取磁盘，保留原搜索条件。</summary>
    private void Details_BackRequested(object? sender, EventArgs e)
    {
        Details.Visibility = Visibility.Collapsed;
        Catalog.Visibility = Visibility.Visible;
        _ = RefreshAsync();
    }

    /// <summary>将详情页删除请求送入与卡片相同的确认与路径校验流程。</summary>
    private async void Details_DeleteRequested(object? sender, EventArgs e)
    {
        if (Details.Version is { } version) await DeleteAsync(version);
    }

    /// <summary>只处理行尾的删除按钮，删除前必须显示版本名与完整路径供用户确认。</summary>
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledVersion version }) await DeleteAsync(version);
    }

    /// <summary>使用共享绿色交互配色的弹窗确认后，将单个版本目录送入回收站；取消或校验失败保留列表，不回退为永久删除。</summary>
    private async Task DeleteAsync(InstalledVersion version)
    {
        if (_dialogOpen || _folders?.Selected is not { } folder) return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "删除版本“" + version.Name + "”？",
                Content = "将以下版本文件夹移入回收站，其中的独立存档、模组和设置也会一起移入。共享资源和其他版本不受影响。\n\n" + version.DirectoryPath,
                PrimaryButtonText = "移入回收站", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("ms-appx:///Themes/AccentButtonResources.xaml")
            });
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            string target = await Task.Run(() => InstalledVersionService.GetDeletionTarget(folder, version));
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(target,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            Details.Visibility = Visibility.Collapsed;
            Catalog.Visibility = Visibility.Visible;
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException or System.Runtime.InteropServices.COMException)
        {
            if (Details.Visibility == Visibility.Visible) Details.ShowError("未删除版本：" + ex.Message);
            else ShowError("未删除版本：" + ex.Message);
        }
        finally { _dialogOpen = false; }
    }

    /// <summary>以页面内错误条报告可恢复失败，避免静默丢失目录或版本状态。</summary>
    private void ShowError(string? error)
    {
        Status.Message = error ?? "";
        Status.IsOpen = error is not null;
    }
}
