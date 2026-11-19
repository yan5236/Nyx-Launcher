using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NyxLauncher.Services;
using NyxLauncher.Themes;
using WinRT.Interop;

namespace NyxLauncher;

public sealed partial class MainWindow
{
    private LauncherTrayIcon? _trayIcon;
    private bool _choosingTrayAction;

    /// <summary>游戏窗口已出现后按偏好操作启动器；系统托盘不可用时保留可访问的主窗口。</summary>
    private async void ApplyLaunchBehavior(LauncherAfterLaunch behavior)
    {
        if (_closing) return;
        if (behavior == LauncherAfterLaunch.Close)
        {
            await CloseLauncherAsync(leaveGameRunning: true);
            return;
        }
        if (behavior == LauncherAfterLaunch.MinimizeToTray) HideToTray();
    }

    /// <summary>注册图标后隐藏窗口并释放后台视觉资源；系统托盘不可用时保留窗口。</summary>
    private void HideToTray()
    {
        if (_closing || _choosingTrayAction || _trayIcon is not null) return;
        var icon = new LauncherTrayIcon(WindowNative.GetWindowHandle(this), DispatcherQueue, RestoreFromTray, ExitFromTray);
        if (!icon.TryShow()) { icon.Dispose(); return; }
        _trayIcon = icon;
        AppWindow.Hide();
        SetWindowForeground(false);
        ThemeManager.Current.SetTrayHidden(true);
        if (RootFrame.Content is MainPage page) page.ReleaseUnusedViews();
    }

    /// <summary>使用已记住的关闭行为，或询问缩回托盘与仅退出启动器；取消时保持窗口可见。</summary>
    private async Task CloseInTrayModeAsync(MainPage page, TrayCloseAction action)
    {
        if (action == TrayCloseAction.Ask)
        {
            var root = ThemeRoot.XamlRoot;
            if (VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup => popup.Child is ContentDialog)) return;
            _choosingTrayAction = true;
            try { action = await AskTrayCloseActionAsync(page, root); }
            finally { _choosingTrayAction = false; }
        }
        if (action == TrayCloseAction.MinimizeToTray) HideToTray();
        else if (action == TrayCloseAction.Exit) await CloseLauncherAsync(leaveGameRunning: true);
    }

    /// <summary>显示可取消的关闭弹窗；只有用户勾选记住且保存成功后才改变下次行为。</summary>
    private static async Task<TrayCloseAction> AskTrayCloseActionAsync(MainPage page, XamlRoot root)
    {
        var remember = new CheckBox { Content = "记住此选择" };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "关闭启动器窗口",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "退出启动器后，已启动的游戏会继续运行。", TextWrapping = TextWrapping.Wrap },
                    remember, error
                }
            },
            PrimaryButtonText = "最小化至托盘",
            SecondaryButtonText = "退出启动器",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) => Remember(TrayCloseAction.MinimizeToTray, args);
        dialog.SecondaryButtonClick += (_, args) => Remember(TrayCloseAction.Exit, args);
        return (await dialog.ShowThemedAsync()) switch
        {
            ContentDialogResult.Primary => TrayCloseAction.MinimizeToTray,
            ContentDialogResult.Secondary => TrayCloseAction.Exit,
            _ => TrayCloseAction.Ask
        };

        /// <summary>保存勾选的行为；写入失败时在同一个弹窗内提示，并允许取消勾选后继续。</summary>
        void Remember(TrayCloseAction action, ContentDialogButtonClickEventArgs args)
        {
            if (remember.IsChecked != true || page.SaveTrayCloseAction(action) is not { } message) return;
            error.Message = message;
            error.IsOpen = true;
            args.Cancel = true;
        }
    }

    /// <summary>从托盘点击或游戏退出时恢复同一个窗口；已手动恢复或正在关闭时直接返回。</summary>
    private void RestoreFromTray()
    {
        if (_closing || _trayIcon is null) return;
        RemoveTrayIcon();
        ThemeManager.Current.SetTrayHidden(false);
        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
            presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized) presenter.Restore();
        Activate();
    }

    /// <summary>右键菜单退出启动器并保留已启动的游戏；只响应当前托盘实例的操作。</summary>
    private async void ExitFromTray()
    {
        if (_trayIcon is not null) await CloseLauncherAsync(leaveGameRunning: true);
    }

    /// <summary>移除系统托盘图标并释放其原生窗口回调；重复调用安全。</summary>
    private void RemoveTrayIcon()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
