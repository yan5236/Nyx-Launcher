using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Services;
using Windows.Graphics.Imaging;

namespace NyxLauncher.Views;

/// <summary>账户页与侧栏共用的头像控件；离线显示默认头像，微软账户异步显示真实皮肤正脸与帽子层。</summary>
public sealed class AccountAvatar : UserControl
{
    private readonly Image _image = new();
    private int _generation;
    private static readonly Dictionary<string, Task<byte[]?>> Requests = [];
    public static readonly DependencyProperty AccountProperty = DependencyProperty.Register(nameof(Account), typeof(GameAccount),
        typeof(AccountAvatar), new PropertyMetadata(null, AccountChanged));
    public GameAccount? Account { get => (GameAccount?)GetValue(AccountProperty); set => SetValue(AccountProperty, value); }

    /// <summary>创建无底色、无边框的头像，加载或切换账户时刷新；卸载使迟到请求失效。</summary>
    public AccountAvatar()
    {
        Content = _image;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => _generation++;
    }

    /// <summary>账户绑定变化后仅刷新已经加载的控件，尚未加载的卡片由 Loaded 统一处理。</summary>
    private static void AccountChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    { if (sender is AccountAvatar avatar && avatar.IsLoaded) avatar.Refresh(); }

    /// <summary>先显示本地头像，再异步获取皮肤；代数隔离切换/卸载后的迟到结果，头像失败不影响登录和启动。</summary>
    private async void Refresh()
    {
        int generation = ++_generation;
        var account = Account;
        _image.Source = account is null ? null : new BitmapImage(new Uri(account.Avatar));
        if (account is not { IsMicrosoft: true }) return;
        try
        {
            string cachePath = CachePath(account.Uuid);
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length == 64 * 64 * 4)
            {
                byte[] cached = await File.ReadAllBytesAsync(cachePath);
                if (generation != _generation) return;
                SetPixels(cached);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try
        {
            // 同一 UI 线程中的所有头像共用一次请求，避免侧栏和卡片重复查询同一个档案。
            if (!Requests.TryGetValue(account.Uuid, out var request)) Requests[account.Uuid] = request = DownloadAsync(account.Uuid);
            byte[]? pixels = await request;
            if (generation != _generation) return;
            if (pixels is null) _image.Source = new BitmapImage(new Uri(account.Avatar));
            else SetPixels(pixels);
        }
        catch (Exception)
        {
            Requests.Remove(account.Uuid);
            // 皮肤加载属于可选显示，失败时保留已显示的缓存或默认头像。
        }
    }

    /// <summary>校验固定缓存尺寸后把 BGRA 像素写入 WinUI 位图，不添加背景或改变头像控件的布局。</summary>
    private void SetPixels(byte[] pixels)
    {
        if (pixels.Length != 64 * 64 * 4) return;
        var bitmap = new WriteableBitmap(64, 64);
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels);
        bitmap.Invalidate();
        _image.Source = bitmap;
    }

    /// <summary>生成带合成版本的 UUID 缓存路径，避免读取旧版不透明方形头像；缓存不包含账户凭据。</summary>
    private static string CachePath(string uuid)
    {
        MinecraftSkinService.ValidateUuid(uuid);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NyxLauncher", "avatars", uuid + "-head-v2.bgra");
    }

    /// <summary>从官方源下载后用 Windows 解码器解码并裁剪，成功后原子缓存；磁盘写入失败仍允许本次显示。</summary>
    private static async Task<byte[]?> DownloadAsync(string uuid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        byte[]? png = await MinecraftSkinService.FetchAsync(http, uuid, timeout.Token);
        string path = CachePath(uuid);
        if (png is null)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }
        using var memory = new MemoryStream(png);
        using var input = memory.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.PngDecoderId, input);
        SkinAvatarPixels.ValidateSize((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] pixels = SkinAvatarPixels.Create(data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(temporary, pixels);
            File.Move(temporary, path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return pixels;
    }
}
