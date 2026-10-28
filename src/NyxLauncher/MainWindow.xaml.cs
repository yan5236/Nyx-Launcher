using Microsoft.UI.Xaml;

namespace NyxLauncher;

/// <summary>承载启动器标题栏与页面容器的桌面主窗口。</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>加载主窗口、设置标题栏与图标，以 1040×680 的初始尺寸显示原型首页。</summary>
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 680));
        RootFrame.Navigate(typeof(MainPage));
    }
}
