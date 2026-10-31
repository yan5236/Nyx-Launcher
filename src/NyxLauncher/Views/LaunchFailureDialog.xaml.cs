using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>显示单次启动失败的实际原因和日志入口；打开日志后保留弹窗，确定、叉号或 Escape 关闭。</summary>
public sealed partial class LaunchFailureDialog : ContentDialog
{
    private readonly string? _path;
    private readonly GameCrashReport _report;

    /// <summary>绑定已结束会话的失败快照；没有成功创建日志时禁用入口并明确显示日志保存错误。</summary>
    public LaunchFailureDialog(XamlRoot root, GameLaunchSession session, GameCrashReport report)
    {
        InitializeComponent();
        XamlRoot = root; _report = report; _path = report.PrimaryPath;
        Heading.Text = session.Crashed ? "崩溃" : "启动失败";
        Reason.Text = session.Status.Message + (session.Crashed ? "\n\n" + report.Diagnosis : "");
        HelpHint.Visibility = session.Crashed ? Visibility.Visible : Visibility.Collapsed;
        OpenLog.IsEnabled = _path is not null && File.Exists(_path);
        OpenFolder.IsEnabled = _path is not null && Directory.Exists(Path.GetDirectoryName(_path));
        ExportLog.IsEnabled = report.Logs.Count > 0;
        Error.Message = report.Warning + (report.Logs.Count == 0 ? "本次未生成可用的游戏日志。" : "");
        Error.IsOpen = Error.Message.Length > 0;
    }

    /// <summary>叉号只关闭当前失败提示，不改变版本选择或再次启动。</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>以独立参数把本次日志交给系统记事本，保留空格路径；打开失败在弹窗内提示。</summary>
    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_path is null || !File.Exists(_path)) throw new IOException("游戏日志文件已不存在。");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe")) { UseShellExecute = false };
            start.ArgumentList.Add(_path);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    /// <summary>打开本次日志所在文件夹；目录移走或系统拒绝打开时保留弹窗及具体原因。</summary>
    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(_path)!);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder)) throw new IOException("系统未能打开日志文件夹。");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    /// <summary>展示日志入口操作错误，不覆盖最初的启动失败原因。</summary>
    private void ShowError(Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }

    /// <summary>调用绑定当前窗口的系统保存对话框；取消不写文件，ZIP 导出失败保留弹窗并显示原因。</summary>
    private async void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        ExportLog.IsEnabled = false;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedFileName = "Minecraft-logs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                DefaultFileExtension = ".zip"
            };
            picker.FileTypeChoices.Add("ZIP 压缩文件", new List<string> { ".zip" });
            var file = await picker.PickSaveFileAsync();
            if (file is not null) await Task.Run(() => _report.Export(file.Path));
        }
        catch (Exception ex) { ShowError(ex); }
        finally { ExportLog.IsEnabled = true; }
    }
}
