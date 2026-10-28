using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>单个真实版本的管理详情；查看元数据、打开目录和删除均针对当前卡片对应的版本。</summary>
public sealed partial class VersionDetailsView : UserControl
{
    public InstalledVersion? Version { get; private set; }
    public event EventHandler? BackRequested;
    public event EventHandler? DeleteRequested;

    /// <summary>初始化详情视图，不在构造时访问游戏文件。</summary>
    public VersionDetailsView() => InitializeComponent();

    /// <summary>显示选中版本的名称、元数据和完整路径，解析失败时展示具体错误而不虚构信息。</summary>
    public void Open(InstalledVersion version)
    {
        Version = version;
        VersionName.Text = version.Name;
        VersionInfo.Text = version.Detail;
        VersionPath.Text = version.DirectoryPath;
        ShowError(version.Error);
    }

    /// <summary>请求父页返回版本列表，保持搜索与文件夹选择。</summary>
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>请求父页执行统一删除确认，详情页自身不直接删除文件。</summary>
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>通过系统资源管理器打开真实版本目录；路径缺失、访问失败或系统拒绝打开时在页内显示原因。</summary>
    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Version is null) return;
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Version.DirectoryPath);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder)) throw new IOException("系统未能打开版本文件夹。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        { ShowError(ex.Message); }
    }

    /// <summary>展示目录读取或删除失败信息，并保留当前详情以便用户检查。</summary>
    public void ShowError(string? error)
    {
        Status.Message = error ?? "";
        Status.IsOpen = error is not null;
    }
}
