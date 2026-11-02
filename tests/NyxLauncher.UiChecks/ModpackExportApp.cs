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

/// <summary>用合成目录检查导出页真实控件、嵌套选择、格式约束及成功通知。</summary>
public partial class App : Application
{
    private Window? _window;
    private readonly string _log = Path.Combine(AppContext.BaseDirectory, "export-checks.log");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>加载产品正式主题及控件资源。</summary>
    public App() => InitializeComponent();

    /// <summary>执行导出交互并保存浅深色截图；只使用临时游戏目录，失败以非零退出码报告。</summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nyx-export-ui-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(_log, "START\n");
        try
        {
            Themes.ThemeManager.Current.Initialize();
            string game = Path.Combine(temporary, ".minecraft");
            string directory = Path.Combine(game, "versions", "Test");
            foreach (string folder in new[] { "mods", "config", "saves/新的世界", "screenshots", "shaderpacks", "schematics" })
                Directory.CreateDirectory(Path.Combine(directory, folder));
            File.WriteAllText(Path.Combine(directory, "Test.json"), """{"id":"Test","clientVersion":"1.21.1","type":"release"}""");
            File.WriteAllText(Path.Combine(directory, "mods", "demo.jar"), "demo");
            File.WriteAllText(Path.Combine(directory, "mods", "other.jar"), "other");
            File.WriteAllText(Path.Combine(directory, "saves", "新的世界", "level.dat"), "world");
            File.WriteAllText(Path.Combine(directory, "options.txt"), "options");
            File.WriteAllText(Path.Combine(directory, "servers.dat"), "servers");
            var version = InstalledVersionService.Scan(game).Single();
            var view = new ModpackExportView();
            var root = new Grid { Width = 780, Height = 960, Padding = new Thickness(20), Background = (Brush)Resources["PageBrush"], RequestedTheme = ElementTheme.Light };
            root.Children.Add(view);
            _window = new Window { Title = "Modpack export UI checks", Content = root };
            _window.AppWindow.Resize(new(840, 1040)); _window.Activate();
            view.Open(version, game, new(), () => false);
            await WaitUntil(() => Field<string?>(view, "_root") is not null);
            var exportButton = Field<Button>(view, "Export");
            Check(ReferenceEquals(exportButton.Style, Resources["DeepButtonStyle"]), "导出按钮复用现有 DeepButtonStyle");
            await Task.Delay(200);
            Check(exportButton.Background is SolidColorBrush buttonBrush && buttonBrush.Color == ((SolidColorBrush)Resources["AccentButtonBackground"]).Color,
                "导出按钮实际背景与主题主色一致");
            foreach (var state in new[] { ("PointerOver", "AccentButtonBackgroundPointerOver"), ("Pressed", "AccentButtonBackgroundPressed"), ("Normal", "AccentButtonBackground") })
            {
                VisualStateManager.GoToState(exportButton, state.Item1, false);
                await Task.Delay(150);
                var expected = ((SolidColorBrush)Resources[state.Item2]).Color;
                Check(Descendants(exportButton).OfType<ContentPresenter>().Any(x => x.Background is SolidColorBrush brush && brush.Color == expected),
                    "按钮模板实际呈现主题状态：" + state.Item1);
            }
            Field<TextBox>(view, "Author").Text = "测试作者";
            Field<TextBlock>(view, "DestinationPath").Text = temporary;
            Field<TextBox>(view, "PackName").Text = "测试:整合包";
            var tree = Field<StackPanel>(view, "FileTree");
            Check(tree.Parent is Border, "文件列表直接放在卡片内，没有独立滚动区域");
            Check(Field<Button>(view, "Destination").HorizontalAlignment == HorizontalAlignment.Stretch, "整个路径区域可点击选择文件夹");
            var mods = tree.Children.OfType<Expander>().Single(x => ((CheckBox)x.Header).Tag is ModpackExportEntry { RelativePath: "mods" });
            mods.IsExpanded = true;
            await Task.Delay(250);
            var checks = ((StackPanel)mods.Content).Children.OfType<CheckBox>().ToArray();
            Check(checks.Length == 2, "展开目录显示实际子文件");
            Check(((StackPanel)((CheckBox)mods.Header).Content).Children.OfType<FontIcon>().Single().Glyph == "\uE838", "文件夹行具有文件夹图标");
            Check(checks.All(x => ((StackPanel)x.Content).Children.OfType<FontIcon>().Single().Glyph == "\uEA86"), "模组文件行具有模组图标");
            var optionsRow = tree.Children.OfType<CheckBox>().Single(x => x.Tag is ModpackExportEntry { RelativePath: "options.txt" });
            Check(((StackPanel)optionsRow.Content).Children.OfType<FontIcon>().Single().Glyph == "\uE8A5", "文本文件行具有文件图标");
            checks[0].IsChecked = false;
            Check(((CheckBox)mods.Header).IsChecked is null && checks[1].IsChecked == true, "子项取消后父目录半选，不改变兄弟文件");
            ((CheckBox)mods.Header).IsChecked = true;
            Check(checks.All(x => x.IsChecked == true), "勾选父目录恢复全部子项");
            var saves = tree.Children.OfType<Expander>().Single(x => ((CheckBox)x.Header).Tag is ModpackExportEntry { RelativePath: "saves" });
            saves.IsExpanded = true;
            await Task.Delay(250);
            Check(((StackPanel)saves.Content).Children.OfType<Expander>().Count() == 1, "展开存档目录显示世界文件夹");
            await Snapshot(root, "export-light.png");
            Field<CheckBox>(view, "OnlyModrinth").IsChecked = true;
            Field<CheckBox>(view, "Bundle").IsChecked = true;
            Check(!Field<CheckBox>(view, "OnlyModrinth").IsEnabled && Field<CheckBox>(view, "OnlyModrinth").IsChecked == false && Field<InfoBar>(view, "BundleWarning").IsOpen,
                "打包资源时清除并禁用仅平台模式，显示协议警告");
            Field<ComboBox>(view, "Format").SelectedIndex = 2;
            Check(!Field<CheckBox>(view, "Bundle").IsEnabled && Field<CheckBox>(view, "Bundle").IsChecked == true &&
                Field<TextBlock>(view, "DestinationPath").Text == temporary, "MultiMC 固定内置资源且保持所选文件夹");
            root.RequestedTheme = ElementTheme.Dark;
            Invoke(Themes.ThemeManager.Current, "UpdateBrushes", true);
            await Task.Delay(300);
            Check(exportButton.Background is SolidColorBrush darkBrush && darkBrush.Color == ((SolidColorBrush)Resources["AccentButtonBackground"]).Color,
                "切换深色后按钮跟随共享主题画刷");
            await Snapshot(root, "export-dark.png");
            Invoke(view, "Export_Click", view, new RoutedEventArgs());
            await WaitUntil(() => Field<InfoBar>(view, "SuccessToast").IsOpen || Field<InfoBar>(view, "Status").IsOpen);
            Check(Field<InfoBar>(view, "SuccessToast").IsOpen, "实际导出完成后出现成功 Toast：" + Field<InfoBar>(view, "Status").Message);
            Check(File.Exists(Path.Combine(temporary, "测试_整合包.zip")), "按包名与格式生成文件，替换文件名非法字符");
            Check(Field<InfoBar>(view, "SuccessToast").ActionButton is null && Field<InfoBar>(view, "SuccessToast").IsTabStop,
                "成功 Toast 无独立操作按钮，整条通知保留键盘访问");
            await Snapshot(root, "export-success.png");
            view.Deactivate();
            Check(tree.Children.Count == 0 && !Field<InfoBar>(view, "SuccessToast").IsOpen, "离开页面释放树并关闭 Toast");
            File.AppendAllText(_log, "PASS\n");
        }
        catch (Exception ex) { Environment.ExitCode = 1; File.AppendAllText(_log, "FAIL " + ex + "\n"); }
        finally { _window?.Close(); if (Directory.Exists(temporary)) Directory.Delete(temporary, true); Exit(); }
    }

    /// <summary>等待异步状态，十秒内未完成即明确失败，避免无限挂起。</summary>
    private static async Task WaitUntil(Func<bool> ready)
    {
        for (int i = 0; i < 100; i++) { if (ready()) return; await Task.Delay(100); }
        throw new TimeoutException("等待界面状态超时。");
    }

    /// <summary>捕获真实 XAML 控件到 PNG，不依赖桌面截图服务。</summary>
    private static async Task Snapshot(FrameworkElement root, string name)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(root);
        var pixels = await bitmap.GetPixelsAsync();
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        using var stream = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }

    /// <summary>读取控件字段，保持产品不包含测试专用接口。</summary>
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;

    /// <summary>遍历真实按钮模板，核对交互状态实际使用的背景颜色。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    /// <summary>执行正式事件处理路径以验证状态转换。</summary>
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);

    /// <summary>将断言结果写入日志，失败时中止验收。</summary>
    private void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); File.AppendAllText(_log, message + "\n"); }
}
