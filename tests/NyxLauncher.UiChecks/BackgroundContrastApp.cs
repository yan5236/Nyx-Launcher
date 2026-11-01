using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Services;
using NyxLauncher.Themes;

namespace NyxLauncher;

/// <summary>替换应用入口，使用真实 WinUI 模板验证背景前景恢复；不打开业务页面或写入用户设置。</summary>
public partial class App : Application
{
    private Window? _window;
    private readonly string _log = Path.Combine(AppContext.BaseDirectory, "contrast-checks.log");

    /// <summary>加载应用的真实资源和控件样式，保持验收与正式界面一致。</summary>
    public App() => InitializeComponent();

    /// <summary>检查浅深色恢复、分类交互、图片溢出布局槽时的取样和输入选区主题色；失败写入日志并设置非零退出码。</summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        File.WriteAllText(_log, "START\n");
        try
        {
            ThemeManager.Current.Initialize();
            var root = new Grid();
            var image = new Image { Stretch = Stretch.UniformToFill };
            var content = new StackPanel();
            var tabs = new SelectorBar();
            tabs.Items.Add(new SelectorBarItem { Text = "通用", IsSelected = true });
            tabs.Items.Add(new SelectorBarItem { Text = "启动" });
            content.Children.Add(tabs);
            content.Children.Add(new TextBlock { Text = "样式前景" });
            content.Children.Add(new TextBlock { Text = "显式前景", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Green) });
            var label = new TextBlock { Text = "类型", Width = 400, HorizontalAlignment = HorizontalAlignment.Left };
            content.Children.Add(label);
            var editor = new ComboBox { IsEditable = true, Width = 180 };
            editor.Items.Add("全部");
            editor.SelectedIndex = 0;
            content.Children.Add(editor);
            root.Children.Add(image);
            root.Children.Add(content);
            var overflowLabel = new TextBlock { Text = "图片溢出取样", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 60, 0) };
            root.Children.Add(overflowLabel);
            _window = new Window { Content = root };
            _window.AppWindow.Resize(new(1040, 680));
            _window.Activate();
            var contrast = new BackgroundTextContrast(root, image, null);
            foreach (var mode in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = mode;
                await Task.Delay(300);
                var texts = Descendants(content).OfType<TextBlock>().Where(t => t.ActualWidth > 0 && !Descendants(editor).Contains(t)).ToArray();
                Check(texts.Length >= 4, "真实标签模板已加载");
                var input = Descendants(editor).OfType<TextBox>().First();
                var primary = ((SolidColorBrush)Resources["AccentFillColorDefaultBrush"]).Color;
                uint selection = BackgroundContrast.SelectionBackground(0xFF000000u | (uint)primary.R << 16 | (uint)primary.G << 8 | primary.B);
                Check(input.SelectionHighlightColor.Color == Windows.UI.Color.FromArgb(255, (byte)(selection >> 16), (byte)(selection >> 8), (byte)selection), "可编辑筛选框选区使用当前主题的可读选区色");
                var originals = texts.Select(t => t.Foreground).ToArray();
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    image.Source = new WriteableBitmap(1, 1);
                    image.Opacity = 1;
                    contrast.SetImage(new BackgroundContrast(1, 1, new byte[] { 0, 0, 0, 255 }));
                    await Task.Delay(300);
                    Check(texts.All(t => t.Foreground is SolidColorBrush brush && brush.Color == Microsoft.UI.Colors.White), "黑图上的文字变白");
                    foreach (var state in new[] { "UnselectedPointerOver", "UnselectedPressed", "SelectedNormal", "SelectedPointerOver", "SelectedPressed", "UnselectedNormal" })
                    {
                        Check(VisualStateManager.GoToState(tabs.Items[1], state, false), "分类状态存在：" + state);
                        await Task.Delay(30);
                        Check(Descendants(tabs.Items[1]).OfType<TextBlock>().All(t => t.Foreground is SolidColorBrush brush && brush.Color == Microsoft.UI.Colors.White), "分类交互保持白字：" + state);
                    }
                    if (repeat == 0) { image.Source = null; contrast.SetImage(null); }
                    else { image.Opacity = 0; contrast.Invalidate(); }
                    await Task.Delay(300);
                    Check(texts.Select((t, i) => ReferenceEquals(t.Foreground, originals[i])).All(restored => restored), $"{mode} 第 {repeat + 1} 次恢复原画刷");
                    contrast.SetImage(null);
                }
                image.Opacity = 1;
                image.Source = new WriteableBitmap(4, 1);
                contrast.SetImage(new BackgroundContrast(4, 1, new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255 }));
                await Task.Delay(300);
                Check(image.ActualWidth > root.ActualWidth * 2, "宽图真实绘制宽度超出窗口布局槽");
                Check(overflowLabel.Foreground is SolidColorBrush labelBrush && labelBrush.Color == Microsoft.UI.Colors.White, "窗口右侧仍位于图片左半边黑区，不误采居中裁切后的白区");
                contrast.SetImage(null);
            }
            File.AppendAllText(_log, "PASS\n");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.AppendAllText(_log, "FAIL " + ex + "\n");
        }
        finally { _window?.Close(); Exit(); }
    }

    /// <summary>遍历实际模板生成的文字节点，覆盖本地值为 null 的分类标签。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    /// <summary>记录具体视觉属性断言；失败时抛出异常终止验收。</summary>
    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        File.AppendAllText(_log, "OK " + message + "\n");
    }
}
