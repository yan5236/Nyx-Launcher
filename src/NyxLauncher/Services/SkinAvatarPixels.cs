namespace NyxLauncher.Services;

/// <summary>从未预乘 BGRA 皮肤生成 64×64 预乘头像，保留透明轮廓和正脸/帽子层的相对比例。</summary>
internal static class SkinAvatarPixels
{
    /// <summary>只允许标准或整数倍皮肤布局并限制解码尺寸，拒绝错误尺寸或像素数量。</summary>
    internal static void ValidateSize(int width, int height)
    {
        if (width is < 64 or > 1024 || width % 64 != 0 || height != width && height != width / 2)
            throw new InvalidDataException("皮肤尺寸不符合 Minecraft 布局。");
    }

    /// <summary>正脸内缩四像素，帽子层铺满画布；分别最近邻采样并以预乘透明度合成，旧版全不透明帽子区域忽略。</summary>
    internal static byte[] Create(byte[] pixels, int width, int height)
    {
        ValidateSize(width, height);
        if (pixels.Length != width * height * 4) throw new InvalidDataException("皮肤像素数据长度不正确。");
        int scale = width / 64;
        bool overlay = height == width;
        if (!overlay)
            for (int y = 0; y < 32 * scale && !overlay; y++)
                for (int x = 32 * scale; x < 64 * scale; x++)
                    if (pixels[(y * width + x) * 4 + 3] < 128) { overlay = true; break; }
        byte[] avatar = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                bool insideFace = x >= 4 && x < 60 && y >= 4 && y < 60;
                int face = insideFace ? ((8 * scale + (y - 4) * 8 * scale / 56) * width + 8 * scale + (x - 4) * 8 * scale / 56) * 4 : 0;
                int hat = ((8 * scale + y * 8 * scale / 64) * width + 40 * scale + x * 8 * scale / 64) * 4;
                int output = (y * 64 + x) * 4;
                int hatAlpha = overlay ? pixels[hat + 3] : 0;
                int faceAlpha = insideFace ? pixels[face + 3] : 0;
                // WinUI 位图需要预乘颜色；完全透明的外角保持全零，半透明边缘不会出现深色光晕。
                for (int channel = 0; channel < 3; channel++)
                    avatar[output + channel] = (byte)((pixels[hat + channel] * hatAlpha * 255 + pixels[face + channel] * faceAlpha * (255 - hatAlpha) + 32512) / 65025);
                avatar[output + 3] = (byte)(hatAlpha + (faceAlpha * (255 - hatAlpha) + 127) / 255);
            }
        return avatar;
    }
}
