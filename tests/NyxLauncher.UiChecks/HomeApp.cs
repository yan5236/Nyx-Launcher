using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Services;
using NyxLauncher.Views;
using Windows.Graphics.Imaging;

namespace NyxLauncher;

/// <summary>用合成版本目录验证首页启动台的尺寸规则、选择器与进度交接，保存真实控件截图。</summary>
public partial class App : Application
{
    private Window? _window;
    private readonly string _log = Path.Combine(AppContext.BaseDirectory, "home-checks.log");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const double DefaultWidth = 792;
    private const double DefaultHeight = 612;
    private const double WideWidth = 1672;
    private const double WideHeight = 1042;

    /// <summary>加载正式主题和控件资源。</summary>
    public App() => InitializeComponent();

    /// <summary>检查固定尺寸控件、画面尺寸上限、进度与选择器的交接和空目录，不读取或写入用户配置里的游戏数据。</summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nyx-home-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        File.WriteAllText(_log, "START\n");
        try
        {
            Themes.ThemeManager.Current.Initialize();
            string game = Path.Combine(temporary, ".minecraft");
            WriteVersion(game, "1.20.1", """{"id":"1.20.1","type":"release"}""");
            WriteVersion(game, "Fabric 生存", """{"id":"custom","inheritsFrom":"1.20.1","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.10"}]}""");
            WriteVersion(game, "NeoForge 21.1", """{"id":"neo","inheritsFrom":"1.20.1","libraries":[{"name":"net.neoforged:neoforge:21.1.1"}]}""");
            WriteVersion(game, "All the Mods 10 生存服", """{"id":"atm","inheritsFrom":"1.20.1","libraries":[{"name":"net.minecraftforge:forge:1.20.1-47.4.0"}]}""");

            var root = new Grid { Width = DefaultWidth, Height = DefaultHeight, Background = (Brush)Resources["PageBrush"], RequestedTheme = ElementTheme.Light };
            var view = new HomeView();
            root.Children.Add(view);
            _window = new Window { Title = "Home UI checks", Content = root };
            _window.AppWindow.Resize(new(1700, 1060));
            _window.Activate();
            await Task.Delay(400);

            var folders = new GameFolderStore(Path.Combine(temporary, "folders.json"), temporary);
            var accounts = new AccountStore(Path.Combine(temporary, "accounts.json"));
            accounts.Add("Steve");
            view.Configure(folders, new DownloadSettingsStore(Path.Combine(temporary, "download.json")), accounts,
                new JavaRuntimeStore(Path.Combine(temporary, "java.json")));
            ApplyPalette(root, dark: false);
            Resize(view, root, DefaultWidth, DefaultHeight);
            await Task.Delay(700);

            var list = Field<ComboBox>(view, "VersionList");
            var launch = Field<Button>(view, "LaunchButton");
            var deck = Field<Border>(view, "Deck");
            Check(list.Items.Count == 4 && list.SelectedItem is InstalledVersion, "首页选择器列出全部已安装版本并恢复选择");
            Check(Math.Abs(launch.ActualWidth - 196) < .5 && Math.Abs(launch.ActualHeight - 56) < .5,
                $"默认窗口下启动按钮固定 196×56，实际 {launch.ActualWidth}×{launch.ActualHeight}");
            Check(Math.Abs(deck.ActualWidth - (DefaultWidth - 72)) < 1, "默认窗口下控制台宽度随内容区：" + deck.ActualWidth);
            Check(Field<Button>(view, "ManageButton").Visibility == Visibility.Visible, "默认窗口显示版本管理入口");
            Check(Field<StackPanel>(view, "Greeting").Visibility == Visibility.Visible &&
                Field<TextBlock>(view, "GreetingTitle").Text.Length > 0, "首页显示时段问候语");
            Check(NoAmbientInfo(view), "首页不显示账户、Java 或目录等与启动无关的信息");
            Check(!Field<InfoBar>(view, "Status").IsOpen, "正常状态不占用提示区域");
            await SnapshotAsync(root, "home-light-1040.png");

            Resize(view, root, WideWidth, WideHeight);
            await Task.Delay(500);
            Check(Math.Abs(launch.ActualWidth - 196) < .5 && Math.Abs(launch.ActualHeight - 56) < .5,
                $"最大化后启动按钮仍为 196×56，实际 {launch.ActualWidth}×{launch.ActualHeight}");
            Check(Math.Abs(deck.ActualWidth - 880) < 1, "最大化后控制台宽度上限 880：" + deck.ActualWidth);
            await SnapshotAsync(root, "home-light-1920.png");

            Resize(view, root, DefaultWidth, DefaultHeight);
            root.RequestedTheme = ElementTheme.Dark;
            ApplyPalette(root, dark: true);
            await Task.Delay(300);
            await SnapshotAsync(root, "home-dark-1040.png");
            root.RequestedTheme = ElementTheme.Light;
            ApplyPalette(root, dark: false);
            await Task.Delay(200);

            Invoke(view, "BeginProgress");
            Invoke(view, "ShowProgress", new LaunchStatus(LaunchStage.WaitingForWindow, "等待游戏窗口出现"));
            await Task.Delay(350);
            Check(Field<Grid>(view, "LaunchProgress").Visibility == Visibility.Visible && list.Visibility == Visibility.Collapsed,
                "启动时进度接管选择器位置，不新增占位行");
            Check(Math.Abs(Field<ProgressBar>(view, "StageProgress").Value - 94) < .5, "进度显示真实阶段里程碑");
            await SnapshotAsync(root, "home-launching-1040.png");
            Invoke(view, "ShowProgress", new LaunchStatus(LaunchStage.Exited, "已停止启动"));
            await Task.Delay(500);
            Check(list.Visibility == Visibility.Visible && Field<Grid>(view, "LaunchProgress").Visibility == Visibility.Collapsed,
                "启动结束把位置交还选择器");

            Resize(view, root, 520, 560);
            await Task.Delay(300);
            Check(Math.Abs(launch.ActualWidth - 196) < .5, "窄窗口主按钮仍为固定尺寸：" + launch.ActualWidth);
            Check(Field<Button>(view, "ManageButton").Visibility == Visibility.Collapsed, "窄窗口隐藏次要按钮");
            Check(deck.Width <= 520 - 72 + .5, "窄窗口控制台不超出内容宽度");

            var emptyView = new HomeView();
            root.Children.Clear();
            root.Children.Add(emptyView);
            emptyView.Configure(new GameFolderStore(Path.Combine(temporary, "empty-folders.json"), Path.Combine(temporary, "empty")),
                new DownloadSettingsStore(Path.Combine(temporary, "download.json")),
                new AccountStore(Path.Combine(temporary, "accounts.json")), new JavaRuntimeStore(Path.Combine(temporary, "java.json")));
            Resize(emptyView, root, DefaultWidth, DefaultHeight);
            await Task.Delay(700);
            Check(Field<ComboBox>(emptyView, "VersionList").Items.Count == 0, "没有已安装版本时选择器为空");
            Check(Field<ComboBox>(emptyView, "VersionList").PlaceholderText == "请先安装游戏", "空目录显示安装提示");
            Check(!Field<Button>(emptyView, "LaunchButton").IsEnabled, "没有版本时启动按钮不可用");
            await SnapshotAsync(root, "home-empty-1040.png");

            File.AppendAllText(_log, "PASS\n");
        }
        catch (Exception ex) { Environment.ExitCode = 1; File.AppendAllText(_log, "FAIL " + ex + "\n"); }
        finally { _window?.Close(); Directory.Delete(temporary, true); Exit(); }
    }

    /// <summary>按窗口尺寸调用首页布局，并同步宿主容器尺寸，模拟真实内容区。</summary>
    private static void Resize(HomeView view, Grid root, double width, double height)
    {
        root.Width = width; root.Height = height;
        view.ResizeLayout(width, height);
    }

    /// <summary>直接切换共享画刷，避免依赖用户保存的外观设置。</summary>
    private static void ApplyPalette(FrameworkElement root, bool dark) =>
        Invoke(Themes.ThemeManager.Current, "UpdateBrushes", dark);

    /// <summary>首页只保留启动所需信息：不存在账户名、Java 或游戏目录文字。</summary>
    private static bool NoAmbientInfo(DependencyObject root)
    {
        foreach (var element in Descendants(root))
        {
            if (element is TextBlock { Text: { Length: > 0 } text } &&
                (text.Contains("Java", StringComparison.OrdinalIgnoreCase) || text.Contains("Steve") ||
                 text.Contains(".minecraft") || text.Contains("账户")))
                return false;
        }
        return true;
    }

    /// <summary>遍历真实视觉树，用于断言首页不再出现无关信息。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }

    /// <summary>直接捕获真实 XAML 视觉树到 PNG，失败会使验收失败而不生成伪造截图。</summary>
    private static async Task SnapshotAsync(FrameworkElement root, string name)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(root);
        var pixels = await bitmap.GetPixelsAsync();
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        using var stream = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "home-checks.log"), "shot " + name + "\n");
    }

    /// <summary>生成最小可解析的版本清单，供首页扫描。</summary>
    private static void WriteVersion(string game, string name, string json)
    {
        string directory = Path.Combine(game, "versions", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), json);
    }

    /// <summary>读取真实控件内部状态以断言布局与状态，不在产品中增加测试专用接口。</summary>
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    /// <summary>执行正式控件事件处理方法，验证同一业务路径的状态变化。</summary>
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    /// <summary>记录具体断言并在失败时中断验收。</summary>
    private void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); File.AppendAllText(_log, message + "\n"); }
}




