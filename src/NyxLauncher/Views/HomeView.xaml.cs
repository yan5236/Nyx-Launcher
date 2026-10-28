using Microsoft.UI.Xaml.Controls;

namespace NyxLauncher.Views;

/// <summary>展示首页风景与启动区域，版本选项为静态原型数据。</summary>
public sealed partial class HomeView : UserControl
{
    /// <summary>加载首页控件；不查找游戏安装，也不执行启动操作。</summary>
    public HomeView()
    {
        InitializeComponent();
    }
}
