using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace NyxLauncher.Views;

public sealed partial class VersionLoadStatus : UserControl
{
    public event EventHandler? RetryRequested;
    private string _content = "版本";

    /// <summary>创建加载状态控件；由下载页控制动画生命周期。</summary>
    public VersionLoadStatus()
    {
        InitializeComponent();
        Failure.Completed += Failure_Completed;
    }

    /// <summary>交叉图标出现后停止已不可见的循环旋转，避免失败页持续消耗动画资源。</summary>
    private void Failure_Completed(object? sender, object e) => Spin.Stop();

    /// <summary>清除上次错误、使用当前主题副色并开始循环旋转；content 区分版本与资源，用于后续失败状态文本，默认兼容原调用。</summary>
    public void Start(string content = "版本")
    {
        Stop();
        RingBrush.Color = ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["SecondaryBrush"]).Color;
        Arc.Opacity = 1;
        Cross.Opacity = 0;
        ErrorDetails.Visibility = Visibility.Collapsed;
        _content = content;
        StatusText.Text = "正在加载";
        Spin.Begin();
    }

    /// <summary>先将旋转弧线变红，再淡出为交叉，显示当前内容的失败状态、传入的用户错误提示与重试按钮。</summary>
    public void ShowError(string message)
    {
        StatusText.Text = _content + "加载失败";
        ErrorText.Text = message;
        ErrorDetails.Visibility = Visibility.Visible;
        Failure.Begin();
    }

    /// <summary>停止隐藏页面的动画并清除动画持有的属性值。</summary>
    public void Stop()
    {
        Spin.Stop();
        Failure.Stop();
    }

    /// <summary>将用户重试请求交给下载页，不在状态控件中访问网络。</summary>
    private void Retry_Click(object sender, RoutedEventArgs e) => RetryRequested?.Invoke(this, EventArgs.Empty);
}
