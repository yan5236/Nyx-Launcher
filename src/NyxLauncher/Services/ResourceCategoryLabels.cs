namespace NyxLauncher.Services;

/// <summary>Modrinth 资源类型的中文标签；平台标识只用于请求，不直接显示为筛选文本。</summary>
internal static class ResourceCategoryLabels
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["adventure"] = "冒险", ["cursed"] = "整活", ["decoration"] = "装饰", ["economy"] = "经济",
        ["environment"] = "环境", ["equipment"] = "装备", ["food"] = "食物", ["game-mechanics"] = "游戏机制",
        ["library"] = "前置与类库", ["magic"] = "魔法", ["management"] = "管理", ["minigame"] = "小游戏",
        ["mobs"] = "生物", ["optimization"] = "优化", ["social"] = "社交", ["storage"] = "存储",
        ["technology"] = "科技", ["transportation"] = "交通运输", ["utility"] = "实用工具", ["worldgen"] = "世界生成",
        ["challenging"] = "高难度", ["combat"] = "战斗", ["kitchen-sink"] = "大型综合", ["lightweight"] = "轻量",
        ["multiplayer"] = "多人游戏", ["quests"] = "任务", ["modded"] = "模组适配", ["realistic"] = "写实",
        ["simplistic"] = "简约", ["themed"] = "主题", ["tweaks"] = "细节调整", ["vanilla-like"] = "原版风格",
        ["audio"] = "音效", ["blocks"] = "方块", ["core-shaders"] = "核心着色器", ["entities"] = "实体",
        ["fonts"] = "字体", ["gui"] = "界面", ["items"] = "物品", ["locale"] = "语言", ["models"] = "模型",
        ["atmosphere"] = "大气效果", ["bloom"] = "泛光", ["cartoon"] = "卡通", ["colored-lighting"] = "彩色光照",
        ["fantasy"] = "奇幻", ["foliage"] = "植物效果", ["path-tracing"] = "路径追踪", ["pbr"] = "物理材质",
        ["reflections"] = "反射", ["semi-realistic"] = "半写实", ["shadows"] = "阴影",
        ["high"] = "高性能开销", ["medium"] = "中等性能开销", ["low"] = "低性能开销",
        ["potato"] = "低配适用", ["screenshot"] = "截图专用"
    };

    /// <summary>把已知标签或分辨率转换为中文；平台新类型保留统一中文名称，等待补充正式译名。</summary>
    public static string Translate(string id) => Labels.TryGetValue(id, out string? label) ? label : id switch
    {
        "8x-" => "8 倍及以下分辨率", "512x+" => "512 倍及以上分辨率",
        _ => id.EndsWith('x') ? id.TrimEnd('x') + " 倍分辨率" : "其他类型"
    };
}
