using Microsoft.UI.Xaml;

namespace NyxLauncher;

/// <summary>管理启动器的应用生命周期，并持有主窗口以维持其生命周期。</summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>初始化应用级 XAML 资源，供窗口和页面加载时使用。</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>响应应用启动，初始化主题画刷，再创建并激活启动器主窗口。</summary>
    /// <param name="args">系统提供的启动参数；当前骨架不处理命令行参数。</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Themes.ThemeManager.Current.Initialize();
        _window = new MainWindow();
        _window.Activate();
    }
}
