using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace NyxLauncher;

/// <summary>在窗口线程管理一个 Windows 托盘图标及其原生菜单，无后台线程或计时器。</summary>
internal sealed class LauncherTrayIcon(
    nint window, DispatcherQueue dispatcher, Action showWindow, Action exitLauncher) : IDisposable
{
    private const uint TrayMessage = 0x8001;
    private const uint ShowCommand = 1;
    private const uint ExitCommand = 2;
    private const uint NotifyAdd = 0, NotifyDelete = 2, NotifySetFocus = 3, NotifySetVersion = 4;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private SubclassProc? _subclass;
    private NotifyIconData _data;
    private bool _added;
    private bool _menuOpen;

    /// <summary>注册托盘图标与窗口回调，失败时返回 false，调用方需释放实例并保持窗口可见。</summary>
    public bool TryShow()
    {
        nint icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), 1, 16, 16, 0x0010);
        if (icon == 0) return false;
        _data = new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1,
            Flags = 0x87, CallbackMessage = TrayMessage, Icon = icon, Tip = "NyxLauncher",
            Info = "", InfoTitle = "", Version = 4
        };
        SubclassProc callback = OnWindowMessage;
        if (!SetWindowSubclass(window, callback, 1, 0)) return false;
        _subclass = callback;
        return AddIcon();
    }

    /// <summary>向当前 Explorer 注册图标并启用版本 4 的鼠标和键盘通知；任一步失败均返回 false。</summary>
    private bool AddIcon()
    {
        _added = ShellNotifyIcon(NotifyAdd, ref _data);
        return _added && ShellNotifyIcon(NotifySetVersion, ref _data);
    }

    /// <summary>接收托盘点击和 Explorer 重启事件；把窗口操作排队，避免在 Shell 回调中重入窗口生命周期。</summary>
    private nint OnWindowMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == TrayMessage)
        {
            switch ((uint)lParam & 0xffff)
            {
                case 0x0202: // WM_LBUTTONUP
                case 0x0400: // NIN_SELECT
                case 0x0401: // NIN_KEYSELECT
                    dispatcher.TryEnqueue(() => { if (_added) showWindow(); });
                    break;
                case 0x007B: // WM_CONTEXTMENU
                    dispatcher.TryEnqueue(ShowMenu);
                    break;
            }
            return 0;
        }
        if (message == _taskbarCreated)
            dispatcher.TryEnqueue(() => { if (_subclass is not null && !AddIcon()) showWindow(); });
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// <summary>在托盘图标旁打开原生菜单，支持键盘与鼠标选择；取消时将焦点交还托盘。</summary>
    private void ShowMenu()
    {
        if (!_added || _menuOpen) return;
        // WM_CONTEXTMENU 的托盘通知不保证携带坐标，向 Explorer 查询图标位置。
        var icon = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = window, Id = _data.Id };
        ScreenPoint point;
        if (Shell_NotifyIconGetRect(ref icon, out var bounds) == 0)
            point = new() { X = bounds.Left, Y = bounds.Top };
        else if (!GetCursorPos(out point)) return;
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        _menuOpen = true;
        uint command;
        try
        {
            if (!AppendMenu(menu, 0, ShowCommand, "显示界面") || !AppendMenu(menu, 0, ExitCommand, "退出启动器")) return;
            SetForegroundWindow(window);
            const uint menuFlags = 0x0100 | 0x0080 | 0x0020 | 0x0002; // RETURNCMD、NONOTIFY、BOTTOMALIGN、RIGHTBUTTON
            command = TrackPopupMenuEx(menu, menuFlags, point.X, point.Y, window, 0);
            PostMessage(window, 0, 0, 0);
        }
        finally { DestroyMenu(menu); _menuOpen = false; }
        if (!_added) return;
        if (command == ShowCommand) showWindow();
        else if (command == ExitCommand) exitLauncher();
        else ShellNotifyIcon(NotifySetFocus, ref _data);
    }

    /// <summary>在窗口销毁前删除图标、卸载窗口回调并归还 HICON，避免托盘留下失效的图标。</summary>
    public void Dispose()
    {
        if (_menuOpen) EndMenu();
        if (_added) ShellNotifyIcon(NotifyDelete, ref _data);
        _added = false;
        if (_subclass is not null) RemoveWindowSubclass(window, _subclass, 1);
        _subclass = null;
        if (_data.Icon != 0) DestroyIcon(_data.Icon);
        _data.Icon = 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenBounds { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint { public int X, Y; }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    /// <summary>在拥有者线程安装原生托盘通知回调。</summary>
    [DllImport("comctl32.dll", ExactSpelling = true)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    /// <summary>用原回调和编号注销窗口子类。</summary>
    [DllImport("comctl32.dll", ExactSpelling = true)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    /// <summary>将其他消息交还系统和窗口原有处理链。</summary>
    [DllImport("comctl32.dll", ExactSpelling = true)] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    /// <summary>按操作码向 Explorer 注册、删除或更新托盘图标状态。</summary>
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    /// <summary>按窗口和图标编号获取托盘图标的屏幕坐标，返回 HRESULT。</summary>
    [DllImport("shell32.dll", ExactSpelling = true)] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier icon, out ScreenBounds bounds);
    /// <summary>取得 Explorer 重建任务栏时广播的消息编号。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    /// <summary>从文件读取图标，返回由调用者释放的句柄。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    /// <summary>释放从文件加载的图标。</summary>
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool DestroyIcon(nint icon);
    /// <summary>返回空原生菜单；失败返回零。</summary>
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint CreatePopupMenu();
    /// <summary>向原生菜单附加带命令编号的文字项。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string label);
    /// <summary>按屏幕坐标显示菜单，等待选择并返回命令编号；取消返回零。</summary>
    [DllImport("user32.dll", ExactSpelling = true)] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);
    /// <summary>结束当前菜单循环，让窗口恢复时不留下孤立菜单。</summary>
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool EndMenu();
    /// <summary>菜单循环返回后释放菜单句柄。</summary>
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool DestroyMenu(nint menu);
    /// <summary>在显示菜单前请求前台焦点，以便点击菜单外时系统能关闭菜单。</summary>
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool SetForegroundWindow(nint hwnd);
    /// <summary>无法定位托盘图标时读取鼠标屏幕位置。</summary>
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool GetCursorPos(out ScreenPoint point);
    /// <summary>在原生菜单关闭后发送空消息，确保后续菜单能正常处理焦点变化。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
}
