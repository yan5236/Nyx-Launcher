using System.Globalization;
using System.Text.Json;

namespace NyxLauncher.Services;

public enum AppearanceMode { System, Light, Dark }
public enum ThemePreset { Green, VuiBlue, Custom }
public sealed record ThemeColors(string Tone, string Primary, string Secondary);
public sealed record ThemeSettings
{
    public AppearanceMode Mode { get; init; } = AppearanceMode.System;
    public ThemePreset Preset { get; init; } = ThemePreset.Green;
    public ThemeColors Light { get; init; } = new("#397450", "#28563B", "#397450");
    public ThemeColors Dark { get; init; } = new("#397450", "#80C99A", "#A2D6B4");
    public string? BackgroundPath { get; init; }
    public string? HomeImagePath { get; init; }
    public bool HideHomeImage { get; init; }
    public double BackgroundOpacity { get; init; } = 0.3;
    public double SidebarOpacity { get; init; } = 0.45;

    /// <summary>按配色与明暗返回色调、主色和副色；自定义的两组颜色独立保存。</summary>
    public ThemeColors Colors(bool dark) => Preset switch
    {
        ThemePreset.Green => dark ? new("#397450", "#80C99A", "#A2D6B4") : new("#397450", "#28563B", "#397450"),
        ThemePreset.VuiBlue => dark ? new("#0078D4", "#60CDFF", "#99DFFF") : new("#0078D4", "#0078D4", "#005A9E"),
        _ => dark ? Dark : Light
    };
}

/// <summary>保存外观偏好；损坏配置保持原文件，成功写盘后才更新内存。</summary>
public sealed class ThemeSettingsStore
{
    private readonly string _path;
    public ThemeSettings Current { get; private set; } = new();
    public string? LoadError { get; private set; }
    public string DirectoryPath => Path.GetDirectoryName(_path)!;

    /// <summary>从指定路径或用户配置目录读取外观；读取失败时提供默认外观及可显示错误。</summary>
    public ThemeSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NyxLauncher", "theme.json");
        try
        {
            if (File.Exists(_path)) Current = Validate(JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(_path)) ?? throw new JsonException("外观设置为空。"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { LoadError = "无法读取外观设置：" + ex.Message; }
    }

    /// <summary>解析不透明 RGB 十六进制颜色；格式无效时抛出参数异常。</summary>
    public static uint ParseColor(string value)
    {
        if (value is null || value.Length != 7 || value[0] != '#' || !uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            throw new ArgumentException("颜色必须为 #RRGGBB。");
        return rgb;
    }

    /// <summary>检查枚举、两组配色及有限的不透明度范围，拒绝损坏或越界设置。</summary>
    private static ThemeSettings Validate(ThemeSettings settings)
    {
        if (!Enum.IsDefined(settings.Mode) || !Enum.IsDefined(settings.Preset) || !double.IsFinite(settings.BackgroundOpacity) || settings.BackgroundOpacity is < 0 or > 1 ||
            !double.IsFinite(settings.SidebarOpacity) || settings.SidebarOpacity is < 0 or > 1)
            throw new ArgumentException("外观设置超出有效范围。");
        foreach (var colors in new[] { settings.Light, settings.Dark })
        {
            if (colors is null) throw new ArgumentException("缺少配色。");
            ParseColor(colors.Tone); ParseColor(colors.Primary); ParseColor(colors.Secondary);
        }
        return settings;
    }

    /// <summary>原子替换配置；读取损坏或写入失败时不覆盖原文件并返回错误。</summary>
    public string? Save(ThemeSettings settings)
    {
        if (LoadError is not null) return LoadError;
        try
        {
            Validate(settings);
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(settings));
            File.Move(_path + ".tmp", _path, true);
            Current = settings;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return "外观设置未保存：" + ex.Message; }
    }
}
