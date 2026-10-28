namespace NyxLauncher.Services;

/// <summary>人工核对的 Java 愚人节版本 ID；来源和非清单版本边界见 docs/april-fools-versions.md。</summary>
public static class AprilFoolsVersions
{
    public static IReadOnlySet<string> Ids { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "15w14a", "1.RV-Pre1", "3D Shareware v1.34", "20w14infinite",
        "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine", "26w14a"
    };
}
