using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Services;
using NyxLauncher.Views;
using SkiaSharp;
using Windows.Graphics.Imaging;

namespace NyxLauncher;

/// <summary>使用隔离账户与皮肤副本验证真实衣柜控件，所有网络写入均不执行。</summary>
public partial class App : Application
{
    private Window? _window;
    private readonly string _log = Path.Combine(AppContext.BaseDirectory, "wardrobe-checks.log");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>加载正式主题和控件资源。</summary>
    public App() => InitializeComponent();

    /// <summary>验证账户过滤、穿戴原地更新、应用状态、主色悬停、分类纹理、Toast 生命周期和模型操作；保存真实控件截图，不执行远端写入。</summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nyx-wardrobe-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        File.WriteAllText(_log, "START\n");
        try
        {
            Themes.ThemeManager.Current.Initialize();
            var view = new WardrobeView();
            var root = new Grid { Padding = new Thickness(28), Background = (Brush)Resources["PageBrush"], RequestedTheme = ElementTheme.Light };
            root.Children.Add(view);
            _window = new Window { Title = "Wardrobe UI checks", Content = root };
            _window.AppWindow.Resize(new(1080, 720)); _window.Activate();
            await Task.Delay(400);
            var accounts = new AccountStore(Path.Combine(temporary, "accounts.json")); accounts.Add("Offline");
            view.Configure(accounts); view.SetActive(true);
            await Task.Delay(100);
            Check(Field<ComboBox>(view, "AccountPicker").Items.Count == 0 && !Field<Button>(view, "UploadButton").IsEnabled, "离线账户不可用");
            view.SetActive(false);
            accounts.SaveMicrosoft(new GameAccount("WardrobeTest", new string('a', 32), "fixture-home", Guid.NewGuid().ToString(), "AA=="));
            Invoke(view, "BindAccounts");
            Check(Field<ComboBox>(view, "AccountPicker").Items.Count == 1, "选择器只显示微软账户");
            byte[] steve = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "steve.png"));
            byte[] alex = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "alex.png"));
            var store = new WardrobeStore(Path.Combine(temporary, "wardrobe"));
            var currentSkin = store.Add("Steve", false, steve); var nextSkin = store.Add("Alex", true, alex);
            Set(view, "_store", store); Set(view, "_currentSkin", steve);
            Set(view, "_currentSkinId", currentSkin.Id);
            Set(view, "_profile", new WardrobeProfile(null, []));
            Field<InfoBar>(view, "Status").IsOpen = false;
            Invoke(view, "RebuildSkins"); Invoke(view, "FilterCards");
            var cards = Field<GridView>(view, "Cards");
            Check(cards.Items.Count == 3, "当前皮肤与收藏合并去重，保留导入入口");
            Invoke(view, "DeleteAsync", currentSkin);
            Check(store.Skins.Count == 2, "当前皮肤拒绝删除，不打开确认弹窗");
            store.Rename(currentSkin, "我的皮肤"); Invoke(view, "RebuildSkins"); Invoke(view, "FilterCards");
            Check((string)cards.Items[0].GetType().GetProperty("Name")!.GetValue(cards.Items[0])! == "我的皮肤（当前）", "当前皮肤重命名后保留当前标记");
            Set(view, "_currentSkinId", nextSkin.Id); Invoke(view, "RebuildSkins"); Invoke(view, "FilterCards");
            Check(cards.Items.Count == 3 && store.Skins.Any(s => s.Id == currentSkin.Id && s.Name == "我的皮肤"), "切换当前皮肤后旧皮肤和名称保留");
            Set(view, "_currentSkinId", currentSkin.Id); Invoke(view, "RebuildSkins"); Invoke(view, "FilterCards");
            Invoke(view, "ShowStatus", "皮肤已应用。", InfoBarSeverity.Success);
            Check(Field<InfoBar>(view, "SuccessToast").IsOpen && !Field<InfoBar>(view, "Status").IsOpen && Field<InfoBar>(view, "SuccessToast").VerticalAlignment == VerticalAlignment.Bottom, "应用成功仅显示右下角 Toast");
            await SnapshotAsync(root, "wardrobe-toast.png");
            await Task.Delay(3200);
            Check(!Field<InfoBar>(view, "SuccessToast").IsOpen && !Field<DispatcherTimer>(view, "_toastTimer").IsEnabled, "Toast 三秒后关闭并停止计时");
            cards.SelectedIndex = 1;
            Check(Field<Button>(view, "ApplyButton").IsEnabled, "选择有效皮肤才允许应用");
            var selectedCard = cards.SelectedItem;
            var itemsSource = cards.ItemsSource;
            Set(view, "_busy", true); Set(view, "_applying", true); Invoke(view, "UpdateEnabled");
            Check(!Field<Button>(view, "ApplyButton").IsEnabled && (string)Field<Button>(view, "ApplyButton").Content == "应用中" &&
                cards.Visibility == Visibility.Visible && !Field<ProgressRing>(view, "LoadingRing").IsActive &&
                !Field<ComboBox>(view, "AccountPicker").IsEnabled && !Field<TextBox>(view, "Search").IsEnabled, "应用中禁用操作但保留列表，不显示加载状态");
            await SnapshotAsync(root, "wardrobe-applying.png");
            Invoke(view, "AcceptAppliedCard", selectedCard);
            Check(ReferenceEquals(cards.ItemsSource, itemsSource) && ReferenceEquals(cards.SelectedItem, selectedCard) &&
                Field<string>(view, "_currentSkinId") == nextSkin.Id &&
                (string)selectedCard.GetType().GetProperty("Name")!.GetValue(selectedCard)! == "Alex（当前）", "成功原地更新穿戴标记，保留列表和选择");
            Set(view, "_busy", false);
            Invoke(view, "DeleteAsync", nextSkin);
            Check(store.Skins.Count == 2, "原地应用后的当前皮肤仍受删除保护");
            Invoke(view, "AcceptAppliedCard", cards.Items[0]);
            Set(view, "_busy", false); Set(view, "_applying", false); Invoke(view, "UpdateEnabled");
            Check(Field<Button>(view, "ApplyButton").IsEnabled && (string)Field<Button>(view, "ApplyButton").Content == "应用" &&
                ReferenceEquals(cards.SelectedItem, selectedCard), "请求结束恢复按钮并保留选择供重试");
            var upload = Field<Button>(view, "UploadButton");
            Check(ReferenceEquals(upload.Style, Resources["DeepButtonStyle"]) && VisualStateManager.GoToState(upload, "PointerOver", false), "上传复用主色样式及悬停视觉状态");
            await SnapshotAsync(root, "wardrobe-upload-hover.png");
            VisualStateManager.GoToState(upload, "Normal", false);
            AccountAvatar.UpdateMicrosoft(new string('a', 32), alex);
            var avatarRequests = (Dictionary<string, Task<byte[]?>>)typeof(AccountAvatar).GetField("Requests", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            byte[]? avatarPixels = await avatarRequests[new string('a', 32)];
            Check(avatarPixels is { Length: 16384 }, "已应用皮肤直接生成共享头像，无需重新查询官方纹理");
            var sidebarAvatar = new AccountAvatar { Account = accounts.Accounts.Single(a => a.IsMicrosoft) };
            root.Children.Add(sidebarAvatar);
            await Task.Delay(150);
            AccountAvatar.UpdateMicrosoft(new string('a', 32), steve);
            byte[] expectedAvatar = (await avatarRequests[new string('a', 32)])!;
            await Task.Delay(150);
            var pickerAvatar = FindAvatar(Field<ComboBox>(view, "AccountPicker"));
            Check(pickerAvatar is not null && AvatarPixels(pickerAvatar).SequenceEqual(expectedAvatar) &&
                AvatarPixels(sidebarAvatar).SequenceEqual(expectedAvatar), "选择器和独立侧栏头像同步显示新皮肤像素");
            root.Children.Remove(sidebarAvatar);
            var preview = Field<SkinPreview>(view, "Preview");
            preview.SetActive(true);
            await Task.Delay(200);
            float phase = Field<float>(preview, "_phase");
            await Task.Delay(150);
            Check(Field<float>(preview, "_phase") > phase, "默认持续走动");
            Invoke(view, "Pause_Click", view, new RoutedEventArgs());
            phase = Field<float>(preview, "_phase"); await Task.Delay(120);
            Check(Field<float>(preview, "_phase") == phase && (string)Field<Button>(view, "PauseButton").Content == "继续", "暂停冻结步态并更新按钮");
            preview.Zoom(20); Check(Field<float>(preview, "_zoom") == 2.2f, "放大上限");
            preview.Zoom(.01f); Check(Field<float>(preview, "_zoom") == .6f, "缩小下限");
            preview.ResetCamera(); Check(Field<float>(preview, "_yaw") == .55f && Field<float>(preview, "_zoom") == 1, "恢复人物右前斜视角");
            Field<TextBox>(view, "Search").Text = "Alex";
            await Task.Delay(100);
            Check(cards.Items.Count == 2 && !Field<Button>(view, "ApplyButton").IsEnabled, "搜索保留上传入口并清除旧选择");
            Field<TextBox>(view, "Search").Text = ""; await Task.Delay(100); cards.SelectedIndex = 1;
            await Task.Delay(250);
            await SnapshotAsync(root, "wardrobe-light.png");
            root.RequestedTheme = ElementTheme.Dark;
            Invoke(Themes.ThemeManager.Current, "UpdateBrushes", true);
            await Task.Delay(200); await SnapshotAsync(root, "wardrobe-dark.png");
            byte[] cape = CapeFixture();
            AddCapeCard(view, "不使用披风", null, null, steve);
            AddCapeCard(view, "方向测试披风", "fixture-cape", cape, steve);
            Field<SelectorBar>(view, "CategoryTabs").SelectedItem = Field<SelectorBarItem>(view, "CapesTab");
            await Task.Delay(250); cards.SelectedIndex = 1; await Task.Delay(250);
            Check(cards.Items.Count == 2 && Field<Button>(view, "ApplyButton").IsEnabled, "披风卡片可选择并允许应用");
            Check((Visibility)cards.Items[1].GetType().GetProperty("MenuVisibility")!.GetValue(cards.Items[1])! == Visibility.Collapsed, "披风不提供皮肤管理菜单");
            Set(preview, "_yaw", MathF.PI); Invoke(preview, "Draw");
            await SnapshotAsync(root, "wardrobe-cape-direction.png");
            for (int round = 0; round < 3; round++)
            {
                Field<SelectorBar>(view, "CategoryTabs").SelectedItem = Field<SelectorBarItem>(view, "SkinsTab");
                Check(cards.Items.Count == 3 && cards.SelectedItem is null && !Field<Button>(view, "ApplyButton").IsEnabled, "切回皮肤立即恢复皮肤列表并清除披风选择");
                cards.SelectedIndex = 1;
                Check(ReferenceEquals(Field<byte[]>(preview, "_skin"), cards.Items[1].GetType().GetProperty("Png")!.GetValue(cards.Items[1])) && Field<byte[]?>(preview, "_cape") is null, "皮肤只进入人物纹理槽");
                Field<SelectorBar>(view, "CategoryTabs").SelectedItem = Field<SelectorBarItem>(view, "CapesTab");
                Check(cards.Items.Count == 2 && cards.SelectedItem is null && !Field<Button>(view, "ApplyButton").IsEnabled, "切回披风立即恢复披风列表并清除皮肤选择");
                cards.SelectedIndex = 1;
                Check(Field<byte[]>(preview, "_skin").SequenceEqual(steve) && ReferenceEquals(Field<byte[]>(preview, "_cape"), cape), "披风只进入披风纹理槽，人物保留当前皮肤");
                cards.SelectedIndex = 0;
                Check(Field<byte[]?>(preview, "_cape") is null && Field<Button>(view, "ApplyButton").IsEnabled, "不使用披风只清除披风且可应用");
            }
            cards.SelectedIndex = 1;
            var capeItems = cards.ItemsSource;
            Invoke(view, "AcceptAppliedCard", cards.SelectedItem!);
            Check(ReferenceEquals(Field<byte[]>(view, "_currentCape"), cape) && ReferenceEquals(cards.ItemsSource, capeItems) && cards.SelectedIndex == 1, "应用披风原地更新穿戴并保留列表和选择");
            cards.SelectedIndex = 0; Invoke(view, "AcceptAppliedCard", cards.SelectedItem!);
            Check(Field<byte[]?>(view, "_currentCape") is null && cards.SelectedIndex == 0, "取消披风原地清空当前披风并保留选择");
            Set(view, "_loading", true); Invoke(view, "UpdateEnabled");
            Check(Field<StackPanel>(view, "LoadingPanel").Visibility == Visibility.Visible && Field<ProgressRing>(view, "LoadingRing").IsActive && cards.Visibility == Visibility.Collapsed, "加载时右侧显示转圈并隐藏列表");
            await SnapshotAsync(root, "wardrobe-loading.png");
            Set(view, "_loading", false); Invoke(view, "UpdateEnabled");
            Check(Field<StackPanel>(view, "LoadingPanel").Visibility == Visibility.Collapsed && !Field<ProgressRing>(view, "LoadingRing").IsActive && cards.Visibility == Visibility.Visible, "加载结束恢复列表并停止转圈");
            preview.TogglePause(); preview.SetForeground(false);
            Check(!Field<DispatcherTimer>(preview, "_timer").IsEnabled, "失焦停止动画");
            preview.SetForeground(true);
            Check(Field<DispatcherTimer>(preview, "_timer").IsEnabled, "聚焦恢复动画");
            view.SetActive(false);
            Check(!Field<DispatcherTimer>(preview, "_timer").IsEnabled && cards.Items.Count == 0, "离开页面停止动画并释放卡片");
            if (Environment.GetEnvironmentVariable("NYX_WARDROBE_LIVE") == "1")
            {
                view.Configure(new AccountStore()); view.SetActive(true);
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (Field<bool>(view, "_busy") && DateTime.UtcNow < deadline) await Task.Delay(200);
                Check(Field<WardrobeProfile?>(view, "_profile") is not null, "实网皮肤与披风加载：" + Field<InfoBar>(view, "Status").Message);
                var profile = Field<WardrobeProfile>(view, "_profile");
                File.AppendAllText(_log, $"LIVE capes={profile.Capes.Count}; no remote changes\n");
                await SnapshotAsync(root, "wardrobe-live.png");
                Field<SelectorBar>(view, "CategoryTabs").SelectedItem = Field<SelectorBarItem>(view, "CapesTab");
                await Task.Delay(100);
                Check(cards.Items.Count == profile.Capes.Count + 1, "真实拥有披风与不使用披风卡片完整");
                if (profile.Capes.Count > 0) cards.SelectedIndex = 1;
                preview.TogglePause(); Set(preview, "_yaw", 3.45f); Invoke(preview, "Draw");
                await SnapshotAsync(root, "wardrobe-capes.png");
                view.SetActive(false);
            }
            File.AppendAllText(_log, "PASS\n");
        }
        catch (Exception ex) { Environment.ExitCode = 1; File.AppendAllText(_log, "FAIL " + ex + "\n"); }
        finally { _window?.Close(); Directory.Delete(temporary, true); Exit(); }
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
    }

    /// <summary>创建外侧左绿右黄、内侧紫色的非对称披风，方便检查正反和左右方向。</summary>
    private static byte[] CapeFixture()
    {
        using var bitmap = new SKBitmap(64, 32); bitmap.Erase(SKColors.Transparent);
        for (int y = 1; y < 17; y++)
            for (int x = 1; x < 22; x++) bitmap.SetPixel(x, y, x < 6 ? SKColors.Lime : x < 11 ? SKColors.Yellow : SKColors.Magenta);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100); return png.ToArray();
    }

    /// <summary>只向测试页面注入公开纹理卡片，复用正式缩略图渲染且不新增产品测试接口。</summary>
    private static void AddCapeCard(WardrobeView view, string name, string? id, byte[]? png, byte[] skin)
    {
        var type = typeof(WardrobeView).GetNestedType("Card", BindingFlags.NonPublic)!;
        var card = Activator.CreateInstance(type)!;
        type.GetProperty("Name")!.SetValue(card, name); type.GetProperty("CapeId")!.SetValue(card, id);
        type.GetProperty("Png")!.SetValue(card, png);
        type.GetProperty("IsCape")!.SetValue(card, true);
        type.GetProperty("Thumbnail")!.SetValue(card, SkinPreview.Thumbnail(skin, false, png, true));
        Field<System.Collections.IList>(view, "_capeCards").Add(card);
    }

    /// <summary>读取真实控件内部状态以断言生命周期，不在产品中增加测试专用接口。</summary>
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;

    /// <summary>从真实选择器模板中寻找头像控件，验证其订阅了换肤通知。</summary>
    private static AccountAvatar? FindAvatar(DependencyObject root)
    {
        if (root is AccountAvatar avatar) return avatar;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindAvatar(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    /// <summary>读取实际头像位图像素，确保更新了显示内容而非仅缓存状态。</summary>
    private static byte[] AvatarPixels(AccountAvatar avatar) => ((WriteableBitmap)Field<Image>(avatar, "_image").Source).PixelBuffer.ToArray();
    /// <summary>向独立测试实例注入隔离数据，绝不修改用户配置。</summary>
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    /// <summary>执行正式控件事件处理方法，验证同一业务路径的状态变化。</summary>
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    /// <summary>记录具体断言并在失败时中断验收。</summary>
    private void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); File.AppendAllText(_log, message + "\n"); }
}
