using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Views;
using Windows.Graphics.Imaging;

namespace NyxLauncher;

/// <summary>使用正式主页面验证转场及快速导航；读取本机配置，不安装游戏或修改用户偏好。</summary>
public partial class App : Application
{
    private Window? _window;
    private readonly string _log = Path.Combine(AppContext.BaseDirectory, "navigation-checks.log");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>加载正式应用样式，使检查使用与启动器相同的控件模板。</summary>
    public App() => InitializeComponent();

    /// <summary>检查主页面与 Tab 转场并支持关于页及服务器页定向验收；失败返回非零退出码，退出前停止后台任务。</summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainPage? page = null;
        File.WriteAllText(_log, "START\n");
        try
        {
            Themes.ThemeManager.Current.Initialize();
            page = new MainPage();
            // 模拟 MainWindow 的窗口根：标题栏行加页面内容行，使页面级遮罩与正式窗口一样只挡住内容区。
            var host = new Grid { Background = (Brush)Resources["ShellBrush"] };
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            host.Children.Add(new Border
            {
                Background = (Brush)Resources["ShellBrush"],
                Child = new TextBlock { Text = "Nyx Launcher", Margin = new Thickness(56, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
            });
            Grid.SetRow(page, 1);
            host.Children.Add(page);
            _window = new Window { Content = host };
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
            if (Environment.GetCommandLineArgs().Contains("--servers-only"))
            {
                await CheckServersAsync(page);
                File.AppendAllText(_log, "PASS\n");
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--about-only"))
            {
                await CheckAboutAsync(page);
                File.AppendAllText(_log, "PASS\n");
                return;
            }
            foreach (string destination in new[] { "versions", "downloads", "wardrobe", "servers", "account", "settings", "home" })
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
            await CheckAboutAsync(page);
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

    /// <summary>验证服务器入口、空状态、首次自动查询、切页缓存、中断补查、手动刷新、新增后查询、精选菜单、长版本号和 MOTD 彩字，
    /// 以及启动与编辑共用的版本卡片弹窗（当前目录、切换目录、版本副标题、默认标记和选择后立即启动）
    /// 和覆盖侧栏的窗口层表单，并验证无版本时保存、清除绑定以及删除确认；使用临时配置与桩探测。</summary>
    private async Task CheckServersAsync(MainPage page)
    {
        Check(Field<object?>(page, "ServersContent") is null, "未访问服务器页时不创建");
        var navigation = Field<NavigationView>(page, "Navigation");
        var homeItem = navigation.SelectedItem;
        var serverItem = navigation.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag as string == "servers");
        typeof(MainPage).GetMethod("EnsurePage", Private)!.Invoke(page, ["servers"]);
        var view = Field<ServersView>(page, "ServersContent");
        var add = Field<Button>(view, "AddButton");
        var refresh = Field<Button>(view, "RefreshButton");
        var empty = Field<StackPanel>(view, "Empty");
        var tabs = Field<SelectorBar>(view, "Tabs");
        page.Background = (Brush)Resources["PageBrush"];
        Themes.ThemeManager.Current.Attach(page, new Image());
        await Task.Delay(200);

        string temporary = Path.Combine(Path.GetTempPath(), "nyx-server-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var folders = new Services.GameFolderStore(Path.Combine(temporary, "folders.json"), temporary);
        var calls = new List<string>();
        var slowProbe = new TaskCompletionSource();
        TaskCompletionSource? customGate = null;
        string hypixelPlayers = "2/20";
        /// <summary>记录查询地址并返回合成状态，用可控的慢请求验证导航中断和新增服务器时的独立更新。</summary>
        async Task<Services.ServerStatus?> Probe(string address, CancellationToken token)
        {
            lock (calls) calls.Add(address);
            if (address == "mc.hypixel.net")
                return new([new("Hypixel", 0xAA00AA)], "1.21", hypixelPlayers, 17, null);
            if (address == "2b2t.org")
                await slowProbe.Task.WaitAsync(token);
            if (address == "localhost:25566" && customGate is not null)
                await customGate.Task.WaitAsync(token);
            await Task.Delay(200, token);
            return address switch
            {
                "localhost:25566" => new([new("彩色简介", 0xAA00AA), new("\n第二行", 0x0000AA)], "Waterfall 1.12.2-1.20.4", "168/1", 48, null),
                "localhost:25568" => new([], "1.21", "7/20", 24, null),
                _ => null
            };
        }
        /// <summary>等待当前标签查询完成，超过时限则令检查失败。</summary>
        async Task SettleAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Field<object?>(view, "_probe") is not null && DateTime.UtcNow < deadline) await Task.Delay(20);
            Check(Field<object?>(view, "_probe") is null, "服务器状态查询已结束");
            await Task.Delay(120);
        }
        try
        {
            view.Configure(folders, (_, _) => Task.CompletedTask, Path.Combine(temporary, "no-servers.json"), Probe);
            navigation.SelectedItem = serverItem;
            Check(view.Visibility == Visibility.Visible && add.Visibility == Visibility.Visible, "自定义页显示添加入口");
            await SettleAsync();
            var hint = empty.Children.OfType<TextBlock>().Single();
            var emptyAdd = Field<Button>(view, "EmptyAddButton");
            Check(empty.Visibility == Visibility.Visible && Field<StackPanel>(view, "Cards").Children.Count == 0 &&
                empty.Children.OfType<FontIcon>().Single().FontSize == 48, "自定义页空列表显示图标和提示");
            Check(hint.Text == "暂时没有服务器呢，点击右上角或下方添加" && emptyAdd.Visibility == Visibility.Visible &&
                !refresh.IsEnabled && calls.Count == 0, "空状态给出直接添加入口且不发起查询");
            var accent = ((SolidColorBrush)Application.Current.Resources["AccentButtonBackground"]).Color;
            Check(emptyAdd.Background is SolidColorBrush { Color: var buttonColor } && buttonColor == accent,
                "空状态按钮使用页面主色");
            await SnapshotAsync(page, "servers-empty.png");
            page.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(350);
            await SnapshotAsync(page, "servers-empty-dark.png");
            page.RequestedTheme = ElementTheme.Default;
            await Task.Delay(250);

            string customPath = Path.Combine(temporary, "servers.json");
            new Services.ServerStore(customPath).Save(new Services.ServerEntry("custom-1", "滑滑水", "localhost:25566",
                Path.Combine(temporary, "version")));
            navigation.SelectedItem = homeItem;
            view.Configure(folders, (_, _) => Task.CompletedTask, customPath, Probe);
            navigation.SelectedItem = serverItem;
            Check(Field<ProgressRing>(view, "Refreshing").IsActive && !refresh.IsEnabled, "自动刷新期间显示进度并禁用刷新按钮");
            await SettleAsync();
            var cards = Field<StackPanel>(view, "Cards").Children.OfType<Border>().ToArray();
            Check(cards.Length == 1 && empty.Visibility == Visibility.Collapsed, "有服务器时隐藏空状态并显示卡片");
            Check(calls.Contains("localhost:25566"), "进入页面自动查询自定义服务器");
            var version = Descendants(cards[0]).OfType<TextBlock>()
                .Single(text => AutomationProperties.GetName(text).StartsWith("服务器版本 "));
            double full = TextWidth(version, version.Text);
            File.AppendAllText(_log, $"VERSION actual={version.ActualWidth:F1} max={version.MaxWidth:F1} full={full:F1}\n");
            Check(version.Text == "Waterfall 1.12.2-1.20.4" && version.ActualWidth + 0.5 >= full,
                "服务器版本完整显示且未被简介挤压");
            await SnapshotAsync(page, "servers-custom.png");

            navigation.SelectedItem = homeItem;
            navigation.SelectedItem = serverItem;
            Check(calls.Count(address => address == "localhost:25566") == 1 && version.Text == "Waterfall 1.12.2-1.20.4",
                "返回自定义列表直接显示缓存版本");

            calls.Clear();
            tabs.SelectedItem = Field<SelectorBarItem>(view, "FeaturedTab");
            var featuredList = Field<StackPanel>(view, "Cards");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!Descendants(featuredList).OfType<TextBlock>().Any(text => text.Text == "2/20") && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Check(!refresh.IsEnabled && Descendants(featuredList).OfType<TextBlock>().Any(text => text.Text == "2/20"),
                "慢服务器仍在查询时，已完成的精选卡片显示玩家数");
            navigation.SelectedItem = homeItem;
            navigation.SelectedItem = serverItem;
            Check(calls.Count(address => address == "mc.hypixel.net") == 1 && calls.Count(address => address == "2b2t.org") == 2,
                "中途离开后返回只补查未完成的地址");
            slowProbe.SetResult();
            await SettleAsync();
            cards = Field<StackPanel>(view, "Cards").Children.OfType<Border>().ToArray();
            Check(cards.Length == 4 && add.Visibility == Visibility.Collapsed &&
                Field<TextBlock>(view, "Disclaimer").Visibility == Visibility.Visible && empty.Visibility == Visibility.Collapsed,
                "精选页包含清单、提示和手动刷新");
            Check(calls.Distinct().Count() == 4 && calls.All(address => address != "localhost:25566"), "首次切到精选页自动查询精选服务器");
            foreach (var card in cards)
            {
                var menu = ((Grid)card.Child).Children.OfType<Button>().Single().Flyout as MenuFlyout;
                Check(menu?.Items.Count == 1 && ((MenuFlyoutItem)menu.Items[0]).Text == "修改信息", "精选菜单仅提供修改");
            }
            var identity = ((Grid)cards[0].Child).Children.OfType<StackPanel>().Single(element => Grid.GetColumn(element) == 1);
            var motd = (TextBlock)identity.Children[1];
            Check(motd.Inlines[0] is Microsoft.UI.Xaml.Documents.Run { Text: "Hypixel", Foreground: SolidColorBrush brush } &&
                brush.Color.R == 0xAA && brush.Color.G == 0 && brush.Color.B == 0xAA, "MOTD 颜色应用到 WinUI 文本片段");

            calls.Clear();
            navigation.SelectedItem = homeItem;
            navigation.SelectedItem = serverItem;
            tabs.SelectedItem = tabs.Items[0];
            tabs.SelectedItem = Field<SelectorBarItem>(view, "FeaturedTab");
            Check(calls.Count == 0 && refresh.IsEnabled && Descendants(featuredList).OfType<TextBlock>().Any(text => text.Text == "2/20"),
                "返回页面及来回切换标签会复用缓存，包括已完成的失败结果");

            hypixelPlayers = "4/20";
            var refreshMethod = typeof(ServersView).GetMethod("Refresh_Click", Private)!;
            refreshMethod.Invoke(view, [refresh, new RoutedEventArgs()]);
            await SettleAsync();
            Check(calls.Count == 4 && Descendants(featuredList).OfType<TextBlock>().Any(text => text.Text == "4/20"),
                "手动刷新重新查询当前列表并更新缓存");

            page.Background = (Brush)Resources["PageBrush"];
            Themes.ThemeManager.Current.Attach(page, new Image());
            await Task.Delay(300);
            await SnapshotAsync(page, "servers-featured.png");

            tabs.SelectedItem = tabs.Items[0];
            _window!.AppWindow.Resize(new(820, 680));
            await Task.Delay(500);
            cards = Field<StackPanel>(view, "Cards").Children.OfType<Border>().ToArray();
            var narrowVersion = Descendants(cards[0]).OfType<TextBlock>()
                .Single(text => AutomationProperties.GetName(text).StartsWith("服务器版本 "));
            double narrowFull = TextWidth(narrowVersion, narrowVersion.Text);
            File.AppendAllText(_log, $"NARROW actual={narrowVersion.ActualWidth:F1} max={narrowVersion.MaxWidth:F1} full={narrowFull:F1}\n");
            Check(narrowVersion.ActualWidth + 0.5 >= narrowFull, "窄窗口把指标移到简介下方后版本仍完整");
            await SnapshotAsync(page, "servers-narrow.png");

            var editor = Field<Grid>(view, "EditorLayer");
            var editMethod = typeof(ServersView).GetMethod("EditAsync", Private)!;
            var saveMethod = typeof(ServersView).GetMethod("EditorSave_Click", Private)!;
            var saveButton = Field<Button>(view, "EditorSave");
            await (Task)editMethod.Invoke(view, [null])!;
            Field<TextBox>(view, "EditorName").Text = "无默认版本测试服";
            Field<TextBox>(view, "EditorAddress").Text = "localhost:25568";
            calls.Clear();
            customGate = new TaskCompletionSource();
            refreshMethod.Invoke(view, [refresh, new RoutedEventArgs()]);
            saveMethod.Invoke(view, [saveButton, new RoutedEventArgs()]);
            var unbound = new Services.ServerStore(customPath).Entries(false).Single(item => item.Address == "localhost:25568");
            Check(editor.Visibility == Visibility.Collapsed && unbound.VersionPath is null, "没有安装版本也可以保存服务器");
            Check(calls.SequenceEqual(["localhost:25566", "localhost:25568"]), "保存后立即查询新地址，即使已有刷新在进行");
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (!Descendants(Field<StackPanel>(view, "Cards")).OfType<TextBlock>().Any(text => text.Text == "7/20") &&
                DateTime.UtcNow < deadline) await Task.Delay(20);
            Check(Descendants(Field<StackPanel>(view, "Cards")).OfType<TextBlock>().Any(text => text.Text == "7/20"),
                "新增卡片在上一条请求结束前显示真实探测流程返回的玩家数");
            customGate.SetResult();
            await SettleAsync();

            // 版本选择使用卡片弹窗：临时启动、默认实例缺失和表单内选择都复用同一路径。
            string gameRoot = Path.Combine(temporary, ".minecraft");
            string fabricPath = Path.Combine(gameRoot, "versions", "fabric-loader-0.16.9-1.21.4");
            Directory.CreateDirectory(fabricPath);
            await File.WriteAllTextAsync(Path.Combine(fabricPath, "fabric-loader-0.16.9-1.21.4.json"),
                """{"id":"fabric-loader-0.16.9-1.21.4","type":"release","inheritsFrom":"1.21.4","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.9"}]}""");
            string vanillaPath = Path.Combine(gameRoot, "versions", "1.21.4");
            Directory.CreateDirectory(vanillaPath);
            await File.WriteAllTextAsync(Path.Combine(vanillaPath, "1.21.4.json"), """{"id":"1.21.4","type":"release"}""");
            string otherRoot = Path.Combine(temporary, "other-game");
            string otherVersion = Path.Combine(otherRoot, "versions", "1.20.1");
            Directory.CreateDirectory(otherVersion);
            await File.WriteAllTextAsync(Path.Combine(otherVersion, "1.20.1.json"), """{"id":"1.20.1","type":"release"}""");
            folders.Add(otherRoot);

            Services.ServerVersion? launched = null;
            view.Configure(folders, (version, _) => { launched = version; return Task.CompletedTask; }, customPath, Probe);
            view.SetActive(true);
            var entry = new Services.ServerEntry("custom-1", "滑滑水", "localhost:25566", fabricPath);
            var start = StartVersionAsync(view, entry, true);
            var dialog = await WaitForServerDialogAsync<ServerVersionDialog>(page);
            var directorySelector = Field<ScrollSafeComboBox>(dialog, "GameDirectories");
            var choices = await WaitForVersionCardsAsync(dialog);
            Check(Equals(directorySelector.SelectedItem, gameRoot) && choices.Length == 2 &&
                choices.All(item => item.Version.Folder == gameRoot) && choices.Single(item => item.DefaultVisibility == Visibility.Visible)
                .Version.Version.DirectoryPath == fabricPath, "打开弹窗只列出当前游戏目录的实例并标记默认");
            var pickerTexts = Descendants(dialog).OfType<TextBlock>().Select(text => text.Text).ToArray();
            Check(Descendants(dialog).OfType<VersionIcon>().Count(icon => icon.Version is not null) == 2 &&
                pickerTexts.Contains("fabric-loader-0.16.9-1.21.4") &&
                pickerTexts.Any(text => text.StartsWith("Minecraft 1.21.4 · Fabric: 0.16.9")),
                "版本卡片带图标、版本名和副标题小字");
            var dialogContent = Field<Grid>(dialog, "DialogContent");
            File.AppendAllText(_log, $"PICKER content={dialogContent.ActualWidth:F0}\n");
            // 原生 ContentDialog 的内容区约 500 宽，内容超过后会被模板裁切；460 留出关闭按钮的完整空间。
            Check(dialogContent.ActualWidth <= 460, "版本卡片内容宽度适配弹窗内容区，关闭按钮不被裁切");
            await SnapshotAsync(dialog, "servers-versions.png");
            directorySelector.SelectedItem = otherRoot;
            choices = await WaitForVersionCardsAsync(dialog);
            Check(choices.Single().Version.Version.DirectoryPath == otherVersion &&
                choices[0].DefaultVisibility == Visibility.Collapsed && folders.Selected == gameRoot &&
                Descendants(dialog).OfType<VersionIcon>().Single(icon => icon.Version is not null).Version!.DirectoryPath == otherVersion,
                "切换弹窗目录时只显示目标目录版本且不写回全局目录");
            directorySelector.SelectedItem = null;
            Check((await WaitForVersionCardsAsync(dialog)).Length == 0 && Field<TextBlock>(dialog, "Empty").Text == "请选择游戏目录",
                "未选目录时显示提示，版本列表为空");
            directorySelector.SelectedItem = gameRoot;
            choices = await WaitForVersionCardsAsync(dialog);
            dialog.Use(choices.Single(item => item.Version.Version.DirectoryPath == fabricPath));
            await start;
            Check(launched?.Version.DirectoryPath == fabricPath && launched.Folder == gameRoot, "点击卡片立即用该实例启动");
            Check(new Services.ServerStore(customPath).Entries(false).Single(item => item.Id == "custom-1").VersionPath ==
                Path.Combine(temporary, "version"), "临时选择不写回服务器默认版本");

            launched = null;
            folders.Select(otherRoot);
            await StartVersionAsync(view, entry with { VersionPath = fabricPath }, false);
            Check(launched?.Version.DirectoryPath == fabricPath && launched.Folder == gameRoot && folders.Selected == otherRoot,
                "全局目录切换后仍从已绑定的登记目录启动");
            folders.Select(gameRoot);

            launched = null;
            start = StartVersionAsync(view, entry with { VersionPath = Path.Combine(temporary, "absent") }, false);
            dialog = await WaitForServerDialogAsync<ServerVersionDialog>(page);
            dialog.Use((await WaitForVersionCardsAsync(dialog))
                .Single(item => item.Version.Version.DirectoryPath == vanillaPath));
            await start;
            Check(launched?.Version.DirectoryPath == vanillaPath, "默认实例缺失时由卡片弹窗补选");

            launched = null;
            start = StartVersionAsync(view, unbound, false);
            dialog = await WaitForServerDialogAsync<ServerVersionDialog>(page);
            dialog.Use((await WaitForVersionCardsAsync(dialog))
                .Single(item => item.Version.Version.DirectoryPath == vanillaPath));
            await start;
            Check(launched?.Version.DirectoryPath == vanillaPath &&
                new Services.ServerStore(customPath).Entries(false).Single(item => item.Id == unbound.Id).VersionPath is null,
                "无默认版本时点击启动可临时选版本");

            typeof(ServersView).GetMethod("Add_Click", Private)!.Invoke(view, [add, new RoutedEventArgs()]);
            var editorDeadline = DateTime.UtcNow.AddSeconds(5);
            while (editor.Visibility != Visibility.Visible && DateTime.UtcNow < editorDeadline) await Task.Delay(20);
            await Task.Delay(200);
            var editorVersionName = Field<TextBlock>(view, "EditorVersionName");
            var editorPanel = Field<Border>(view, "EditorPanel");
            Check(editor.Visibility == Visibility.Visible && Field<TextBlock>(view, "EditorTitle").Text == "添加服务器" &&
                Field<TextBox>(view, "EditorName").Text == "" && editorVersionName.Text == "未选择启动版本" &&
                editorPanel.ActualWidth is >= 300 and <= 520, "添加服务器在页面上层打开自适应宽度的表单");
            Field<TextBox>(view, "EditorName").Text = "卡片测试服";
            Field<TextBox>(view, "EditorAddress").Text = "localhost:25567";
            await Task.Delay(150);
            var windowHost = (Grid)_window!.Content;
            var layerBounds = editor.TransformToVisual(windowHost)
                .TransformBounds(new(0, 0, editor.ActualWidth, editor.ActualHeight));
            Check(editor.Visibility == Visibility.Visible && layerBounds.Top >= 40 &&
                Math.Abs(layerBounds.Height - (windowHost.ActualHeight - 48)) < 1,
                "表单与遮罩覆盖标题栏下方的内容区（含侧栏）");
            await SnapshotAsync(windowHost, "servers-editor.png");
            typeof(ServersView).GetMethod("EditorVersion_Click", Private)!
                .Invoke(view, [Field<Button>(view, "EditorVersion"), new RoutedEventArgs()]);
            dialog = await WaitForServerDialogAsync<ServerVersionDialog>(page);
            Check(editor.Visibility == Visibility.Visible, "版本卡片弹窗覆盖在表单上且表单保持打开");
            dialog.Use((await WaitForVersionCardsAsync(dialog))
                .Single(item => item.Version.Version.Name == "fabric-loader-0.16.9-1.21.4"));
            var pickDeadline = DateTime.UtcNow.AddSeconds(5);
            while (editorVersionName.Text != "fabric-loader-0.16.9-1.21.4" && DateTime.UtcNow < pickDeadline) await Task.Delay(20);
            Check(editorVersionName.Text == "fabric-loader-0.16.9-1.21.4" &&
                Field<VersionIcon>(view, "EditorVersionIcon").Version?.DirectoryPath == fabricPath, "表单版本入口同步所选卡片");
            saveMethod.Invoke(view, [saveButton, new RoutedEventArgs()]);
            var saved = new Services.ServerStore(customPath).Entries(false);
            Check(editor.Visibility == Visibility.Collapsed && saved.Any(item => item.Name == "卡片测试服" &&
                item.Address == "localhost:25567" && item.VersionPath == fabricPath), "保存表单写入名称、地址和卡片选择的版本");

            var bound = saved.Single(item => item.Address == "localhost:25567");
            await (Task)editMethod.Invoke(view, [bound])!;
            var clearButton = Field<Button>(view, "EditorClearVersion");
            Check(clearButton.Visibility == Visibility.Visible, "已绑定版本可在表单内清除");
            typeof(ServersView).GetMethod("EditorClearVersion_Click", Private)!.Invoke(view, [clearButton, new RoutedEventArgs()]);
            Check(editorVersionName.Text == "未选择启动版本" &&
                new Services.ServerStore(customPath).Entries(false).Single(item => item.Id == bound.Id).VersionPath == fabricPath,
                "清除版本后要保存才写入配置");
            saveMethod.Invoke(view, [saveButton, new RoutedEventArgs()]);
            Check(new Services.ServerStore(customPath).Entries(false).Single(item => item.Id == bound.Id).VersionPath is null,
                "保存后移除默认版本绑定");

            var featured = new Services.ServerStore(customPath).Entries(true).First();
            await (Task)typeof(ServersView).GetMethod("EditAsync", Private)!.Invoke(view, [featured])!;
            await Task.Delay(150);
            Check(editor.Visibility == Visibility.Visible && Field<TextBlock>(view, "EditorTitle").Text == "修改服务器" &&
                Field<TextBox>(view, "EditorAddress").Visibility == Visibility.Collapsed, "精选服务器表单隐藏地址");
            typeof(ServersView).GetMethod("EditorCancel_Click", Private)!
                .Invoke(view, [Field<Button>(view, "EditorCancel"), new RoutedEventArgs()]);
            Check(editor.Visibility == Visibility.Collapsed, "取消关闭表单且不写入设置");

            var deleteMethod = typeof(ServersView).GetMethod("DeleteAsync", Private)!;
            var deletion = (Task)deleteMethod.Invoke(view, [unbound])!;
            var confirmation = await WaitForServerDialogAsync<ContentDialog>(page);
            Check(confirmation.Title is "删除服务器？" && confirmation.DefaultButton == ContentDialogButton.Close &&
                confirmation.Content is TextBlock description && description.Text.Contains(unbound.Name) &&
                description.Text.Contains(unbound.Address) && description.Text.Contains("已安装的游戏版本和文件会保留"),
                "删除明确询问并显示名称、地址和影响，默认取消");
            await SnapshotAsync(confirmation, "servers-delete.png");
            InvokeDialogButton(confirmation, "取消");
            await deletion;
            Check(new Services.ServerStore(customPath).Entries(false).Contains(unbound), "取消删除保留服务器记录");

            deletion = (Task)deleteMethod.Invoke(view, [unbound])!;
            confirmation = await WaitForServerDialogAsync<ContentDialog>(page);
            InvokeDialogButton(confirmation, "删除记录");
            await deletion;
            var remaining = new Services.ServerStore(customPath).Entries(false).ToArray();
            Check(remaining.All(item => item.Id != unbound.Id) && remaining.Any(item => item.Id == bound.Id) &&
                File.Exists(Path.Combine(fabricPath, "fabric-loader-0.16.9-1.21.4.json")),
                "确认只删除选中的本机记录，保留其他记录和已安装版本");
        }
        finally { Directory.Delete(temporary, true); }
    }

    /// <summary>让服务器页走正式启动入口，返回可等待的任务；不复制版本核对与弹窗逻辑。</summary>
    private static Task StartVersionAsync(ServersView view, Services.ServerEntry entry, bool temporary) => (Task)typeof(ServersView)
        .GetMethod("StartAsync", Private)!.Invoke(view, [entry, temporary])!;

    /// <summary>等待当前目录扫描结束并更新弹窗布局，返回列表中的卡片数据；超时则令检查失败。</summary>
    private static async Task<ServerVersionItem[]> WaitForVersionCardsAsync(ServerVersionDialog dialog)
    {
        var empty = Field<TextBlock>(dialog, "Empty");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (empty.Text == "正在读取此目录的版本…" && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (empty.Text == "正在读取此目录的版本…") throw new InvalidOperationException("读取目录版本超时");
        dialog.UpdateLayout();
        return Field<ListView>(dialog, "Versions").Items.Cast<ServerVersionItem>().ToArray();
    }

    /// <summary>等待真实弹窗层出现指定类型的服务器弹窗，超时则令检查失败。</summary>
    private async Task<T> WaitForServerDialogAsync<T>(MainPage page) where T : ContentDialog
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (VisualTreeHelper.GetOpenPopupsForXamlRoot(page.XamlRoot).Select(popup => popup.Child)
                .OfType<T>().FirstOrDefault() is { } dialog)
            {
                await Task.Delay(250);
                return dialog;
            }
            await Task.Delay(30);
        }
        throw new InvalidOperationException($"服务器弹窗没有打开：{typeof(T).Name}");
    }

    /// <summary>通过自动化调用弹窗内指定文字的按钮，走 WinUI 原生确认或取消事件。</summary>
    private static void InvokeDialogButton(ContentDialog dialog, string label)
    {
        var button = Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, label));
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    /// <summary>按无限宽测量文本的完整绘制宽度，用于判断是否被裁剪。</summary>
    private static double TextWidth(TextBlock source, string text)
    {
        var measure = new TextBlock { Text = text, FontSize = source.FontSize, FontFamily = source.FontFamily };
        measure.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return measure.DesiredSize.Width;
    }

    /// <summary>遍历可视化树，供检查按辅助名称定位生成的文本。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>通过真实设置 Tab 验证关于页延迟创建、清单绑定、GitHub 协议及滚动；切换明暗并保存截图，不打开浏览器或写用户偏好。</summary>
    private async Task CheckAboutAsync(MainPage page)
    {
        Show(page, "settings");
        await Task.Delay(350);
        var settings = Field<SettingsView>(page, "SettingsContent");
        Check(Field<object?>(settings, "AboutPanel") is null, "未访问关于时不创建致谢列表");
        var tabs = Field<SelectorBar>(settings, "SettingsTabs");
        tabs.SelectedItem = tabs.Items.Single(item => item.Tag as string == "about");
        await Task.Delay(350);
        var about = Field<AboutView>(settings, "AboutPanel");
        Check(Field<Grid>(page, "SettingsScroll").Visibility == Visibility.Visible &&
            about.Visibility == Visibility.Visible && (string)Field<SelectorBar>(settings, "SettingsTabs").SelectedItem.Tag == "about",
            "设置中的关于 Tab 显示正确正文");
        Check(Field<TextBlock>(about, "VersionLabel").Text == about.VersionText && about.VersionText != "版本 未知",
            "版本标签来自当前程序集");
        Check(Field<ItemsControl>(about, "DependencyList").Items.Count == about.Dependencies.Count &&
            about.Dependencies.SelectMany(x => x.Packages).Contains("CmlLib.Core/4.1.0") &&
            about.Dependencies.Any(x => x.Name == "authlib-injector"), "完整清单绑定到实际控件");
        Check(Field<HyperlinkButton>(about, "UserAgreement").IsEnabled &&
            (string)Field<HyperlinkButton>(about, "UserAgreement").Tag == Services.LauncherInfo.AgreementUrl &&
            Field<HyperlinkButton>(about, "PrivacyPolicy").IsEnabled &&
            (string)Field<HyperlinkButton>(about, "PrivacyPolicy").Tag == Services.LauncherInfo.PrivacyUrl, "两份协议均连接对应 GitHub 文档");
        typeof(AboutView).GetMethod("Link_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(about, [new HyperlinkButton { Tag = "invalid" }, new RoutedEventArgs()]);
        Check(Field<InfoBar>(about, "LinkError").IsOpen && Field<InfoBar>(about, "LinkError").Message == "链接地址无效。",
            "无效链接在页内报错");
        Field<InfoBar>(about, "LinkError").IsOpen = false;
        page.Background = (Brush)Resources["PageBrush"];
        Themes.ThemeManager.Current.Attach(page, new Image());
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            page.RequestedTheme = theme;
            await Task.Delay(350);
            await SnapshotAsync(page, "about-" + theme + ".png");
        }
        var scroll = (ScrollView)about.Content;
        await CheckAboutScrollAsync(scroll, Field<ItemsControl>(about, "DependencyList"));
        var list = Field<ItemsControl>(about, "DependencyList");
        var container = (ContentPresenter)list.ContainerFromIndex(list.Items.Count - 1);
        var card = (FrameworkElement)VisualTreeHelper.GetChild(container, 0);
        var lastLink = (HyperlinkButton)card.FindName("ProjectLink");
        Check(lastLink.Focus(FocusState.Keyboard), "列表底部链接可以通过键盘获得焦点");
        await Task.Delay(700);
        var linkBounds = lastLink.TransformToVisual(scroll).TransformBounds(new(0, 0, lastLink.ActualWidth, lastLink.ActualHeight));
        Check(linkBounds.Top >= -1 && linkBounds.Bottom <= scroll.ViewportHeight + 1, "键盘聚焦自动滚动到目标链接");
        scroll.ScrollTo(0, scroll.ScrollableHeight, new(ScrollingAnimationMode.Disabled));
        await Task.Delay(350);
        Check(scroll.VerticalOffset > 0, "依赖清单可以滚动到底部");
        await SnapshotAsync(page, "about-dependencies.png");
        scroll.ScrollTo(0, 0, new(ScrollingAnimationMode.Disabled));
        Show(page, "home");
        Show(page, "settings");
        Check(ReferenceEquals(about, Field<AboutView>(settings, "AboutPanel")), "重复进入复用关于页");
        Show(page, "home");
    }

    /// <summary>逐段往返滚动静态致谢列表，检查滚动方向、最终位置和内容高度；等待实际到达目标，避免用固定动画时长制造误报。</summary>
    private async Task CheckAboutScrollAsync(ScrollView scroll, ItemsControl list)
    {
        File.AppendAllText(_log, $"SCROLL panel={list.ItemsPanelRoot.GetType().Name} extent={scroll.ExtentHeight} viewport={scroll.ViewportHeight}\n");
        double extent = scroll.ExtentHeight;
        double previous = scroll.VerticalOffset;
        int direction = 1;
        bool reversed = false;
        /// <summary>检测与当前滚动请求方向相反的位置变化；允许一像素以内的浮点和绘制舍入误差。</summary>
        void Observe(ScrollView sender, object args)
        {
            if (direction * (scroll.VerticalOffset - previous) < -1) reversed = true;
            previous = scroll.VerticalOffset;
        }
        scroll.ViewChanged += Observe;
        try
        {
            foreach (double target in new[] { 400d, 900d, 1500d, scroll.ScrollableHeight, 1500d, 900d, 400d, 0d })
            {
                direction = Math.Sign(target - scroll.VerticalOffset);
                scroll.ScrollTo(0, target, new(ScrollingAnimationMode.Enabled));
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (Math.Abs(scroll.VerticalOffset - Math.Min(target, scroll.ScrollableHeight)) >= 1 && DateTime.UtcNow < deadline)
                    await Task.Delay(40);
                await Task.Delay(150);
                Check(Math.Abs(scroll.VerticalOffset - Math.Min(target, scroll.ScrollableHeight)) < 1,
                    $"滚动到 {target} 后停留在目标位置");
                Check(Math.Abs(scroll.ExtentHeight - extent) < 1, "滚动时清单高度稳定");
            }
            Check(!reversed, "同方向滚动没有反向跳动");
        }
        finally { scroll.ViewChanged -= Observe; }
    }

    /// <summary>将当前真实页面及侧栏渲染为 PNG，供检查布局；空图或编码失败让本次验收失败。</summary>
    private static async Task SnapshotAsync(FrameworkElement root, string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        var pixels = await bitmap.GetPixelsAsync();
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        using var stream = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }

    /// <summary>直接操作四类真实视图的 SelectorBar，不注入业务服务，验证包括关于在内的 Tab 切换与动画，避免测试触发下载或配置写入。</summary>
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
