using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>显示可兼容的本地版本及真实 mods 路径；点选后创建现有下载任务，失败保留弹窗显示原因。</summary>
public sealed partial class ModInstallDialog : ContentDialog
{
    private readonly Action<InstalledVersion> _install;

    /// <summary>绑定已验证的安装候选和任务回调；按窗口限制列表高度，没有候选时提供安装游戏的提示。</summary>
    public ModInstallDialog(XamlRoot root, string fileName, IReadOnlyList<ModInstallTarget> targets, Action<InstalledVersion> install)
    {
        InitializeComponent();
        XamlRoot = root;
        Title = "选择安装版本";
        FileName.Text = fileName;
        DialogContent.Width = Math.Clamp(root.Size.Width - 100, 240, 460);
        DialogContent.MaxHeight = Math.Max(160, root.Size.Height - 220);
        Targets.ItemsSource = targets;
        EmptyHint.Visibility = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _install = install;
    }

    /// <summary>点击即尝试创建任务，只有成功才关闭；重复点击由列表禁用及共享任务管理器共同保护。</summary>
    private void Target_Click(object sender, ItemClickEventArgs e)
    {
        if (!Targets.IsEnabled || e.ClickedItem is not ModInstallTarget target) return;
        Targets.IsEnabled = false;
        try { _install(target.Version); Hide(); }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
        finally { Targets.IsEnabled = true; }
    }
}

/// <summary>安装卡片同时展示版本与隔离规则计算出的目录，便于用户识别共享 mods。</summary>
public sealed record ModInstallTarget(InstalledVersion Version, string ModsPath)
{
    public string AccessibleName => Version.AccessibleName + "，安装到 " + ModsPath;
}
