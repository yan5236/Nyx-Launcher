using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Services;
using Windows.Graphics.Imaging;

namespace NyxLauncher.Views;

/// <summary>账户页与侧栏共用的头像控件；离线显示默认头像，在线账户及选角预览异步显示真实皮肤正脸与帽子层。</summary>
public sealed class AccountAvatar : UserControl
{
    private readonly Image _image = new();
    private int _generation;
    private static readonly Dictionary<string, Task<byte[]?>> Requests = [];
    private static event Action<string>? SkinInvalidated;
    public static readonly DependencyProperty AccountProperty = DependencyProperty.Register(nameof(Account), typeof(GameAccount),
        typeof(AccountAvatar), new PropertyMetadata(null, AccountChanged));
    public GameAccount? Account { get => (GameAccount?)GetValue(AccountProperty); set => SetValue(AccountProperty, value); }

    public static readonly DependencyProperty ProfileIdProperty = DependencyProperty.Register(nameof(ProfileId), typeof(string), typeof(AccountAvatar), new PropertyMetadata(null, AccountChanged));
    public static readonly DependencyProperty AuthServerProperty = DependencyProperty.Register(nameof(AuthServer), typeof(string), typeof(AccountAvatar), new PropertyMetadata(null, AccountChanged));
    public string? ProfileId { get => (string?)GetValue(ProfileIdProperty); set => SetValue(ProfileIdProperty, value); }
    public string? AuthServer { get => (string?)GetValue(AuthServerProperty); set => SetValue(AuthServerProperty, value); }

    /// <summary>创建无底色、无边框的头像，加载或切换账户时刷新；卸载使迟到请求失效。</summary>
    public AccountAvatar()
    {
        Content = _image;
        Loaded += (_, _) => { SkinInvalidated += OnSkinInvalidated; Refresh(); };
        Unloaded += (_, _) => { SkinInvalidated -= OnSkinInvalidated; _generation++; };
    }

    /// <summary>官方换肤成功后用已确认的 PNG 更新进程缓存并通知同身份头像；解码异常由刷新路径处理，不影响应用结果。</summary>
    public static void UpdateMicrosoft(string uuid, byte[] png)
    {
        MinecraftSkinService.ValidateUuid(uuid);
        Requests[uuid] = DecodeAsync(png); SkinInvalidated?.Invoke(uuid);
    }

    /// <summary>仅刷新匹配的微软身份，外置与其他账户不受影响；失败继续保留旧头像缓存。</summary>
    private void OnSkinInvalidated(string uuid)
    { if (Account is { IsMicrosoft: true } account && account.Uuid == uuid) Refresh(); }

    /// <summary>账户绑定变化后仅刷新已经加载的控件，尚未加载的卡片由 Loaded 统一处理。</summary>
    private static void AccountChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    { if (sender is AccountAvatar avatar && avatar.IsLoaded) avatar.Refresh(); }

    /// <summary>先显示本地头像，再异步获取皮肤；代数隔离迟到结果，旧请求失败不得移除换肤后的新缓存，头像失败不影响登录和启动。</summary>
    private async void Refresh()
    {
        int generation = ++_generation;
        var account = Account;
        string? uuid = account?.Uuid ?? ProfileId;
        string? server = account?.AuthServer ?? AuthServer;
        string fallback = account?.Avatar ?? "ms-appx:///Assets/steve-avatar.png";
        _image.Source = uuid is null ? null : new BitmapImage(new Uri(fallback));
        if (uuid is null || account is { IsMicrosoft: false, IsExternal: false }) return;
        string key = server is null ? uuid : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(server))) + "-" + uuid;
        try
        {
            string cachePath = CachePath(key);
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length == 64 * 64 * 4)
            {
                byte[] cached = await File.ReadAllBytesAsync(cachePath);
                if (generation != _generation) return;
                SetPixels(cached);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Task<byte[]?>? request = null;
        try
        {
            // 同一 UI 线程中的所有头像共用一次请求，避免侧栏和卡片重复查询同一个档案。
            if (!Requests.TryGetValue(key, out request)) Requests[key] = request = DownloadAsync(uuid, server, key);
            byte[]? pixels = await request;
            if (generation != _generation) return;
            if (pixels is null) _image.Source = new BitmapImage(new Uri(fallback));
            else SetPixels(pixels);
        }
        catch (Exception)
        {
            if (Requests.TryGetValue(key, out var current) && ReferenceEquals(current, request)) Requests.Remove(key);
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

    /// <summary>生成带合成版本和服务器摘要的缓存路径，避免读取旧版不透明方形头像；缓存不包含账户凭据。</summary>
    private static string CachePath(string key)
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NyxLauncher", "avatars", key + "-head-v2.bgra");
    }

    /// <summary>从对应认证服务器公开档案下载后用 Windows 解码器解码并裁剪，成功后原子缓存；磁盘写入失败仍允许本次显示。</summary>
    private static async Task<byte[]?> DownloadAsync(string uuid, string? server, string key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        byte[]? png = await MinecraftSkinService.FetchAsync(http, uuid, timeout.Token, server);
        string path = CachePath(key);
        if (png is null)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }
        byte[] pixels = (await DecodeAsync(png))!;
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

    /// <summary>把 PNG 解码为合成帽子层的头像像素；非法尺寸或格式向调用方报告，供下载和已应用皮肤共用。</summary>
    private static async Task<byte[]?> DecodeAsync(byte[] png)
    {
        using var memory = new MemoryStream(png);
        using var input = memory.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.PngDecoderId, input);
        SkinAvatarPixels.ValidateSize((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return SkinAvatarPixels.Create(data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }
}
