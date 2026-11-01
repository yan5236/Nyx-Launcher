using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Themes;

namespace NyxLauncher;

/// <summary>统一处理独立弹窗视觉树的主题与系统明暗切换。</summary>
internal static class ThemedDialog
{
    /// <summary>显示弹窗前应用窗口主题，打开期间响应系统切换，关闭或失败时解除订阅并返回原生按钮结果。</summary>
    public static async Task<ContentDialogResult> ShowThemedAsync(this ContentDialog dialog)
    {
        var themes = ThemeManager.Current;
        // 弹窗脱离窗口视觉树，需显式同步主题。
        void Refresh() => themes.ApplyToDialog(dialog);
        Refresh();
        themes.Changed += Refresh;
        try { return await dialog.ShowAsync(); }
        finally { themes.Changed -= Refresh; }
    }
}
