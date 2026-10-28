using Microsoft.UI.Xaml.Controls;

namespace NyxLauncher.Views;

/// <summary>展示已安装版本列表的视觉原型，列表选择仅改变当前高亮项。</summary>
public sealed partial class VersionsView : UserControl
{
    /// <summary>加载静态版本示例；不读取、导入或修改本机游戏文件。</summary>
    public VersionsView()
    {
        InitializeComponent();
    }
}
