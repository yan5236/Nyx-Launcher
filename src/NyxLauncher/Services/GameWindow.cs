using System.Runtime.InteropServices;
using System.Text;

namespace NyxLauncher.Services;

/// <summary>按进程归属检查可见游戏窗口，避免把 Java 已创建或隐藏辅助窗口当成启动完成。</summary>
internal static class GameWindow
{
    /// <summary>根据类名、标题与可见性识别游戏渲染窗口，排除加载器早期窗口及辅助窗口；允许模组自定义游戏标题。</summary>
    internal static bool IsGameWindow(string className, string title, bool visible) => visible &&
        className is "GLFW30" or "LWJGL" or "SDL_app" or "SunAwtFrame" &&
        !string.IsNullOrWhiteSpace(title) && title is not ("SDL_app" or "PopupMessageWindow") &&
        !title.StartsWith("GLFW", StringComparison.OrdinalIgnoreCase) &&
        !title.EndsWith("Hidden Utility Window", StringComparison.OrdinalIgnoreCase) &&
        !title.StartsWith("FML", StringComparison.OrdinalIgnoreCase) &&
        !title.StartsWith("Quilt Loader", StringComparison.OrdinalIgnoreCase);

    /// <summary>在 Windows 当前桌面查找该 PID 的真实渲染窗口；未出现时返回 false，不扫描或终止其他 Java 进程。</summary>
    public static bool HasVisibleWindow(int processId)
    {
        bool found = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != processId) return true;
            var className = new StringBuilder(256);
            var title = new StringBuilder(1024);
            GetClassName(window, className, className.Capacity);
            GetWindowText(window, title, title.Capacity);
            found = IsGameWindow(className.ToString(), title.ToString(), IsWindowVisible(window));
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr state);
    /// <summary>调用系统枚举顶层窗口；回调返回 false 时停止。</summary>
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr state);
    /// <summary>读取窗口所属进程 ID，用于精确限定本次启动。</summary>
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    /// <summary>读取原生窗口类名；缓冲区长度包含终止字符。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    /// <summary>读取窗口标题，用于排除隐藏工具和加载器启动窗。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder title, int length);
    /// <summary>检查窗口可见标记；隐藏窗口不能结束等待阶段。</summary>
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}
