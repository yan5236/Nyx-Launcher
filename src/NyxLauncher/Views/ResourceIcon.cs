using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>无背景、无边框的资源图标；持有自己的占位和图片，翻页回收时取消请求且不访问父控件。</summary>
public sealed class ResourceIcon : UserControl
{
    private static readonly ResourceIconService Service = new();
    private readonly Image _placeholder = new() { Width = 38, Height = 38 };
    private readonly Image _image = new() { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
    private CancellationTokenSource? _request;
    public static readonly DependencyProperty ProjectProperty = DependencyProperty.Register(nameof(Project), typeof(ResourceProject),
        typeof(ResourceIcon), new PropertyMetadata(null, ProjectChanged));
    public ResourceProject? Project { get => (ResourceProject?)GetValue(ProjectProperty); set => SetValue(ProjectProperty, value); }

    /// <summary>创建透明图标容器，并把加载与卸载绑定到卡片实际生命周期。</summary>
    public ResourceIcon()
    {
        var grid = new Grid();
        grid.Children.Add(_placeholder);
        grid.Children.Add(_image);
        Content = grid;
        Loaded += Icon_Loaded;
        Unloaded += Icon_Unloaded;
    }

    /// <summary>绑定项目变化时刷新占位及请求；尚未上屏的卡片只准备占位，避免预取不可见图片。</summary>
    private static void ProjectChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((ResourceIcon)sender).Refresh();

    /// <summary>进入可见树后开始加载当前项目的图标。</summary>
    private void Icon_Loaded(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>卡片回收或页面隐藏后取消旧请求，移除位图引用，防止迟到结果操作已卸载的内容。</summary>
    private void Icon_Unloaded(object sender, RoutedEventArgs e)
    {
        _request?.Cancel();
        _request = null;
        _image.Source = null;
    }

    /// <summary>先显示对应分类占位，再有界加载与解码；只向仍持有该请求的已加载控件发布，所有图片失败均保留占位。</summary>
    private async void Refresh()
    {
        _request?.Cancel();
        _request = null;
        _image.Source = null;
        _placeholder.Visibility = Visibility.Visible;
        string category = Project?.Category ?? "mod";
        _placeholder.Source = new SvgImageSource(new Uri($"ms-appx:///Assets/resource-{category}.svg"));
        if (!IsLoaded || string.IsNullOrWhiteSpace(Project?.IconUrl)) return;
        using var request = new CancellationTokenSource();
        _request = request;
        try
        {
            byte[] png = await Service.FetchAsync(Project.IconUrl, request.Token);
            if (request.IsCancellationRequested) return;
            using var stream = new MemoryStream(png);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            if (!request.IsCancellationRequested && IsLoaded && ReferenceEquals(_request, request))
            { _image.Source = bitmap; _placeholder.Visibility = Visibility.Collapsed; }
        }
        catch (Exception) { /* 图标是可选内容；失败或卸载只保留分类占位，不影响列表和应用生命周期。 */ }
        finally { if (ReferenceEquals(_request, request)) _request = null; }
    }
}
