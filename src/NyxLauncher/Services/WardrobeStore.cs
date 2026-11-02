using System.Text.Json;
using SkiaSharp;

namespace NyxLauncher.Services;

public sealed record WardrobeSkin(string Id, string Name, bool Slim);

/// <summary>保存本机衣柜的皮肤副本及名称；图片使用随机标识，用户名称不参与路径计算。</summary>
public sealed class WardrobeStore
{
    private readonly string _directory;
    private string IndexPath => Path.Combine(_directory, "skins.json");
    public IReadOnlyList<WardrobeSkin> Skins { get; private set; } = [];
    public string? LoadError { get; private set; }

    /// <summary>读取并校验索引，损坏时保留文件并禁止后续覆盖；可指定目录供隔离测试使用。</summary>
    public WardrobeStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NyxLauncher", "wardrobe");
        try
        {
            if (!File.Exists(IndexPath)) return;
            var skins = JsonSerializer.Deserialize<WardrobeSkin[]>(File.ReadAllText(IndexPath)) ?? throw new InvalidDataException("衣柜索引为空。");
            foreach (var skin in skins)
            {
                if (skin is null || !Guid.TryParseExact(skin.Id, "N", out _)) throw new InvalidDataException("衣柜标识无效。");
                ValidateName(skin.Name);
            }
            if (skins.Select(s => s.Id).Distinct().Count() != skins.Length) throw new InvalidDataException("衣柜存在重复标识。");
            Skins = skins;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { LoadError = "无法读取衣柜，请修复索引后重启：" + ex.Message; }
    }

    /// <summary>校验皮肤名称的长度与控制字符；名称只用于显示，允许中文及重复名称。</summary>
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(char.IsControl))
            throw new ArgumentException("皮肤名称应为 1–64 个字符，不能包含控制字符。");
    }

    /// <summary>只接受官方可上传的 64×64 或经典 64×32 PNG；先验证大小和解码结果，避免异常图片进入衣柜。</summary>
    public static void ValidatePng(byte[] png, bool slim = false, bool cape = false)
    {
        if (png.Length is 0 or > 1024 * 1024) throw new InvalidDataException("纹理必须小于 1 MiB。");
        using var stream = new MemoryStream(png);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.Info.Width != 64 ||
            (cape ? codec.Info.Height != 32 : codec.Info.Height != 64 && (slim || codec.Info.Height != 32)))
            throw new InvalidDataException(cape ? "披风必须为 64×32 PNG。" : "皮肤必须为 64×64 PNG，经典模型也支持 64×32。");
        using var bitmap = new SKBitmap(codec.Info);
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("PNG 图片不完整或已损坏。");
    }

    /// <summary>读取已登记皮肤的独立副本；拒绝外来标识并重新校验，损坏文件不会被应用或导出。</summary>
    public byte[] Read(WardrobeSkin skin)
    {
        RequireSkin(skin);
        string path = Path.Combine(_directory, skin.Id + ".png");
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("皮肤文件超过大小限制。");
        byte[] bytes = File.ReadAllBytes(path);
        ValidatePng(bytes, skin.Slim);
        return bytes;
    }

    /// <summary>按解码像素、尺寸和模型去重，重复导入返回已有条目并保留名称；新 PNG 与索引原子发布，失败不改已有衣柜。</summary>
    public WardrobeSkin Add(string name, bool slim, byte[] png)
    {
        ValidateName(name); ValidatePng(png, slim);
        if (LoadError is not null) throw new IOException(LoadError);
        using var incoming = SKBitmap.Decode(png);
        var pixels = incoming.Pixels;
        foreach (var existing in Skins.Where(s => s.Slim == slim))
        {
            try
            {
                using var bitmap = SKBitmap.Decode(Read(existing));
                if (bitmap.Width == incoming.Width && bitmap.Height == incoming.Height && bitmap.Pixels.SequenceEqual(pixels)) return existing;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { /* 损坏副本保留给用户处理，不阻止导入有效皮肤。 */ }
        }
        var skin = new WardrobeSkin(Guid.NewGuid().ToString("N"), name.Trim(), slim);
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, skin.Id + ".png");
        File.WriteAllBytes(path, png);
        try { Save(Skins.Append(skin).ToArray()); }
        catch { File.Delete(path); throw; }
        return skin;
    }

    /// <summary>仅更新本地名称，索引发布成功后才更新内存；失败保留原名称。</summary>
    public void Rename(WardrobeSkin skin, string name)
    {
        RequireSkin(skin); ValidateName(name);
        Save(Skins.Select(s => s == skin ? s with { Name = name.Trim() } : s).ToArray());
    }

    /// <summary>从衣柜移除皮肤并清理副本，不改变服务器当前皮肤；索引失败保留图片，清理失败会明确传播。</summary>
    public void Remove(WardrobeSkin skin)
    {
        RequireSkin(skin);
        Save(Skins.Where(s => s != skin).ToArray());
        File.Delete(Path.Combine(_directory, skin.Id + ".png"));
    }

    /// <summary>验证皮肤仍属于当前索引，阻止任意路径访问和过期菜单操作。</summary>
    private void RequireSkin(WardrobeSkin skin)
    {
        if (!Skins.Contains(skin)) throw new InvalidOperationException("皮肤已改变或删除。");
    }

    /// <summary>通过同目录临时文件原子替换索引，损坏索引和写入失败不覆盖已有数据。</summary>
    private void Save(WardrobeSkin[] skins)
    {
        if (LoadError is not null) throw new IOException(LoadError);
        Directory.CreateDirectory(_directory);
        string temporary = IndexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(skins)); File.Move(temporary, IndexPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Skins = skins;
    }
}
