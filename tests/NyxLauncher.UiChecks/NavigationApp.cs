using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NyxLauncher.Views;

namespace NyxLauncher;

/// <summary>使用正式主页面验证转场及快速导航；读取本机配置，不安装游戏或修改用户偏好。</summary>
public partial class App : Application
{
    private Window? _window;
    private readonly string _log = Path.Combine(AppContext.BaseDirectory, "navigation-checks.log");

    /// <summary>加载正式应用样式，使检查使用与启动器相同的控件模板。</summary>
    public App() => InitializeComponent();

    /// <summary>检查主页面与 Tab 的切换、动画中间态、重复选择和快速打断；失败记录异常并返回非零退出码，退出前停止后台任务。</summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainPage? page = null;
        File.WriteAllText(_log, "START\n");
        try
        {
            Themes.ThemeManager.Current.Initialize();
            page = new MainPage();
            _window = new Window { Content = page };
            _window.AppWindow.Resize(new(1040, 680));
            _window.Activate();
            await Task.Delay(500);
            var surface = Field<Grid>(page, "PageSurface");
            var offset = Field<TranslateTransform>(page, "PageOffset");
            var animation = Field<Storyboard>(page, "PageEntrance");
            Check(Field<object?>(page, "DownloadsContent") is null && Field<object?>(page, "SettingsContent") is null,
                "首次显示仍只创建首页");
            Check(surface.Opacity == 1 && offset.Y == 0, "首次显示没有透明或位移残留");
            Check(!surface.Children.Contains(Field<InfoBar>(page, "CrashToast")), "崩溃提示位于动画层之外");
            foreach (string destination in new[] { "versions", "downloads", "wardrobe", "account", "settings", "home" })
            {
                Show(page, destination);
                await Task.Delay(350);
                Check(surface.Children.OfType<FrameworkElement>().Count(child => child.Visibility == Visibility.Visible) == 1,
                    destination + " 仅一个页面可见");
                Check(surface.Opacity == 1 && offset.Y == 0 && surface.IsHitTestVisible,
                    destination + " 动画结束后正常显示并可交互");
            }
            Show(page, "account");
            await Task.Delay(70);
            if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                Check(surface.Opacity > 0 && surface.Opacity < 1 && offset.Y > 0 && offset.Y < 16,
                    "真实窗口中淡入与上移同时进行");
                var elapsed = animation.GetCurrentTime();
                Show(page, "account");
                Check(animation.GetCurrentTime() >= elapsed, "重复进入当前页不重播动画");
            }
            else Check(surface.Opacity == 1 && offset.Y == 0, "系统关闭动画时直接显示");
            for (int i = 0; i < 12; i++)
            {
                Show(page, i % 2 == 0 ? "home" : "account");
                await Task.Delay(20);
            }
            Show(page, "home");
            await Task.Delay(350);
            Check(Field<ScrollViewer>(page, "HomeScroll").Visibility == Visibility.Visible &&
                surface.Children.OfType<FrameworkElement>().Count(child => child.Visibility == Visibility.Visible) == 1,
                "快速连续切页以最后一次选择为准");
            Check(surface.Opacity == 1 && offset.Y == 0, "打断动画后没有透明或偏移残留");
            Show(page, "account");
            _window.Content = new Grid();
            await Task.Delay(100);
            Check(surface.Opacity == 1 && offset.Y == 0, "动画中卸载恢复默认视觉状态");
            await CheckTabsAsync();
            File.AppendAllText(_log, "PASS\n");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.AppendAllText(_log, "FAIL " + ex + "\n");
        }
        finally
        {
            if (page is not null) await page.StopDownloadsAsync();
            _window?.Close();
            Exit();
        }
    }

    /// <summary>直接操作四类真实视图的 SelectorBar，不注入业务服务，验证 Tab 切换与动画，避免测试触发下载或配置写入。</summary>
    private async Task CheckTabsAsync()
    {
        foreach (UserControl view in new UserControl[] { new SettingsView(), new DownloadsView(), new VersionDetailsView(), new WardrobeView() })
        {
            _window!.Content = view;
            await Task.Delay(350);
            var body = Field<TabContentPanel>(view, "TabBody");
            SelectorBar tabs = view switch
            {
                SettingsView => ((Grid)view.Content).Children.OfType<SelectorBar>().Single(),
                DownloadsView => Field<SelectorBar>(view, "ContentSelector"),
                VersionDetailsView => Field<SelectorBar>(view, "Tabs"),
                _ => Field<SelectorBar>(view, "CategoryTabs")
            };
            for (int i = 1; i < tabs.Items.Count; i++)
            {
                tabs.SelectedItem = tabs.Items[i];
                await Task.Delay(70);
                var offset = (TranslateTransform)body.RenderTransform;
                if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                    Check(body.Opacity > 0 && body.Opacity < 1 && offset.Y > 0 && offset.Y < 16,
                        view.GetType().Name + " / " + tabs.Items[i].Text + " 正文正在淡入上移");
                await Task.Delay(250);
                Check(body.Opacity == 1 && offset.Y == 0 && body.IsHitTestVisible && body.ActualHeight > 0,
                    "Tab 动画结束后正文尺寸和交互正常");
                if (view is SettingsView or VersionDetailsView)
                    Check(body.Children.OfType<FrameworkElement>().Count(child => child.Visibility == Visibility.Visible) == 1,
                        "仅选中 Tab 的正文可见");
            }
            if (view is DownloadsView)
            {
                var tasks = Field<SelectorBar>(view, "TaskSelector");
                tasks.SelectedItem = tasks.Items[0];
                Check(tabs.SelectedItem is null && Field<DownloadTasksView>(view, "TasksContent").Visibility == Visibility.Visible,
                    "下载任务与分类互斥且立即显示");
                await Task.Delay(70);
                if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                    Check(body.Opacity > 0 && body.Opacity < 1, "下载任务正文有过渡动画");
            }
            for (int i = 0; i < 10; i++)
            {
                tabs.SelectedItem = tabs.Items[i % 2];
                await Task.Delay(20);
            }
            tabs.SelectedItem = tabs.Items[0];
            await Task.Delay(350);
            Check(ReferenceEquals(tabs.SelectedItem, tabs.Items[0]) && body.Opacity == 1 &&
                ((TranslateTransform)body.RenderTransform).Y == 0, "快速切换 Tab 后最终选择和视觉状态正常");
            tabs.SelectedItem = tabs.Items[1];
            _window.Content = new Grid();
            await Task.Delay(70);
            Check(body.Opacity == 1 && ((TranslateTransform)body.RenderTransform).Y == 0, "Tab 动画卸载后归位");
            if (view is SettingsView settings) { settings.SetActive(false); await settings.StopAsync(); }
            if (view is VersionDetailsView details) details.SetActive(false);
            if (view is WardrobeView wardrobe) await wardrobe.StopAsync();
        }
    }

    /// <summary>调用正式导航入口，保留页面加载、停用及转场逻辑，不通过测试复制实现。</summary>
    private static void Show(MainPage page, string destination) => typeof(MainPage)
        .GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [destination]);

    /// <summary>读取主页面生成的命名控件或动画，供真实视觉属性断言使用。</summary>
    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(owner)!;

    /// <summary>记录业务和视觉结果；条件不成立时抛出异常使检查失败。</summary>
    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        File.AppendAllText(_log, "OK " + message + "\n");
    }
}
