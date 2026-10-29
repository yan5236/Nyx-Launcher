using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>真实加载器清单弹窗；使用 ContentDialog 的模态焦点、Esc 关闭及系统开关动画。</summary>
public sealed partial class LoaderVersionDialog : ContentDialog
{
    public LoaderVersion? SelectedVersion { get; private set; }
    public FabricApiVersion? SelectedApi { get; private set; }

    /// <summary>绑定当前游戏已获取的真实版本；根据窗口限制内容尺寸，长列表滚动显示，不增加确认步骤。</summary>
    public LoaderVersionDialog(XamlRoot root, IReadOnlyList<LoaderVersion> versions) : this(root, versions, "加载器") { }

    /// <summary>复用相同列表显示 API 的版本与发布日期，选择结果保留独立模组类型。</summary>
    public LoaderVersionDialog(XamlRoot root, IReadOnlyList<FabricApiVersion> versions) : this(root, versions, "Fabric API") { }

    /// <summary>统一配置原生弹窗尺寸和辅助技术名称，两种列表均使用版本、详情、图标显示字段。</summary>
    private LoaderVersionDialog(XamlRoot root, System.Collections.IEnumerable versions, string title)
    {
        InitializeComponent();
        XamlRoot = root;
        DialogContent.Width = Math.Clamp(root.Size.Width - 100, 240, 400);
        DialogContent.Height = Math.Clamp(root.Size.Height - 180, 160, 460);
        Versions.ItemsSource = versions;
        DialogTitle.Text = "选择" + title + "版本";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, DialogTitle.Text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Versions, title + "版本列表");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CloseButton, "关闭" + title + "版本选择");
    }

    /// <summary>保存点击的加载器或 API 发布并立即关闭；键盘激活走同一路径，不混淆两种安装类型。</summary>
    private void Version_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LoaderVersion version) SelectedVersion = version;
        else if (e.ClickedItem is FabricApiVersion api) SelectedApi = api;
        else return;
        Hide();
    }

    /// <summary>仅关闭弹窗，不改变安装页的加载器选择。</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
