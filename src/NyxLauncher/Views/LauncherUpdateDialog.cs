using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>手动检查更新的模态窗口；展示版本与发布说明，仅确认后提交共享下载任务。</summary>
public sealed class LauncherUpdateDialog : ContentDialog
{
    private readonly DownloadTaskManager? _tasks;
    private readonly CancellationTokenSource _request = new();
    private readonly ProgressRing _progress = new() { IsActive = true, Width = 28, Height = 28 };
    private readonly TextBlock _summary = new() { Text = "正在检查 GitHub 发布…", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private LauncherRelease? _release;
    public bool TaskCreated { get; private set; }

    /// <summary>创建可取消的检查弹窗，加载时才请求一次 GitHub；关闭取消请求，按钮不默认触发更新。</summary>
    public LauncherUpdateDialog(XamlRoot root, DownloadTaskManager? tasks)
    {
        XamlRoot = root;
        _tasks = tasks;
        Title = "检查更新";
        PrimaryButtonText = "更新";
        CloseButtonText = "关闭";
        IsPrimaryButtonEnabled = false;
        DefaultButton = ContentDialogButton.Close;
        var panel = new StackPanel { Spacing = 12, Width = 460, MaxWidth = 460 };
        panel.Children.Add(_progress);
        panel.Children.Add(_summary);
        panel.Children.Add(new ScrollViewer { Content = _notes, MaxHeight = 300, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var link = new HyperlinkButton { Content = "在 GitHub 查看发布记录" };
        link.Click += ReleaseLink_Click;
        panel.Children.Add(link);
        panel.Children.Add(_error);
        Content = panel;
        Loaded += Check_Loaded;
        Closed += (_, _) => { _request.Cancel(); _request.Dispose(); };
        PrimaryButtonClick += Update_Click;
    }

    /// <summary>读取并比较正式版，展示发布说明、未发布或已最新状态；失败保留关闭和 GitHub 入口，取消后不更新已关闭控件。</summary>
    private async void Check_Loaded(object sender, RoutedEventArgs args)
    {
        Loaded -= Check_Loaded;
        var token = _request.Token;
        try
        {
            _release = await LauncherUpdateService.FetchAsync(token);
            token.ThrowIfCancellationRequested();
            if (_tasks?.LauncherUpdateInstalled == true)
            { _summary.Text = "新版已安装，下次启动生效。当前窗口可继续使用。"; return; }
            if (_release is null) { _summary.Text = "暂时没有可用的正式发布。"; return; }
            bool newer = ReleaseVersion.Parse(_release.Version).CompareTo(ReleaseVersion.Parse(LauncherInfo.Version)) > 0;
            Title = newer ? "发现新版本 " + _release.Version : "当前已是最新版本";
            _summary.Text = $"当前版本 {LauncherInfo.Version} · 最新正式版 {_release.Version}";
            _notes.Text = _release.Notes;
            IsPrimaryButtonEnabled = newer && LauncherInfo.CanSelfUpdate && _tasks is not null;
            if (newer) _summary.Text += LauncherInfo.CanSelfUpdate
                ? "\n确认后将创建下载任务。完成后不关闭窗口，下次启动使用新版。"
                : "\n当前为目录/开发构建，请先从 GitHub 下载单文件发布版。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            _summary.Text = "检查更新失败，请稍后重试。";
            _error.Message = ex.Message; _error.IsOpen = true;
        }
        finally { if (!token.IsCancellationRequested) _progress.IsActive = false; }
    }

    /// <summary>确认时只创建共享任务，不等待下载、不退出或重启；重复目标及写入准备失败保留弹窗并提示。</summary>
    private void Update_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            if (_release is null || _tasks is null) throw new IOException("更新信息尚未就绪。");
            _tasks.AddLauncherUpdate(_release);
            TaskCreated = true;
        }
        catch (Exception ex) { args.Cancel = true; _error.Message = ex.Message; _error.IsOpen = true; }
    }

    /// <summary>打开已验证的具体发布页或官方发布列表，系统浏览器失败显示在弹窗内。</summary>
    private async void ReleaseLink_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(_release?.Page ?? new Uri(LauncherInfo.Repository + "/releases")))
                throw new IOException("无法打开默认浏览器。");
        }
        catch (Exception ex) { _error.Message = ex.Message; _error.IsOpen = true; }
    }
}
