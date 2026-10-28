using Microsoft.UI.Xaml.Controls;

namespace NyxLauncher.Views;

/// <summary>展示游戏与模组加载器下载入口，版本号仅用于布局预览。</summary>
public sealed partial class DownloadsView : UserControl
{
    /// <summary>加载下载页原型；不获取远程版本清单或发起下载。</summary>
    public DownloadsView()
    {
        InitializeComponent();
    }
}
