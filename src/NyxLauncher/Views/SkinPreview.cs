using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Services;
using SkiaSharp;
using Windows.Foundation;

namespace NyxLauncher.Views;

/// <summary>原生交互式皮肤预览；一个可复用像素缓冲支持走动、拖动旋转、滚轮缩放与暂停。</summary>
public sealed class SkinPreview : UserControl
{
    private readonly Image _image = new() { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
    private readonly SkinModelRenderer _renderer = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _clock = new();
    private SKBitmap? _surface;
    private WriteableBitmap? _bitmap;
    private byte[]? _pixels;
    private bool _active, _foreground = true, _paused;
    private float _phase, _yaw = .55f, _pitch = -.12f, _zoom = 1;
    private Point? _drag;
    private byte[]? _skin, _cape;
    private bool _slim;

    /// <summary>连接输入和计时器；仅在可见、已加载且窗口前台时绘制动画，卸载释放原生缓冲。</summary>
    public SkinPreview()
    {
        Content = _image;
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        IsTabStop = true;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "人物预览，拖动旋转，滚轮缩放，方向键旋转");
        _timer.Tick += Tick;
        Loaded += Preview_Loaded;
        Unloaded += Preview_Unloaded;
        PointerPressed += DragStart;
        PointerMoved += DragMove;
        PointerReleased += DragEnd;
        PointerCaptureLost += DragEnd;
        PointerWheelChanged += Wheel;
        KeyDown += Preview_KeyDown;
    }

    /// <summary>加载时恢复纹理与像素缓冲，首次绘制静态帧后按当前活动状态启动动画。</summary>
    private void Preview_Loaded(object sender, RoutedEventArgs e)
    {
        _surface = new SKBitmap(360, 440, SKColorType.Bgra8888, SKAlphaType.Premul);
        _bitmap = new WriteableBitmap(360, 440);
        _pixels = new byte[360 * 440 * 4];
        _image.Source = _bitmap;
        _renderer.SetTextures(_skin, _slim, _cape);
        Draw(); UpdateTimer();
    }

    /// <summary>卸载停止帧回调并释放图像资源，保留小型 PNG 以便再次进入时恢复。</summary>
    private void Preview_Unloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop(); _clock.Reset(); _drag = null;
        _renderer.Dispose(); _surface?.Dispose(); _surface = null;
        _image.Source = null; _bitmap = null; _pixels = null;
    }

    /// <summary>替换当前预览组合，不触发服务器修改；保留相机和暂停状态以便比较皮肤。</summary>
    public void SetTextures(byte[]? skin, bool slim, byte[]? cape)
    {
        _skin = skin; _cape = cape; _slim = slim;
        _renderer.SetTextures(skin, slim, cape); Draw();
    }

    /// <summary>导航切换时暂停不可见模型，返回后沿用用户设置。</summary>
    public void SetActive(bool active) { _active = active; UpdateTimer(); }

    /// <summary>窗口失焦停止动画，重新聚焦只恢复未手动暂停的预览。</summary>
    public void SetForeground(bool foreground) { _foreground = foreground; UpdateTimer(); }

    /// <summary>切换手动暂停，返回暂停状态供按钮显示；暂停仍允许相机旋转及缩放。</summary>
    public bool TogglePause() { _paused = !_paused; UpdateTimer(); return _paused; }

    /// <summary>按倍率缩放并限制为 0.6–2.2 倍，防止滚轮或连续点击产生无效投影。</summary>
    public void Zoom(float factor) { _zoom = Math.Clamp(_zoom * factor, .6f, 2.2f); Draw(); }

    /// <summary>恢复人物自身右前方的正斜视角和默认缩放，不改变皮肤或走动状态。</summary>
    public void ResetCamera() { _yaw = .55f; _pitch = -.12f; _zoom = 1; Draw(); }

    /// <summary>统一控制计时器，暂停期间不积累时间，恢复时不会突然跳到其他动作。</summary>
    private void UpdateTimer()
    {
        if (_active && _foreground && !_paused && IsLoaded) { _clock.Restart(); _timer.Start(); }
        else { _timer.Stop(); _clock.Reset(); }
    }

    /// <summary>根据真实帧间隔推进步态，限制卡顿后的时间跳跃。</summary>
    private void Tick(object? sender, object e)
    {
        _phase += (float)Math.Min(_clock.Elapsed.TotalSeconds, .1) * 5;
        _clock.Restart(); Draw();
    }

    /// <summary>把 Skia 的预乘 BGRA 像素写入复用的 WinUI 位图，不逐帧创建图像对象。</summary>
    private void Draw()
    {
        if (_surface is null || _bitmap is null || _pixels is null) return;
        _renderer.Render(_surface, _phase, _yaw, _pitch, _zoom);
        Marshal.Copy(_surface.GetPixels(), _pixels, 0, _pixels.Length);
        using var stream = _bitmap.PixelBuffer.AsStream();
        stream.Write(_pixels); _bitmap.Invalidate();
    }

    /// <summary>按下左键后捕获指针，确保拖出控件仍可正确结束旋转。</summary>
    private void DragStart(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _drag = e.GetCurrentPoint(this).Position; CapturePointer(e.Pointer); Focus(FocusState.Pointer); e.Handled = true;
    }

    /// <summary>按指针位移改变水平与俯仰角，限制上下翻转而允许完整水平环绕。</summary>
    private void DragMove(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } previous) return;
        var position = e.GetCurrentPoint(this).Position;
        _yaw += (float)(position.X - previous.X) * .012f;
        _pitch = Math.Clamp(_pitch - (float)(position.Y - previous.Y) * .008f, -1.2f, 1.2f);
        _drag = position; Draw(); e.Handled = true;
    }

    /// <summary>结束或丢失指针捕获时清空拖动状态。</summary>
    private void DragEnd(object sender, PointerRoutedEventArgs e) { _drag = null; ReleasePointerCaptures(); }

    /// <summary>滚轮按方向连续缩放并消费事件，避免同时滚动外层页面。</summary>
    private void Wheel(object sender, PointerRoutedEventArgs e)
    { Zoom(MathF.Pow(1.12f, e.GetCurrentPoint(this).Properties.MouseWheelDelta / 120f)); e.Handled = true; }

    /// <summary>提供键盘相机操作；Home 复位，方向键旋转，暂停由相邻按钮操作。</summary>
    private void Preview_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left: _yaw -= .12f; break;
            case Windows.System.VirtualKey.Right: _yaw += .12f; break;
            case Windows.System.VirtualKey.Up: _pitch = Math.Clamp(_pitch - .1f, -1.2f, 1.2f); break;
            case Windows.System.VirtualKey.Down: _pitch = Math.Clamp(_pitch + .1f, -1.2f, 1.2f); break;
            case Windows.System.VirtualKey.Home: ResetCamera(); break;
            default: return;
        }
        Draw(); e.Handled = true;
    }

    /// <summary>生成无计时器的静态模型缩略图；披风卡片使用背面视角以展示纹理。</summary>
    internal static WriteableBitmap Thumbnail(byte[] skin, bool slim, byte[]? cape = null, bool back = false)
    {
        using var renderer = new SkinModelRenderer();
        renderer.SetTextures(skin, slim, cape);
        using var surface = new SKBitmap(144, 176, SKColorType.Bgra8888, SKAlphaType.Premul);
        renderer.Render(surface, 0, back ? 3.45f : .55f);
        var bitmap = new WriteableBitmap(144, 176);
        byte[] pixels = new byte[144 * 176 * 4];
        Marshal.Copy(surface.GetPixels(), pixels, 0, pixels.Length);
        using var stream = bitmap.PixelBuffer.AsStream(); stream.Write(pixels); bitmap.Invalidate();
        return bitmap;
    }
}
