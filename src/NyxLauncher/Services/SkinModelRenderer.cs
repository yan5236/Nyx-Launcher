using System.Numerics;
using SkiaSharp;

namespace NyxLauncher.Services;

/// <summary>以正交相机绘制 Minecraft 方块模型，按深度排序纹理面；用于动态预览与静态衣柜缩略图。</summary>
internal sealed class SkinModelRenderer : IDisposable
{
    private SKBitmap? _skin;
    private SKBitmap? _cape;
    private bool _slim;
    private readonly List<Face> _faces = [];
    private sealed record Face(Vector3[] Points, SKPoint[] Uv, SKBitmap Texture, byte Light);

    /// <summary>替换并解码已验证纹理；释放上一份原生位图，空皮肤表示清空预览。</summary>
    public void SetTextures(byte[]? skin, bool slim, byte[]? cape)
    {
        _skin?.Dispose(); _cape?.Dispose();
        _skin = skin is null ? null : SKBitmap.Decode(skin);
        _cape = cape is null ? null : SKBitmap.Decode(cape);
        _slim = slim;
        if (_skin is not null)
        {
            MakeOpaque(0, 0, 32, 16); MakeOpaque(0, 16, 64, 32);
            if (_skin.Height == 64) MakeOpaque(16, 48, 48, 64);
        }
    }

    /// <summary>游戏强制底层不透明；保留底层颜色并忽略其 alpha，外层透明度保持原样。</summary>
    private void MakeOpaque(int left, int top, int right, int bottom)
    {
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++) _skin!.SetPixel(x, y, _skin.GetPixel(x, y).WithAlpha(255));
    }

    /// <summary>绘制当前姿态到调用方位图；phase 控制相反方向摆臂摆腿，yaw/pitch 与 zoom 只改变相机。</summary>
    public void Render(SKBitmap target, float phase, float yaw = .55f, float pitch = -.12f, float zoom = 1)
    {
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        if (_skin is null) return;
        _faces.Clear();
        float swing = MathF.Sin(phase) * .55f;
        bool modern = _skin.Height == 64;
        int arm = _slim ? 3 : 4;
        AddPart(new(-4, -16, -4), new(8, 8, 8), 0, 0, Vector3.Zero, 0, 0, _skin);
        AddPart(new(-4, -8, -2), new(8, 12, 4), 16, 16, Vector3.Zero, 0, 0, _skin);
        AddPart(new(-4 - arm, -8, -2), new(arm, 12, 4), 40, 16, new(-4 - arm / 2f, -8, 0), swing, 0, _skin);
        AddPart(new(4, -8, -2), new(arm, 12, 4), modern ? 32 : 40, modern ? 48 : 16, new(4 + arm / 2f, -8, 0), -swing, 0, _skin, !modern);
        AddPart(new(-4, 4, -2), new(4, 12, 4), 0, 16, new(-2, 4, 0), -swing, 0, _skin);
        AddPart(new(0, 4, -2), new(4, 12, 4), modern ? 16 : 0, modern ? 48 : 16, new(2, 4, 0), swing, 0, _skin, !modern);
        if (modern || HasLegacyHat()) AddPart(new(-4, -16, -4), new(8, 8, 8), 32, 0, Vector3.Zero, 0, .5f, _skin);
        if (modern)
        {
            AddPart(new(-4, -8, -2), new(8, 12, 4), 16, 32, Vector3.Zero, 0, .25f, _skin);
            AddPart(new(-4 - arm, -8, -2), new(arm, 12, 4), 40, 32, new(-4 - arm / 2f, -8, 0), swing, .25f, _skin);
            AddPart(new(4, -8, -2), new(arm, 12, 4), 48, 48, new(4 + arm / 2f, -8, 0), -swing, .25f, _skin);
            AddPart(new(-4, 4, -2), new(4, 12, 4), 0, 32, new(-2, 4, 0), -swing, .25f, _skin);
            AddPart(new(0, 4, -2), new(4, 12, 4), 0, 48, new(2, 4, 0), swing, .25f, _skin);
        }
        if (_cape is not null)
            AddPart(new(-5, -8, -3.3f), new(10, 16, 1), 0, 0, new(0, -8, -3), -.12f - MathF.Abs(swing) * .3f, 0, _cape);
        var camera = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(pitch);
        float scale = Math.Min(target.Width / 28f, target.Height / 39f) * zoom;
        var faces = _faces.Select(f => (Face: f, Points: f.Points.Select(p => Vector3.Transform(p, camera)).ToArray()))
            .OrderBy(f => f.Points.Average(p => p.Z));
        foreach (var (face, points) in faces)
        {
            var normal = Vector3.Cross(points[1] - points[0], points[2] - points[0]);
            if (normal.Z <= 0) continue;
            var positions = points.Select(p => new SKPoint(target.Width / 2f + p.X * scale, target.Height / 2f + p.Y * scale)).ToArray();
            var color = new SKColor(face.Light, face.Light, face.Light);
            using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, face.Uv, [color, color, color, color], [0, 1, 2, 0, 2, 3]);
            using var shader = face.Texture.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest));
            using var paint = new SKPaint { Shader = shader, IsAntialias = false };
            canvas.DrawVertices(vertices, SKBlendMode.Modulate, paint);
        }
    }

    /// <summary>旧式皮肤的全不透明帽子区域视为未使用，避免整块背景色遮住面部。</summary>
    private bool HasLegacyHat()
    {
        for (int y = 0; y < 16; y++)
            for (int x = 32; x < 64; x++)
                if (_skin!.GetPixel(x, y).Alpha < 128) return true;
        return false;
    }

    /// <summary>按 Minecraft 展开图构造六面体；披风整体绕 Y 轴转半圈再摆动，保证外面、内衬和侧边方向一致，旧式左肢体镜像右肢纹理。</summary>
    private void AddPart(Vector3 origin, Vector3 size, int u, int v, Vector3 pivot, float angle, float inflate, SKBitmap texture, bool mirror = false)
    {
        float x = origin.X - inflate, y = origin.Y - inflate, z = origin.Z - inflate;
        float X = origin.X + size.X + inflate, Y = origin.Y + size.Y + inflate, Z = origin.Z + size.Z + inflate;
        float w = size.X, h = size.Y, d = size.Z;
        bool cape = ReferenceEquals(texture, _cape);
        var transform = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateRotationY(cape ? MathF.PI : 0)
            * Matrix4x4.CreateRotationX(angle) * Matrix4x4.CreateTranslation(pivot);
        AddFace([new(x,y,Z), new(X,y,Z), new(X,Y,Z), new(x,Y,Z)], u+d,v+d,w,h, 255);
        AddFace([new(X,y,z), new(x,y,z), new(x,Y,z), new(X,Y,z)], u+2*d+w,v+d,w,h, 210);
        AddFace([new(x,y,z), new(x,y,Z), new(x,Y,Z), new(x,Y,z)], mirror ? u+d+w : u,v+d,d,h, 225);
        AddFace([new(X,y,Z), new(X,y,z), new(X,Y,z), new(X,Y,Z)], mirror ? u : u+d+w,v+d,d,h, 225);
        AddFace([new(x,y,z), new(X,y,z), new(X,y,Z), new(x,y,Z)], u+d,v,w,d, 255);
        AddFace([new(x,Y,Z), new(X,Y,Z), new(X,Y,z), new(x,Y,z)], u+d+w,v,w,d, 180);

        /// <summary>保持统一顶点绕序，映射像素纹理坐标并把面加入深度排序列表。</summary>
        void AddFace(Vector3[] points, float left, float top, float width, float height, byte light)
        {
            float a = mirror ? left + width : left, b = mirror ? left : left + width;
            _faces.Add(new(points.Select(p => Vector3.Transform(p, transform)).ToArray(),
                [new(a,top), new(b,top), new(b,top+height), new(a,top+height)], texture, light));
        }
    }

    /// <summary>释放解码纹理和面列表持有的引用；由预览控件卸载或缩略图生成结束时调用。</summary>
    public void Dispose() { _skin?.Dispose(); _cape?.Dispose(); _skin = _cape = null; _faces.Clear(); }
}
