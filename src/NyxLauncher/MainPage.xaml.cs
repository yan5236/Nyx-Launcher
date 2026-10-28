using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NyxLauncher;

/// <summary>承载原型导航、三个主页面以及账户菜单；不访问游戏文件或账户服务。</summary>
public sealed partial class MainPage : Page
{
    /// <summary>加载页面并选中首页；所有示例内容仅保存在当前界面中。</summary>
    public MainPage()
    {
        InitializeComponent();
        Navigation.SelectedItem = HomeItem;
    }

    /// <summary>根据选中导航项的标签切换可见页面；清除选择时保留当前内容。</summary>
    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string page })
        {
            ShowPage(page);
        }
    }

    /// <summary>从底部弹层打开账户原型，并清除主导航的选中状态。</summary>
    private void Account_Click(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedItem = null;
        ShowPage("account");
    }

    /// <summary>从底部弹层打开设置原型，并清除主导航的选中状态。</summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedItem = null;
        ShowPage("settings");
    }

    /// <summary>只显示指定标签对应的界面；不加载数据，也不持久化任何设置。</summary>
    /// <param name="page">由本页导航提供的 home、versions、downloads、account 或 settings 标签。</param>
    private void ShowPage(string page)
    {
        HomeContent.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        VersionsContent.Visibility = page == "versions" ? Visibility.Visible : Visibility.Collapsed;
        DownloadsContent.Visibility = page == "downloads" ? Visibility.Visible : Visibility.Collapsed;
        AccountContent.Visibility = page == "account" ? Visibility.Visible : Visibility.Collapsed;
        SettingsContent.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }
}
