using Microsoft.UI.Xaml;

namespace NyxLauncher;

/// <summary>承载启动器标题栏与页面容器的桌面主窗口。</summary>
public sealed partial class MainWindow : Window
{
    private bool _closing;
    private bool _readyToClose;
    /// <summary>加载主窗口和主题，以 1040×680 显示；等待协议确认后才创建首页及业务服务。</summary>
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 680));
        Themes.ThemeManager.Current.Attach(ThemeRoot, WindowBackground, AppWindow);
        ThemeRoot.Loaded += Startup_Loaded;
        AppWindow.Closing += Window_Closing;
        InitializeMemoryLifecycle();
    }

    /// <summary>根视觉树就绪后显示首次协议；只有同意落盘且窗口仍打开时创建主页面，拒绝时正常退出。</summary>
    private async void Startup_Loaded(object sender, RoutedEventArgs args)
    {
        ThemeRoot.Loaded -= Startup_Loaded;
        var store = new Services.AgreementStore();
        if (!store.IsAccepted && await new Views.AgreementDialog(ThemeRoot.XamlRoot, store).ShowThemedAsync()
            != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        { Close(); return; }
        if (_closing) return;
        Services.LauncherUpdateInstaller.CleanupBackup(Services.LauncherInfo.ExecutablePath);
        RootFrame.Navigate(typeof(MainPage));
        if (RootFrame.Content is MainPage page) page.ConfigureLaunchBehavior(ApplyLaunchBehavior, RestoreFromTray);
    }

    /// <summary>响应窗口标题栏左上角的折叠按钮，将切换请求交给当前主页面。</summary>
    private void TitleBar_PaneToggleRequested(Microsoft.UI.Xaml.Controls.TitleBar sender, object args)
    {
        if (RootFrame.Content is MainPage page) page.ToggleNavigationPane();
    }

    /// <summary>拦截窗口关闭请求；托盘模式交给关闭选项处理，其他情况执行常规关闭。</summary>
    private async void Window_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_readyToClose) return;
        args.Cancel = true;
        if (_closing || _choosingTrayAction) return;
        if (RootFrame.Content is MainPage page && page.TrayClosePreference is { } preference)
        {
            await CloseInTrayModeAsync(page, preference);
            return;
        }
        await CloseLauncherAsync(leaveGameRunning: false);
    }

    /// <summary>禁用页面交互并等待后台工作收尾；从托盘退出时仅解除对已就绪游戏的监控。</summary>
    private async Task CloseLauncherAsync(bool leaveGameRunning)
    {
        if (_closing) return;
        _closing = true;
        RootFrame.IsEnabled = false;
        RemoveTrayIcon();
        try { if (RootFrame.Content is MainPage page) await page.StopDownloadsAsync(leaveGameRunning); }
        finally { _readyToClose = true; Close(); }
    }
}
