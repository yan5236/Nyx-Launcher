using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>首次使用的协议提示；同意成功落盘才允许进入主页面，拒绝或关闭由主窗口退出。</summary>
public sealed class AgreementDialog : ContentDialog
{
    private readonly AgreementStore _store;
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };

    /// <summary>构造协议链接、明确同意勾选和退出按钮；不默认勾选，不发起后台网络请求。</summary>
    public AgreementDialog(XamlRoot root, AgreementStore store)
    {
        _store = store;
        XamlRoot = root;
        Title = "欢迎使用 Nyx Launcher";
        PrimaryButtonText = "同意并继续";
        CloseButtonText = "不同意并退出";
        DefaultButton = ContentDialogButton.Close;
        IsPrimaryButtonEnabled = false;
        var panel = new StackPanel { Spacing = 12, MaxWidth = 460 };
        panel.Children.Add(new TextBlock { Text = "请先阅读用户协议与隐私协议。启动器在本机保存账户和设置；登录、下载及地区判断会连接相应的第三方服务，详情见隐私协议。", TextWrapping = TextWrapping.Wrap });
        foreach (var (label, url) in new[] { ("阅读用户协议", LauncherInfo.AgreementUrl), ("阅读隐私协议", LauncherInfo.PrivacyUrl) })
        {
            var link = new HyperlinkButton { Content = label, Tag = url };
            link.Click += Link_Click;
            panel.Children.Add(link);
        }
        var consent = new CheckBox { Content = "我已阅读并同意用户协议和隐私协议" };
        consent.Checked += (_, _) => IsPrimaryButtonEnabled = true;
        consent.Unchecked += (_, _) => IsPrimaryButtonEnabled = false;
        panel.Children.Add(consent);
        _error.Message = store.LoadError ?? "";
        _error.IsOpen = store.LoadError is not null;
        panel.Children.Add(_error);
        Content = panel;
        PrimaryButtonClick += Accept_Click;
    }

    /// <summary>在系统浏览器打开对应 GitHub 文档，启动失败在弹窗中报告，不替用户勾选同意。</summary>
    private async void Link_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri((string)((HyperlinkButton)sender).Tag)))
                throw new IOException("无法打开默认浏览器。");
        }
        catch (Exception ex) { _error.Message = ex.Message; _error.IsOpen = true; }
    }

    /// <summary>明确同意后保存本地记录；写入失败阻止关闭，并让用户重试或退出。</summary>
    private void Accept_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var error = _store.Accept();
        args.Cancel = error is not null;
        _error.Message = error ?? "";
        _error.IsOpen = error is not null;
    }
}
