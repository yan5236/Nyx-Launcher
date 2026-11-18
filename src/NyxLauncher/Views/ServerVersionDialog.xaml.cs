using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>一张启动版本卡片：图标取自实例目录，版本名、副标题小字和默认标记随实例变化。</summary>
public sealed record ServerVersionItem(ServerVersion Version, bool IsDefault)
{
    public string Name => Version.Version.Name;
    public string Detail => Version.Version.LaunchDetail;
    public Visibility DefaultVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;
    public string AccessibleName => Version.Version.AccessibleName + "，" + Version.Folder + (IsDefault ? "，当前默认版本" : "");
}

/// <summary>已安装版本卡片弹窗；单击卡片立即选用并关闭，不用再次确认。</summary>
public sealed partial class ServerVersionDialog : ContentDialog
{
    private readonly string? _defaultPath;
    private CancellationTokenSource? _scan;

    /// <summary>用户点选的实例；关闭或取消时保持为 null。</summary>
    public ServerVersion? Selected { get; private set; }

    /// <summary>从共享配置取已登记目录和当前选择；弹窗内的切换只影响本次列表，关闭时取消在途扫描。</summary>
    public ServerVersionDialog(XamlRoot root, GameFolderStore folders, string? defaultPath = null)
    {
        InitializeComponent();
        XamlRoot = root;
        _defaultPath = defaultPath;
        DialogContent.Width = Math.Clamp(root.Size.Width - 160, 280, 460);
        DialogContent.MaxHeight = Math.Clamp(root.Size.Height - 180, 220, 520);
        if (folders.Folders.Count == 0) Empty.Text = "请先在版本管理中添加游戏目录";
        GameDirectories.ItemsSource = folders.Folders;
        GameDirectories.IsEnabled = folders.Folders.Count > 0;
        GameDirectories.SelectedItem = folders.Selected;
        Closed += (_, _) => _scan?.Cancel();
    }

    /// <summary>切换目录时立即清空旧卡片并取消旧扫描，只显示本次目录的有效实例；空目录和读取错误在原位置提示。</summary>
    private async void GameDirectories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _scan?.Cancel();
        Versions.ItemsSource = null;
        Empty.Visibility = Visibility.Visible;
        if (GameDirectories.SelectedItem is not string folder)
        {
            Empty.Text = GameDirectories.Items.Count == 0 ? "请先在版本管理中添加游戏目录" : "请选择游戏目录";
            return;
        }

        Empty.Text = "正在读取此目录的版本…";
        using var request = new CancellationTokenSource();
        _scan = request;
        try
        {
            var versions = await ServerVersion.ScanFolderAsync(folder, request.Token);
            request.Token.ThrowIfCancellationRequested();
            Versions.ItemsSource = versions.Select(version => new ServerVersionItem(version,
                string.Equals(version.Version.DirectoryPath, _defaultPath, StringComparison.OrdinalIgnoreCase))).ToArray();
            Empty.Text = "此目录没有已安装的版本";
            Empty.Visibility = versions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!request.IsCancellationRequested) Empty.Text = "无法读取此游戏目录";
        }
        finally
        {
            if (ReferenceEquals(_scan, request)) _scan = null;
        }
    }

    /// <summary>保存点击的实例并立即关闭；鼠标、触摸和键盘激活走同一路径。</summary>
    private void Version_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ServerVersionItem item) Use(item);
    }

    /// <summary>选用指定卡片并关闭弹窗，供点击与验收共用。</summary>
    internal void Use(ServerVersionItem item)
    {
        Selected = item.Version;
        Hide();
    }

    /// <summary>仅关闭弹窗，不改变调用方的默认版本或本次启动选择。</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
