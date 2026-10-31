using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;
using Microsoft.Windows.Storage.Pickers;

namespace NyxLauncher.Views;

/// <summary>显示可兼容的本地版本及真实 mods 路径；点选后创建现有下载任务，失败保留弹窗显示原因。</summary>
public sealed partial class ModInstallDialog : ContentDialog
{
    private readonly Action<InstalledVersion> _install;
    private readonly Action<string> _downloadToFolder;
    private bool _closed;

    /// <summary>绑定安装候选和两种任务回调；按窗口限制列表高度，无候选仍允许手动选目录。关闭后忽略文件夹选择器迟到结果。</summary>
    public ModInstallDialog(XamlRoot root, string fileName, IReadOnlyList<ModInstallTarget> targets, Action<InstalledVersion> install, Action<string> downloadToFolder)
    {
        InitializeComponent();
        XamlRoot = root;
        Title = "选择安装版本";
        FileName.Text = fileName;
        DialogContent.Width = Math.Clamp(root.Size.Width - 100, 240, 460);
        DialogContent.MaxHeight = Math.Max(160, root.Size.Height - 220);
        Targets.ItemsSource = targets;
        EmptyHint.Visibility = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _install = install;
        _downloadToFolder = downloadToFolder;
        Closed += (_, _) => _closed = true;
    }

    /// <summary>通过当前窗口的系统选择器选择直接下载目录；取消不创建任务，失败保留弹窗，关闭后的选择结果丢弃。</summary>
    private async void ChooseDirectory_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        IsPrimaryButtonEnabled = Targets.IsEnabled = false;
        Error.IsOpen = false;
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { CommitButtonText = "下载到此文件夹" };
            var folder = await picker.PickSingleFolderAsync();
            if (_closed || folder is null) return;
            _downloadToFolder(folder.Path);
            Hide();
        }
        catch (Exception ex) { if (!_closed) { Error.Message = ex.Message; Error.IsOpen = true; } }
        finally { IsPrimaryButtonEnabled = Targets.IsEnabled = true; }
    }

    /// <summary>点击即尝试创建任务，只有成功才关闭；重复点击由列表禁用及共享任务管理器共同保护。</summary>
    private void Target_Click(object sender, ItemClickEventArgs e)
    {
        if (!Targets.IsEnabled || e.ClickedItem is not ModInstallTarget target) return;
        Targets.IsEnabled = false;
        try { _install(target.Version); Hide(); }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
        finally { Targets.IsEnabled = true; }
    }
}

/// <summary>安装卡片同时展示版本与隔离规则计算出的目录，便于用户识别共享 mods。</summary>
public sealed record ModInstallTarget(InstalledVersion Version, string ModsPath)
{
    public string AccessibleName => Version.AccessibleName + "，安装到 " + ModsPath;
}
