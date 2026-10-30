using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace NyxLauncher.Views;

/// <summary>为加载器图标选择 SVG 或位图解码，确保 Quilt 矢量图不会被当作普通位图。</summary>
public sealed class ResourceLoaderIconConverter : IValueConverter
{
    /// <summary>把项目内图标 URI 转成对应图像源；输入为空时不显示图片。</summary>
    public object? Convert(object value, Type targetType, object parameter, string language) => value is string path && path.Length > 0
        ? path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? new SvgImageSource(new Uri(path)) : new BitmapImage(new Uri(path)) : null;

    /// <summary>加载器图标只作单向显示，不支持反向写入 URI。</summary>
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
