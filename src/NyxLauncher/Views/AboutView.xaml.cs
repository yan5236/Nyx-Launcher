using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>展示版本、协议及第三方项目；仅用户点击检查更新时联网，不执行自动检查或更新。</summary>
public sealed partial class AboutView : UserControl
{
    private DownloadTaskManager? _tasks;
    public string VersionText { get; } = "版本 " + LauncherInfo.Version;
    public IReadOnlyList<AboutDependency> Dependencies { get; }

    /// <summary>读取随程序集嵌入的项目与许可证清单并创建控件；缺失或损坏的构建资源会抛出异常，避免展示不完整的致谢。</summary>
    public AboutView()
    {
        using var stream = typeof(AboutView).Assembly.GetManifestResourceStream("NyxLauncher.ThirdPartyNotices.json")
            ?? throw new InvalidOperationException("缺少第三方组件清单。");
        Dependencies = JsonSerializer.Deserialize<AboutDependency[]>(stream)
            ?? throw new InvalidOperationException("第三方组件清单为空。");
        InitializeComponent();
    }

    /// <summary>接入主页面共享任务，供用户手动更新；不创建第二套传输器或后台检查。</summary>
    public void ConfigureUpdates(DownloadTaskManager tasks) => _tasks = tasks;

    /// <summary>打开更新说明弹窗；用户确认后提示前往现有下载任务列表，重复点击期间禁用入口。</summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs args)
    {
        CheckUpdate.IsEnabled = false;
        try
        {
            var dialog = new LauncherUpdateDialog(XamlRoot, _tasks);
            await dialog.ShowThemedAsync();
            if (dialog.TaskCreated) UpdateStatus.Text = "已创建更新任务，可在「下载 → 任务」查看进度。";
        }
        finally { CheckUpdate.IsEnabled = true; }
    }

    /// <summary>在系统浏览器中打开按钮绑定的 HTTPS 官方或许可链接；打开失败或系统异常时在当前页报告，不修改应用或账户配置。</summary>
    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        LinkError.IsOpen = false;
        try
        {
            if (sender is not HyperlinkButton { Tag: string target } ||
                !Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("链接地址无效。");
            if (!await Windows.System.Launcher.LaunchUriAsync(uri))
                throw new InvalidOperationException("无法打开浏览器，请检查系统的默认浏览器设置。");
        }
        catch (Exception ex)
        {
            LinkError.Message = ex.Message;
            LinkError.IsOpen = true;
        }
    }
}

/// <summary>按上游项目归并的致谢条目；Packages 保存具体 NuGet 包及版本，供检查脚本防止升级后遗漏。</summary>
public sealed record AboutDependency(string Name, string Version, string Description, string ProjectUrl,
    string License, string LicenseUrl, string[] Packages);
