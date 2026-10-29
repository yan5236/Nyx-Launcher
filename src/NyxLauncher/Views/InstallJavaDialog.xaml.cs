using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>主版本、渠道、具体版本三步安装；元数据请求可取消，最终只创建统一下载任务。</summary>
public sealed partial class InstallJavaDialog : ContentDialog
{
    private readonly DownloadTaskManager _tasks;
    private readonly JavaRuntimeStore _store;
    private CancellationTokenSource? _loading;
    private Storyboard? _transition;
    private int _step;
    private int _major;
    private JavaChannel _channel;
    private bool _closed;
    internal DownloadTaskItem? CreatedTask { get; private set; }

    /// <summary>注入任务和 Java 状态，以同宽容器固定右上角关闭按钮；关闭取消版本加载，原生弹窗负责开关动画。</summary>
    public InstallJavaDialog(XamlRoot root, DownloadTaskManager tasks, JavaRuntimeStore store)
    {
        InitializeComponent(); XamlRoot = root; _tasks = tasks; _store = store;
        DialogContent.Width = Math.Clamp(root.Size.Width - 120, 340, 440);
        Closed += (_, _) => { _closed = true; _loading?.Cancel(); _transition?.Stop(); };
    }

    /// <summary>从主版本按钮的固定标签读取版本并直接进入渠道页；按钮不保留选中状态，关闭或其他步骤忽略迟到点击。</summary>
    private void Major_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _step != 0) return;
        _major = int.Parse((string)((Button)sender).Tag);
        ShowStep(1);
    }

    /// <summary>仅在第三步确认具体发行包后创建下载任务；失败保持弹窗以便重试。</summary>
    private void Install_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_step != 2 || Versions.SelectedItem is not JavaPackage package || CreatedTask is not null) return;
        try { CreatedTask = _tasks.AddJava(package, _store); args.Cancel = false; }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
    }

    /// <summary>渠道卡片进入第三步并实时加载正式发行列表；默认选中排序后的第一项。</summary>
    private void Channel_Click(object sender, RoutedEventArgs e)
    {
        _channel = Enum.Parse<JavaChannel>((string)((Button)sender).Tag);
        ShowStep(2); _ = LoadVersionsAsync();
    }

    /// <summary>失败时重新请求当前渠道；旧请求取消并由请求身份隔离迟到更新。</summary>
    private void Retry_Click(object sender, RoutedEventArgs e) => _ = LoadVersionsAsync();

    /// <summary>加载具体版本并选中最新项；只显示加载或空状态，成功后隐藏说明，返回或关闭取消请求。</summary>
    private async Task LoadVersionsAsync()
    {
        _loading?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _loading = cancellation;
        VersionLoading.Visibility = Visibility.Visible; RetryButton.Visibility = Visibility.Collapsed;
        Versions.ItemsSource = null; IsSecondaryButtonEnabled = false; Error.IsOpen = false;
        VersionHint.Visibility = Visibility.Visible; VersionHint.Text = "正在加载…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var versions = await new JavaCatalogService(http).FetchAsync(_major, _channel, cancellation.Token);
            if (_closed || _loading != cancellation || cancellation.IsCancellationRequested) return;
            Versions.ItemsSource = versions; Versions.SelectedIndex = versions.Count > 0 ? 0 : -1;
            VersionHint.Text = "暂无可用版本";
            VersionHint.Visibility = versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RetryButton.Visibility = versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed && _loading == cancellation)
            { Error.Message = "加载失败：" + ex.Message; Error.IsOpen = true; RetryButton.Visibility = Visibility.Visible; VersionHint.Visibility = Visibility.Collapsed; }
        }
        finally
        {
            if (_loading == cancellation)
            { _loading = null; if (!_closed) VersionLoading.Visibility = Visibility.Collapsed; }
        }
    }

    /// <summary>只有选中具体发行包才启用右侧安装按钮，清空或空结果禁止提交。</summary>
    private void Version_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (_step == 2) IsSecondaryButtonEnabled = Versions.SelectedItem is JavaPackage; }

    /// <summary>取消当前请求再退回上一步，保留已确定的主版本值；主版本页不显示选中状态，不影响下载任务。</summary>
    private void Back_Click(object sender, RoutedEventArgs e) { _loading?.Cancel(); ShowStep(Math.Max(0, _step - 1)); }

    /// <summary>关闭安装向导，关闭事件负责回收在途请求。</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>切换步骤，前两步点选卡片前进，仅第三步显示右侧安装按钮；沿用 220ms 动画并遵循系统开关。</summary>
    private void ShowStep(int step)
    {
        bool forward = step > _step; _step = step; Error.IsOpen = false;
        MajorPanel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChannelPanel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        VersionPanel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
        DialogTitle.Text = step switch { 0 => "选择 Java 主版本", 1 => $"Java {_major} · 选择渠道", _ => "选择可用 Java 版本" };
        SecondaryButtonText = step == 2 ? "安装 Java" : "";
        IsSecondaryButtonEnabled = false;
        DefaultButton = step == 2 ? ContentDialogButton.Secondary : ContentDialogButton.None;
        _transition?.Stop();
        if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            var opacity = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220) };
            var move = new DoubleAnimation { From = forward ? 24 : -24, To = 0, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(opacity, StepContent); Storyboard.SetTargetProperty(opacity, "Opacity");
            Storyboard.SetTarget(move, StepTranslation); Storyboard.SetTargetProperty(move, "X");
            _transition = new(); _transition.Children.Add(opacity); _transition.Children.Add(move); _transition.Begin();
        }
        if (step == 0) Major8.Focus(FocusState.Programmatic);
        else BackButton.Focus(FocusState.Programmatic);
    }
}
