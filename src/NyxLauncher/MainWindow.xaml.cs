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
        Themes.ThemeManager.Current.Attach(ThemeRoot, WindowBackground, AppWindow);
        RootFrame.Navigate(typeof(MainPage));
        AppWindow.Closing += Window_Closing;
    }

    /// <summary>响应窗口标题栏左上角的折叠按钮，将切换请求交给当前主页面。</summary>
    private void TitleBar_PaneToggleRequested(Microsoft.UI.Xaml.Controls.TitleBar sender, object args)
    {
        if (RootFrame.Content is MainPage page) page.ToggleNavigationPane();
    }

    /// <summary>关闭时先禁用页面交互，再停止下载和游戏并等待清理，防止等待期间创建新工作；重复关闭复用本轮等待。</summary>
    private async void Window_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_readyToClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        RootFrame.IsEnabled = false;
        try { if (RootFrame.Content is MainPage page) await page.StopDownloadsAsync(); }
        finally { _readyToClose = true; Close(); }
    }
}
