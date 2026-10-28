using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>真实加载器清单弹窗；使用 ContentDialog 的模态焦点、Esc 关闭及系统开关动画。</summary>
public sealed partial class LoaderVersionDialog : ContentDialog
{
    public LoaderVersion? SelectedVersion { get; private set; }

    /// <summary>绑定当前游戏已获取的真实版本；根据窗口限制内容尺寸，长列表滚动显示，不增加确认步骤。</summary>
    public LoaderVersionDialog(XamlRoot root, IReadOnlyList<LoaderVersion> versions)
    {
        InitializeComponent();
        XamlRoot = root;
        DialogContent.Width = Math.Clamp(root.Size.Width - 100, 240, 400);
        DialogContent.Height = Math.Clamp(root.Size.Height - 180, 160, 460);
        Versions.ItemsSource = versions;
    }

    /// <summary>保存用户点击的真实版本并立即启动关闭动画；键盘激活列表项走同一路径。</summary>
    private void Version_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LoaderVersion version) return;
        SelectedVersion = version;
        Hide();
    }

    /// <summary>仅关闭弹窗，不改变安装页的加载器选择。</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
