namespace NyxLauncher.Views;

/// <summary>游戏版本列表显示条目，来自远程游戏版本清单。</summary>
/// <param name="Title">列表主标题，通常为版本名称。</param>
/// <param name="ReleaseDate">发布时间，使用 yyyy-MM-dd 文本。</param>
/// <param name="Kind">版本类型：正式版、快照版、远古版或愚人节版。</param>
/// <param name="Glyph">条目左侧图标使用的 Segoe Fluent Icons 字形。</param>
public sealed record DownloadEntry(string Title, string ReleaseDate, string Kind, string Glyph);
