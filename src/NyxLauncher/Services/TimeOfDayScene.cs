namespace NyxLauncher.Services;

/// <summary>内置背景的八个时段场景；按本地时间选择，使窗口背景随一天的光线变化。</summary>
public static class TimeOfDayScene
{
    /// <summary>各时段的起始时刻（小时，含）与场景文件名，按时间升序覆盖全天；首项从零点开始。</summary>
    private static readonly (double Start, string Name)[] Scenes =
    [
        (0, "midnight"),
        (4, "predawn"),
        (5.5, "sunrise"),
        (7, "morning"),
        (11, "noon"),
        (16, "sunset"),
        (18, "dusk"),
        (19.5, "night"),
        (23, "midnight"),
    ];

    /// <summary>全部场景文件名（去重），供资源完整性检查使用。</summary>
    public static IReadOnlyList<string> Names { get; } = Scenes.Select(scene => scene.Name).Distinct().ToArray();

    /// <summary>返回指定本地时刻所在时段的场景名；边界时刻归属后一个时段。</summary>
    public static string Resolve(DateTime local)
    {
        double hour = local.TimeOfDay.TotalHours;
        for (int i = Scenes.Length - 1; i >= 0; i--)
            if (hour >= Scenes[i].Start) return Scenes[i].Name;
        return Scenes[0].Name;
    }

    /// <summary>返回场景图片在程序目录中的绝对路径；单文件发布时同样解压到程序基目录。</summary>
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", "Scenes", name + ".jpg");
}
