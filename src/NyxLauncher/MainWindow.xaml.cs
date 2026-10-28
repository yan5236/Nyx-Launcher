using Microsoft.UI.Xaml;

namespace NyxLauncher;

/// <summary>承载启动器标题栏与页面容器的桌面主窗口。</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>加载主窗口、设置标题栏和图标，并将首页放入页面容器。</summary>
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        RootFrame.Navigate(typeof(MainPage));
    }
}
