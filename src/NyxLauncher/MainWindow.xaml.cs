using Microsoft.UI.Xaml;

namespace NyxLauncher;

/// <summary>承载启动器标题栏与页面容器的桌面主窗口。</summary>
public sealed partial class MainWindow : Window
{
    private bool _closing;
    private bool _readyToClose;
    /// <summary>加载主窗口、设置标题栏与图标，以 1040×680 的初始尺寸显示原型首页。</summary>
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 680));
        RootFrame.Navigate(typeof(MainPage));
        AppWindow.Closing += Window_Closing;
    }

    /// <summary>关闭窗口前停止本会话下载和游戏并等待进程退出，防止遗留后台工作或失去游戏停止入口。</summary>
    private async void Window_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_readyToClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        try { if (RootFrame.Content is MainPage page) await page.StopDownloadsAsync(); }
        finally { _readyToClose = true; Close(); }
    }
}
