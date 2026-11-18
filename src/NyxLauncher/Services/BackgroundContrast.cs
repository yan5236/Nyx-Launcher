namespace NyxLauncher.Services;

/// <summary>背景图的低分辨率 BGRA 采样；按 UniformToFill 的裁切与实际透明度计算局部可见底色。</summary>
public sealed class BackgroundContrast
{
    private readonly byte[] _pixels;
    private readonly int _width;
    private readonly int _height;

    /// <summary>保存已解码的直通 Alpha 像素；尺寸或缓冲长度不一致时拒绝输入。</summary>
    public BackgroundContrast(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || (long)width * height * 4 != pixels.Length)
            throw new ArgumentException("背景采样尺寸无效。");
        _width = width; _height = height; _pixels = pixels;
    }

    /// <summary>对文字矩形内的 4×4 点采样，将图片叠加到底色后返回按亮度排序的中位颜色，减少局部亮斑、暗斑干扰；映射包含居中裁切。</summary>
    public uint Sample(double x, double y, double width, double height, double viewportWidth, double viewportHeight, uint surface, double opacity)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0) return surface;
        double scale = Math.Max(viewportWidth / _width, viewportHeight / _height);
        double offsetX = (_width * scale - viewportWidth) / 2, offsetY = (_height * scale - viewportHeight) / 2;
        // shortcut: 使用局部代表色，若需保证黑白交错纹理上的每个字清晰，应增加文字轮廓或底色。
        Span<uint> samples = stackalloc uint[16];
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++)
        {
            int px = Math.Clamp((int)((x + (column + .5) * width / 4 + offsetX) / scale), 0, _width - 1);
            int py = Math.Clamp((int)((y + (row + .5) * height / 4 + offsetY) / scale), 0, _height - 1);
            int index = (py * _width + px) * 4;
            uint color = (uint)(_pixels[index + 3] << 24 | _pixels[index + 2] << 16 | _pixels[index + 1] << 8 | _pixels[index]);
            samples[row * 4 + column] = Composite(color, surface, opacity);
        }
        samples.Sort(static (a, b) => Luminance(a).CompareTo(Luminance(b)));
        return Composite(samples[7], samples[8], .5);
    }

    /// <summary>将带 Alpha 的前景按画刷不透明度叠加到不透明底色，返回实际可见颜色。</summary>
    public static uint Composite(uint foreground, uint background, double opacity)
    {
        double alpha = (foreground >> 24) / 255d * Math.Clamp(opacity, 0, 1);
        uint r = (uint)Math.Round(((foreground >> 16) & 255) * alpha + ((background >> 16) & 255) * (1 - alpha));
        uint g = (uint)Math.Round(((foreground >> 8) & 255) * alpha + ((background >> 8) & 255) * (1 - alpha));
        uint b = (uint)Math.Round((foreground & 255) * alpha + (background & 255) * (1 - alpha));
        return 0xFF000000 | r << 16 | g << 8 | b;
    }

    /// <summary>依据线性亮度选择对比度更高的黑色或白色文字。</summary>
    public static uint TextColor(uint background)
    {
        return Luminance(background) > .179 ? 0xFF000000 : 0xFFFFFFFF;
    }

    /// <summary>为固定白字的原生输入选区生成同色相底色；过亮主色与黑色混合，保证白字至少约 4.5:1 的对比度。</summary>
    public static uint SelectionBackground(uint primary) => TextColor(primary) == 0xFFFFFFFF ? primary : Composite(0xFF000000, primary, .55);

    /// <summary>在卡片底色上保持原字色；对比不足时逐步向黑色或白色混合，返回达到 4.5:1 的近似色。</summary>
    internal static uint ReadableText(uint foreground, uint background)
    {
        double backdrop = Luminance(background);
        uint target = TextColor(background);
        for (int step = 0; step < 20; step++)
        {
            uint candidate = Composite(target, foreground, step / 20d);
            double brightness = Luminance(candidate);
            if ((Math.Max(backdrop, brightness) + .05) / (Math.Min(backdrop, brightness) + .05) >= 4.5)
                return candidate;
        }
        return target;
    }

    /// <summary>计算不透明 sRGB 颜色的线性相对亮度，供局部取样排序与文字对比度共用。</summary>
    private static double Luminance(uint color) => .2126 * Linear((byte)(color >> 16)) + .7152 * Linear((byte)(color >> 8)) + .0722 * Linear((byte)color);

    /// <summary>将 sRGB 分量转换为线性亮度，避免直接平均 RGB 导致中间亮度误判。</summary>
    private static double Linear(byte value) => value / 255d <= .04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
}
