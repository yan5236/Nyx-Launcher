using Microsoft.UI.Xaml.Controls;

namespace NyxLauncher;

/// <summary>启动器首页，作为后续业务界面的入口。</summary>
public sealed partial class MainPage : Page
{
    /// <summary>加载首页 XAML 控件；当前仅展示项目初始化状态。</summary>
    public MainPage()
    {
        InitializeComponent();
    }
}
