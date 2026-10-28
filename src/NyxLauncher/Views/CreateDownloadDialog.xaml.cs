using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

public sealed partial class CreateDownloadDialog : ContentDialog
{
    private readonly DownloadTaskManager _tasks;
    private string? _directory;

    /// <summary>绑定共享任务管理器与当前窗口；不自动选择游戏目录作为普通文件下载目录。</summary>
    public CreateDownloadDialog(XamlRoot root, DownloadTaskManager tasks)
    { InitializeComponent(); XamlRoot = root; _tasks = tasks; }

    /// <summary>调用当前窗口的原生目录选择器；取消保留原选择，路径错误显示在弹窗内。</summary>
    private async void ChooseDirectory_Click(object sender, RoutedEventArgs e)
    {
        DirectoryButton.IsEnabled = false;
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            _directory = folder.Path;
            DirectoryText.Text = folder.Path;
            ToolTipService.SetToolTip(DirectoryButton, folder.Path);
        }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
        finally { DirectoryButton.IsEnabled = true; }
    }

    /// <summary>同步验证并创建任务后关闭；失败阻止对话框关闭，让用户修正链接、目录或同名冲突。</summary>
    private void Download_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try { _tasks.AddFile(DownloadUrl.Text, _directory ?? ""); }
        catch (Exception ex) { args.Cancel = true; Error.Message = ex.Message; Error.IsOpen = true; }
    }
}
