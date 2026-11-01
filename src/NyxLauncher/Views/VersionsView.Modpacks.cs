using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

public sealed partial class VersionsView
{
    private DownloadTaskManager? _packTasks;

    /// <summary>注入共享任务管理器，让文件选择和拖放入口使用同一安装流程。</summary>
    public void ConfigureModpacks(DownloadTaskManager tasks) => _packTasks = tasks;

    /// <summary>选择 ZIP 或 MRPACK 后读取清单；选择期间阻止重复打开，取消选择不创建任务。</summary>
    private async void ImportPack_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        try
        {
            _dialogOpen = true;
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            picker.FileTypeFilter.Add(".zip"); picker.FileTypeFilter.Add(".mrpack");
            var file = await picker.PickSingleFileAsync();
            _dialogOpen = false;
            if (file is not null) await ImportModpackAsync(file.Path);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _dialogOpen = false; }
    }

    /// <summary>后台识别本地包，预填包名并要求确认；无效包和目录错误显示在版本管理页，重复导入弹窗被忽略。</summary>
    public async Task ImportModpackAsync(string path)
    {
        if (_dialogOpen || _packTasks is null) return;
        _dialogOpen = true;
        try
        {
            var manifest = await Task.Run(() => ModpackManifest.Read(path));
            await new ModpackInstallDialog(XamlRoot, _packTasks, _folders?.Selected ?? "", manifest.Name, path).ShowThemedAsync();
        }
        catch (Exception ex) { ShowError("无法导入整合包：" + ex.Message); }
        finally { _dialogOpen = false; }
    }
}
