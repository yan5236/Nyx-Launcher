using System.Net;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Services;
using NyxLauncher.Themes;
using NyxLauncher.Views;
using Windows.Graphics.Imaging;

namespace NyxLauncher;

/// <summary>以隔离配置验证正式协议控件、首页图片及运行中单文件替换，保存可用于 README 的真实首页截图。</summary>
public partial class App : Application
{
    private MainWindow? _window;
    private readonly string _output = Path.GetDirectoryName(Environment.ProcessPath)!;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>加载正式应用样式，不增加产品中的测试开关。</summary>
    public App() => InitializeComponent();

    /// <summary>将外观持久化隔离到临时目录，以合成游戏和账户验收协议、图片和单文件更新；失败写日志并返回非零。</summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nyx-release-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        MainPage? page = null;
        File.WriteAllText(Path.Combine(_output, "release-checks.log"), "START\n");
        try
        {
            var themes = ThemeManager.Current;
            typeof(ThemeManager).GetField("<Store>k__BackingField", Private)!.SetValue(themes, new ThemeSettingsStore(Path.Combine(temporary, "theme.json")));
            themes.Initialize();
            _window = new MainWindow();
            var root = Field<Grid>(_window, "ThemeRoot");
            // 测试只替换首次入口的存储，避免把测试接受记录写入用户真实配置。
            var startup = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), _window,
                typeof(MainWindow).GetMethod("Startup_Loaded", Private)!);
            root.Loaded -= startup;
            _window.Activate();
            await Task.Delay(400);
            Check(Field<Frame>(_window, "RootFrame").Content is null, "协议前尚未创建主页面");
            await CheckAgreementAsync(root.XamlRoot, temporary);

            Field<Frame>(_window, "RootFrame").Navigate(typeof(MainPage));
            page = (MainPage)Field<Frame>(_window, "RootFrame").Content;
            string game = Path.Combine(temporary, ".minecraft");
            string versionDirectory = Path.Combine(game, "versions", "1.21.1");
            Directory.CreateDirectory(versionDirectory);
            File.WriteAllText(Path.Combine(versionDirectory, "1.21.1.json"), "{\"id\":\"1.21.1\",\"type\":\"release\"}");
            var accounts = new AccountStore(Path.Combine(temporary, "accounts.json"));
            accounts.Add("Steve");
            Field<HomeView>(page, "HomeContent").Configure(new GameFolderStore(Path.Combine(temporary, "folders.json"), temporary),
                new DownloadSettingsStore(Path.Combine(temporary, "download.json")), accounts, new JavaRuntimeStore(Path.Combine(temporary, "java.json")));
            Field<TextBlock>(page, "AccountName").Text = "Steve";
            Field<AccountAvatar>(page, "AccountAvatar").Account = accounts.Selected;
            Field<AccountAvatar>(page, "AccountAvatar").Visibility = Visibility.Visible;
            Field<FontIcon>(page, "EmptyAccountIcon").Visibility = Visibility.Collapsed;
            themes.Save(themes.Store.Current with { Mode = AppearanceMode.Light });
            await Task.Delay(800);
            await SnapshotAsync(root, "home-readme.png");
            await CheckHomeImageAsync(page, temporary);
            if (LauncherInfo.CanSelfUpdate) await CheckRunningReplacementAsync(temporary);
            else Check(false, "本入口必须以单文件发布执行，才能覆盖运行中替换");
            Check(_window.Content is not null, "更新完成仍保留当前窗口");
            File.AppendAllText(Path.Combine(_output, "release-checks.log"), "PASS\n");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.AppendAllText(Path.Combine(_output, "release-checks.log"), "FAIL " + ex + "\n");
        }
        finally
        {
            if (page is not null) await page.StopDownloadsAsync();
            _window?.Close();
            Directory.Delete(temporary, true);
            Exit();
        }
    }

    /// <summary>用真实弹窗和按钮自动化验证未勾选、拒绝、主动勾选与同意持久化；只写隔离的协议记录。</summary>
    private async Task CheckAgreementAsync(XamlRoot root, string temporary)
    {
        var store = new AgreementStore(Path.Combine(temporary, "agreement.json"));
        var declined = new AgreementDialog(root, store);
        var showing = declined.ShowThemedAsync();
        await Task.Delay(300);
        Check(!declined.IsPrimaryButtonEnabled && !store.IsAccepted, "协议不默认勾选或默认同意");
        var links = ((StackPanel)declined.Content).Children.OfType<HyperlinkButton>().Select(link => (string)link.Tag).ToArray();
        Check(links.SequenceEqual(new[] { LauncherInfo.AgreementUrl, LauncherInfo.PrivacyUrl }), "协议直接指向对应 GitHub 文档");
        InvokeButton(declined, "CloseButton");
        Check(await showing != ContentDialogResult.Primary && !store.IsAccepted, "拒绝不保存同意");
        var accepted = new AgreementDialog(root, store);
        showing = accepted.ShowThemedAsync();
        await Task.Delay(300);
        ((StackPanel)accepted.Content).Children.OfType<CheckBox>().Single().IsChecked = true;
        Check(accepted.IsPrimaryButtonEnabled, "主动勾选后才能继续");
        InvokeButton(accepted, "PrimaryButton");
        Check(await showing == ContentDialogResult.Primary && new AgreementStore(Path.Combine(temporary, "agreement.json")).IsAccepted,
            "真实确认按钮成功保存当前协议修订");
    }

    /// <summary>通过外观服务及正式按钮验证自定义图、无图和恢复默认；所有写入发生在隔离配置，图片源不被删除。</summary>
    private async Task CheckHomeImageAsync(MainPage page, string temporary)
    {
        var themes = ThemeManager.Current;
        var home = Field<HomeView>(page, "HomeContent");
        string source = Path.Combine(temporary, "custom.jpg");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "HomeLandscape.jpg"), source);
        Check(themes.Save(themes.Store.Current with { HomeImagePath = source }) is null, "自选首页图保存成功");
        await Task.Delay(500);
        Check(Field<Border>(home, "StageCard").Background is ImageBrush && Field<ImageBrush>(home, "StageImage").ImageSource is BitmapImage,
            "自选图片已经解码并显示");
        var appearance = new AppearanceSettingsView();
        typeof(AppearanceSettingsView).GetMethod("RemoveHomeImage_Click", Private)!.Invoke(appearance, [appearance, new RoutedEventArgs()]);
        await Task.Delay(100);
        Check(Field<Border>(home, "StageCard").Background is null && Field<Border>(home, "EdgeShade").Visibility == Visibility.Collapsed &&
            Field<Border>(home, "BottomShade").Visibility == Visibility.Collapsed, "移除同时清除图片和暗角");
        Check(Field<Button>(home, "LaunchButton").IsEnabled && Field<ComboBox>(home, "VersionList").SelectedItem is InstalledVersion,
            "无图时仍可选择和启动游戏");
        Check(File.Exists(source), "移除不删除用户源图");
        typeof(AppearanceSettingsView).GetMethod("RestoreHomeImage_Click", Private)!.Invoke(appearance, [appearance, new RoutedEventArgs()]);
        await Task.Delay(300);
        Check(!themes.Store.Current.HideHomeImage && themes.Store.Current.HomeImagePath is null && Field<Border>(home, "StageCard").Background is ImageBrush,
            "恢复默认立即显示内置图");
    }

    /// <summary>用本进程单文件的副本模拟已校验的新发布，真实下载并替换正在运行的 EXE，断言当前进程和窗口未被关闭。</summary>
    private async Task CheckRunningReplacementAsync(string temporary)
    {
        Check(LauncherInfo.DirectoryPath == _output && !string.Equals(_output, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
            "单文件将持久数据根目录与依赖解压目录分开");
        var settings = MicrosoftLoginSettings.Load();
        Check(settings.ClientId != "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "发布产物内嵌实际微软公共客户端配置");
        string source = Path.Combine(temporary, "candidate.exe");
        File.Copy(LauncherInfo.ExecutablePath, source);
        string hash;
        using (var file = File.OpenRead(source)) hash = Convert.ToHexString(await SHA256.HashDataAsync(file));
        var release = new LauncherRelease("65534.0.0", "合成更新夹具", new Uri(LauncherInfo.Repository), new Uri(LauncherInfo.Repository + "/releases/download/v65534.0.0/NyxLauncher.exe"), new FileInfo(source).Length, hash);
        using var http = new HttpClient(new FileHandler(source));
        var transport = new DownloadTransport(new(), http);
        var tasks = new DownloadTaskManager(new DownloadSettingsStore(Path.Combine(temporary, "tasks.json")), action => action());
        // 保留一个已有任务作为共享传输批次，用正式管理器复用注入的协议夹具，不增加产品测试接口。
        var existing = new DownloadTaskItem { Name = "已有下载", Destination = Path.Combine(temporary, "existing"), State = DownloadState.Running };
        tasks.Tasks.Add(existing);
        typeof(DownloadTaskManager).GetField("_transport", Private)!.SetValue(tasks, transport);
        int processId = Environment.ProcessId;
        var update = tasks.AddLauncherUpdate(release);
        await update.Execution;
        Check(update.IsLauncherUpdate && update.State == DownloadState.Completed && update.Percent == 100 &&
            update.Detail.Contains("下次启动生效") && tasks.LauncherUpdateInstalled, "更新复用共享下载任务并明确提示下次启动生效");
        Check(Environment.ProcessId == processId && File.Exists(LauncherInfo.ExecutablePath + ".previous") && File.Exists(LauncherInfo.ExecutablePath),
            "运行中 EXE 替换成功并保留备份，进程未退出");
        var about = new AboutView();
        Check(about.Dependencies.Count > 0 && new SettingsView().Content is not null, "替换后仍能加载尚未访问的 XAML 页面和内嵌资源");
        existing.State = DownloadState.Completed;
        await tasks.ShutdownAsync();
    }

    /// <summary>通过控件自动化提供器触发模板中的真实按钮，而不复制弹窗确认逻辑。</summary>
    private static void InvokeButton(DependencyObject root, string name)
    {
        var button = Descendants(root).OfType<Button>().Where(item => item.Name == name)
            .OrderByDescending(item => item.ActualWidth * item.ActualHeight).First();
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    /// <summary>遍历真实视觉树以定位 ContentDialog 模板按钮。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>将正式窗口的 XAML 根节点渲染为 PNG，保存真实标题栏、侧栏和首页，不拼接或重绘界面。</summary>
    private async Task SnapshotAsync(FrameworkElement root, string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        var pixels = await bitmap.GetPixelsAsync();
        using var file = File.Create(Path.Combine(_output, name));
        using var stream = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }

    /// <summary>读取正式页面控件和应用生成字段，避免产品暴露专用测试接口。</summary>
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;

    /// <summary>记录具体断言，失败中止本次原生窗口验收。</summary>
    private void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); File.AppendAllText(Path.Combine(_output, "release-checks.log"), "OK " + message + "\n"); }

    private sealed class FileHandler(string path) : HttpMessageHandler
    {
        /// <summary>以本地夹具流提供完整下载响应；HEAD 只报告长度，GET 流在响应释放时关闭，不访问网络。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            HttpContent content = request.Method == HttpMethod.Head ? new ByteArrayContent([]) : new StreamContent(File.OpenRead(path));
            content.Headers.ContentLength = new FileInfo(path).Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
