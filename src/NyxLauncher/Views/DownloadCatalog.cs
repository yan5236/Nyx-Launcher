using System;
using System.Collections.Generic;

namespace NyxLauncher.Views;

/// <summary>下载页各分类的静态演示数据；不访问网络或本机游戏目录。</summary>
internal static class DownloadCatalog
{
    private const string ModGlyph = "\uE8A5";
    private const string ModPackGlyph = "\uE7B8";
    private const string ResourcePackGlyph = "\uE8B9";
    private const string DataPackGlyph = "\uE8F1";
    private const string ShaderGlyph = "\uE706";

    private static readonly Dictionary<string, (string SearchHint, DownloadEntry[] Entries)> Categories =
        new(StringComparer.Ordinal)
        {
            ["game"] = ("搜索游戏版本", Array.Empty<DownloadEntry>()),
            ["mod"] = ("搜索模组", new[]
            {
                new DownloadEntry("Fabric API 0.115.1", "2025-03-18", "正式版", ModGlyph),
                new DownloadEntry("Sodium 0.6.9", "2025-04-02", "正式版", ModGlyph),
                new DownloadEntry("Lithium 0.14.3", "2025-02-11", "正式版", ModGlyph),
                new DownloadEntry("Iris 1.8.8", "2025-03-27", "正式版", ModGlyph),
                new DownloadEntry("Sodium 0.6.10-beta.1", "2025-04-15", "快照版", ModGlyph),
                new DownloadEntry("OptiFine 1.21.4_HD_U_J3", "2024-12-20", "正式版", ModGlyph),
            }),
            ["modpack"] = ("搜索整合包", new[]
            {
                new DownloadEntry("Fabulously Optimized 6.4.0", "2025-03-09", "正式版", ModPackGlyph),
                new DownloadEntry("Better MC [FORGE] 30", "2025-02-22", "正式版", ModPackGlyph),
                new DownloadEntry("All the Mods 10 2.38", "2025-03-30", "正式版", ModPackGlyph),
                new DownloadEntry("Create: Astral 2.1.1", "2025-01-17", "正式版", ModPackGlyph),
                new DownloadEntry("RLCraft 2.9.3", "2024-11-05", "正式版", ModPackGlyph),
                new DownloadEntry("Poison Potato 1.0", "2024-04-01", "愚人节版", ModPackGlyph),
            }),
            ["resourcepack"] = ("搜索资源包", new[]
            {
                new DownloadEntry("Faithful 32x r45", "2025-03-12", "正式版", ResourcePackGlyph),
                new DownloadEntry("Stay True 1.21.4", "2025-01-28", "正式版", ResourcePackGlyph),
                new DownloadEntry("Bare Bones 1.21.4", "2024-12-18", "正式版", ResourcePackGlyph),
                new DownloadEntry("Fresh Animations 1.9.4", "2025-03-02", "正式版", ResourcePackGlyph),
                new DownloadEntry("Motschen's Better Leaves 3.2", "2024-10-30", "正式版", ResourcePackGlyph),
                new DownloadEntry("Potato UI", "2024-04-01", "愚人节版", ResourcePackGlyph),
            }),
            ["datapack"] = ("搜索数据包", new[]
            {
                new DownloadEntry("Terralith 2.5.4", "2025-03-21", "正式版", DataPackGlyph),
                new DownloadEntry("Incendium 5.1.6", "2025-02-14", "正式版", DataPackGlyph),
                new DownloadEntry("Nullscape 1.2.4", "2025-01-09", "正式版", DataPackGlyph),
                new DownloadEntry("Tectonic 2.4", "2024-12-05", "正式版", DataPackGlyph),
                new DownloadEntry("Vein Mining 1.4.2", "2024-11-23", "正式版", DataPackGlyph),
            }),
            ["shader"] = ("搜索光影", new[]
            {
                new DownloadEntry("Complementary Reimagined r5.4", "2025-03-16", "正式版", ShaderGlyph),
                new DownloadEntry("BSL Shaders 8.4", "2025-01-31", "正式版", ShaderGlyph),
                new DownloadEntry("SEUS PTGI HRR 3", "2024-12-09", "正式版", ShaderGlyph),
                new DownloadEntry("Photon 1.3", "2025-02-25", "正式版", ShaderGlyph),
                new DownloadEntry("Bliss 2.0.4", "2024-11-19", "正式版", ShaderGlyph),
                new DownloadEntry("MakeUp Ultra Fast 9.2", "2025-03-06", "正式版", ShaderGlyph),
            }),
        };

    /// <summary>返回指定分类的演示条目；未知分类返回空列表。</summary>
    /// <param name="category">SelectorBar 标签上的分类键。</param>
    public static IReadOnlyList<DownloadEntry> GetEntries(string category) =>
        Categories.TryGetValue(category, out var value) ? value.Entries : Array.Empty<DownloadEntry>();

    /// <summary>返回指定分类的搜索框占位文本。</summary>
    /// <param name="category">SelectorBar 标签上的分类键。</param>
    public static string GetSearchHint(string category) =>
        Categories.TryGetValue(category, out var value) ? value.SearchHint : "搜索";
}
