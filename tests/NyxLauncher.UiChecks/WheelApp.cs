using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NyxLauncher.Views;
using WinRT.Interop;

namespace NyxLauncher;

/// <summary>用真实控件与真实鼠标滚轮验证选择器收起时不会改选：普通 ComboBox 覆盖悬停、键盘焦点、点击后滚轮与可编辑模式作对照，ScrollSafeComboBox 必须保持选中项并把滚轮交给外层页面。</summary>
public partial class App : Application
{
    private Window? _window;
    private readonly string _log = Path.Combine(AppContext.BaseDirectory, "wheel-checks.log");

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        File.WriteAllText(_log, "START\n");
        try
        {
            var focusAnchor = new Button { Content = "焦点占位", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            var plain = BuildCombo(new ComboBox());
            var safe = BuildCombo(new ScrollSafeComboBox());
            var plainOutside = BuildCombo(new ComboBox());
            var safeOutside = BuildCombo(new ScrollSafeComboBox());
            var editable = BuildCombo(new ComboBox { IsEditable = true, IsTextSearchEnabled = false });
            var editableSafe = BuildCombo(new ScrollSafeComboBox { IsEditable = true, IsTextSearchEnabled = false });

            int plainChanges = 0, safeChanges = 0;
            plain.SelectionChanged += (_, _) => plainChanges++;
            safe.SelectionChanged += (_, _) => safeChanges++;
            plain.PointerEntered += (_, _) => Log("命中：指针进入普通 ComboBox");
            safe.PointerEntered += (_, _) => Log("命中：指针进入 ScrollSafeComboBox");

            var panel = new StackPanel { Width = 420, Spacing = 12, Padding = new Thickness(24) };
            panel.Children.Add(focusAnchor);
            panel.Children.Add(new TextBlock { Text = "普通 ComboBox（对照）" });
            panel.Children.Add(plain);
            panel.Children.Add(new TextBlock { Text = "ScrollSafeComboBox" });
            panel.Children.Add(safe);
            panel.Children.Add(new Border { Height = 1200 });

            var scroller = new ScrollViewer { Height = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
            var outside = new StackPanel { Spacing = 8, Padding = new Thickness(24, 4, 24, 0) };
            outside.Children.Add(new TextBlock { Text = "页面外（无外层滚动容器，对照）" });
            outside.Children.Add(plainOutside);
            outside.Children.Add(safeOutside);
            outside.Children.Add(editable);
            outside.Children.Add(editableSafe);

            var root = new Grid { Width = 470, Height = 500, Background = new SolidColorBrush(Microsoft.UI.Colors.White) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(250) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(scroller, 0);
            Grid.SetRow(outside, 1);
            root.Children.Add(scroller);
            root.Children.Add(outside);

            _window = new Window { Title = "Wheel UI checks", Content = root };
            _window.Activate();
            _window.AppWindow.Resize(new(560, 620));
            await Task.Delay(1500);

            var hwnd = WindowNative.GetWindowHandle(_window);
            Check(hwnd != IntPtr.Zero, "窗口句柄有效");
            Log($"验收窗口前台={EnsureForeground(hwnd)}（失败时依赖系统“悬停滚动非活动窗口”，页面滚动断言会反证滚轮已送达）");

            // 对照组：默认 ComboBox 在各场景下的实际行为只写入日志，避免 Windows 以后修正默认行为时误判失败。
            await PrepareAsync(hwnd, scroller);
            await WheelAsync(plain, root, hwnd, 3);
            await Task.Delay(500);
            Log($"对照：普通 ComboBox 悬停滚轮后 SelectedIndex={plain.SelectedIndex}，SelectionChanged={plainChanges} 次");

            await PrepareAsync(hwnd, scroller);
            bool plainFocused = plain.Focus(FocusState.Keyboard);
            await Task.Delay(200);
            Log($"对照：普通 ComboBox 请求键盘焦点 {plainFocused}，实际 FocusState={plain.FocusState}");
            await WheelAsync(plain, root, hwnd, 3);
            await Task.Delay(500);
            Log($"对照：普通 ComboBox 键盘焦点滚轮后 SelectedIndex={plain.SelectedIndex}，SelectionChanged={plainChanges} 次");

            await PrepareAsync(hwnd, scroller);
            await ClickAsync(plain, root, hwnd);
            await Task.Delay(700);
            bool opened = plain.IsDropDownOpen;
            await PressEscapeAsync();
            await Task.Delay(400);
            await PrepareAsync(hwnd, scroller);
            await WheelAsync(plain, root, hwnd, 3);
            await Task.Delay(500);
            Log($"对照：普通 ComboBox 点击展开（{(opened ? "已展开" : "未展开")}）并关闭后滚轮 SelectedIndex={plain.SelectedIndex}，SelectionChanged={plainChanges} 次");

            await PrepareAsync(hwnd, scroller);
            editable.Focus(FocusState.Keyboard);
            await Task.Delay(200);
            Log($"对照：可编辑 ComboBox 实际 FocusState={editable.FocusState}");
            await WheelAsync(editable, root, hwnd, 3);
            await Task.Delay(500);
            Log($"对照：可编辑 ComboBox 键盘焦点滚轮后 SelectedIndex={editable.SelectedIndex}，文本={editable.Text}");

            await PrepareAsync(hwnd, scroller);
            await WheelAsync(plainOutside, root, hwnd, 3);
            await Task.Delay(500);
            Log($"对照：页面外普通 ComboBox 悬停滚轮后 SelectedIndex={plainOutside.SelectedIndex}");

            // 1. 安全控件仅悬停、收起时滚轮不得改选，并且外层页面继续滚动。
            await PrepareAsync(hwnd, scroller);
            focusAnchor.Focus(FocusState.Programmatic);
            await Task.Delay(200);
            double before = scroller.VerticalOffset;
            await WheelAsync(safe, root, hwnd, 3);
            await Task.Delay(600);
            double after = scroller.VerticalOffset;
            Check(safe.SelectedIndex == 2 && safeChanges == 0,
                $"ScrollSafeComboBox 悬停收起时滚轮不改选（SelectedIndex={safe.SelectedIndex}，SelectionChanged={safeChanges} 次）");
            Check(after > before, $"悬停滚轮仍交给外层页面滚动（{before:0.#} → {after:0.#}）");

            // 2. 安全控件获得键盘焦点、收起时滚轮不得改选。
            await PrepareAsync(hwnd, scroller);
            bool safeFocused = safe.Focus(FocusState.Keyboard);
            await Task.Delay(200);
            if (!safeFocused) Log($"警告：ScrollSafeComboBox 未取得键盘焦点（FocusState={safe.FocusState}）");
            await WheelAsync(safe, root, hwnd, 3);
            await Task.Delay(600);
            Check(safe.SelectedIndex == 2 && safeChanges == 0,
                $"ScrollSafeComboBox 键盘焦点收起时滚轮不改选（SelectedIndex={safe.SelectedIndex}，SelectionChanged={safeChanges} 次）");

            // 3. 页面外的安全控件同样不改选。
            await PrepareAsync(hwnd, scroller);
            await WheelAsync(safeOutside, root, hwnd, 3);
            await Task.Delay(500);
            Check(safeOutside.SelectedIndex == 2, $"页面外 ScrollSafeComboBox 悬停滚轮不改选（SelectedIndex={safeOutside.SelectedIndex}）");

            // 4. 可编辑安全控件同样不改选。
            await PrepareAsync(hwnd, scroller);
            editableSafe.Focus(FocusState.Keyboard);
            await Task.Delay(200);
            await WheelAsync(editableSafe, root, hwnd, 3);
            await Task.Delay(500);
            Check(editableSafe.SelectedIndex == 2, $"可编辑 ScrollSafeComboBox 滚轮不改选（SelectedIndex={editableSafe.SelectedIndex}）");

            // 5. 展开状态：滚轮滚动下拉列表的行为只写入日志（收起状态才是本次要求的场景）。
            await PrepareAsync(hwnd, scroller);
            plain.IsDropDownOpen = true;
            await Task.Delay(700);
            var (plainX, plainY) = ScreenPoint(plain, root, hwnd);
            await WheelAtAsync(plainX, plainY + 70, 2);
            await Task.Delay(600);
            Log($"对照：普通 ComboBox 展开后在下拉列表上滚轮 SelectedIndex={plain.SelectedIndex}，IsDropDownOpen={plain.IsDropDownOpen}");
            plain.IsDropDownOpen = false;
            await Task.Delay(300);

            // 6. 收起状态保留下拉列表的默认行为：弹层仍然能打开。
            safe.IsDropDownOpen = true;
            await Task.Delay(600);
            Check(VisualTreeHelper.GetOpenPopupsForXamlRoot(safe.XamlRoot).Count > 0, "ScrollSafeComboBox 展开时仍弹出下拉层");
            safe.IsDropDownOpen = false;
            await Task.Delay(300);

            Log("PASS");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            Log("FAIL " + ex);
        }
        try { _window?.Close(); } catch (Exception) { }
        Exit();
    }

    /// <summary>每次注入前尝试把窗口提到前台并回到页面顶部，保证场景可比。</summary>
    private async Task PrepareAsync(IntPtr hwnd, ScrollViewer scroller)
    {
        EnsureForeground(hwnd);
        await ResetScrollAsync(scroller);
    }

    /// <summary>统一的下拉框外形与固定选中项，保证各场景可比。</summary>
    private static T BuildCombo<T>(T box) where T : ComboBox
    {
        box.Width = 320;
        box.Height = 36;
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.SelectedIndex = 2;
        for (int i = 0; i < 6; i++) box.Items.Add("选项 " + i);
        return box;
    }

    /// <summary>把滚轮注入到控件中心；窗口必须位于前台才能被系统投递。</summary>
    private async Task WheelAsync(FrameworkElement element, UIElement root, IntPtr hwnd, int notches)
    {
        var (x, y) = ScreenPoint(element, root, hwnd);
        SetCursorPos(x, y);
        await Task.Delay(200);
        Log($"悬停命中检查：{element.GetType().Name}（光标 {x},{y}）");
        await WheelAtAsync(x, y, notches);
    }

    /// <summary>在指定屏幕坐标注入滚轮，用于覆盖下拉弹层内部的列表。</summary>
    private async Task WheelAtAsync(int x, int y, int notches)
    {
        SetCursorPos(x, y);
        await Task.Delay(200);
        var inputs = new[]
        {
            new INPUT
            {
                type = InputMouse,
                mouse = new MOUSEINPUT { mouseData = unchecked((uint)(-120 * notches)), dwFlags = MouseEventWheel },
            },
        };
        uint sent = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        if (sent != 1)
        {
            throw new Exception($"滚轮注入失败（{notches} 格，光标 {x},{y}）：桌面可能已锁屏或验收窗口无法位于前台，请在解锁的桌面会话中重跑并保持窗口前台");
        }
        Log($"滚轮已注入（{notches} 格，光标 {x},{y}）");
    }

    /// <summary>在控件中心按下并抬起左键，用于制造真实指针焦点。</summary>
    private static async Task ClickAsync(FrameworkElement element, UIElement root, IntPtr hwnd)
    {
        var (x, y) = ScreenPoint(element, root, hwnd);
        SetCursorPos(x, y);
        await Task.Delay(200);
        SendInput(1, new[] { new INPUT { type = InputMouse, mouse = new MOUSEINPUT { dwFlags = MouseEventLeftDown } } }, Marshal.SizeOf<INPUT>());
        await Task.Delay(80);
        SendInput(1, new[] { new INPUT { type = InputMouse, mouse = new MOUSEINPUT { dwFlags = MouseEventLeftUp } } }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>发送 Esc，关闭点击展开的下拉层。</summary>
    private static async Task PressEscapeAsync()
    {
        SendInput(1, new[] { new INPUT { type = InputKeyboard, keyboard = new KEYBDINPUT { wVk = 0x1B } } }, Marshal.SizeOf<INPUT>());
        await Task.Delay(100);
    }

    /// <summary>换算元素中心相对窗口客户区的屏幕坐标（按窗口 DPI 缩放）。</summary>
    private static (int X, int Y) ScreenPoint(FrameworkElement element, UIElement root, IntPtr hwnd)
    {
        var point = element.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(element.ActualWidth / 2, element.ActualHeight / 2));
        double scale = GetDpiForWindow(hwnd) / 96.0;
        var screen = new POINT { X = (int)Math.Round(point.X * scale), Y = (int)Math.Round(point.Y * scale) };
        ClientToScreen(hwnd, ref screen);
        return (screen.X, screen.Y);
    }

    /// <summary>瞬时回到顶部，避免上一次滚轮改变本次基线。</summary>
    private static async Task ResetScrollAsync(ScrollViewer scroller)
    {
        scroller.ChangeView(null, 0, null, true);
        await Task.Delay(300);
    }

    /// <summary>等待验收窗口获得前台；常规调用失败时置顶并点一下标题栏，用真实激活动作补前台。</summary>
    private static bool EnsureForeground(IntPtr hwnd)
    {
        for (int i = 0; i < 12; i++)
        {
            if (GetForegroundWindow() == hwnd) return true;
            ForceForeground(hwnd);
            Thread.Sleep(100);
        }
        if (GetForegroundWindow() == hwnd) return true;
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);
        SetCursorPos(origin.X + 120, Math.Max(0, origin.Y - 16));
        SendInput(1, new[] { new INPUT { type = InputMouse, mouse = new MOUSEINPUT { dwFlags = MouseEventLeftDown } } }, Marshal.SizeOf<INPUT>());
        SendInput(1, new[] { new INPUT { type = InputMouse, mouse = new MOUSEINPUT { dwFlags = MouseEventLeftUp } } }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(300);
        SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>把本窗口提到前台：附加到当前前台线程后调用 SetForegroundWindow。</summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = false;
        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            attached = AttachThreadInput(currentThread, foregroundThread, true);
        }
        ShowWindow(hwnd, 9);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        SetForegroundWindow(hwnd);
        SetActiveWindow(hwnd);
        if (attached) AttachThreadInput(currentThread, foregroundThread, false);
    }

    /// <summary>记录具体断言并在失败时中断验收。</summary>
    private void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Log(message);
    }

    private void Log(string line) => File.AppendAllText(_log, line + "\n");

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventWheel = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mouse;
        [FieldOffset(8)] public KEYBDINPUT keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hwnd);
}
