using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NyxLauncher.Views;

namespace NyxLauncher;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _idleCollection = new() { Interval = TimeSpan.FromSeconds(5) };
    private long _lastCollection = -60000;
    private bool _foreground = true;

    /// <summary>连接窗口焦点和一次性后台回收；窗口关闭后停止计时，不创建常驻轮询。</summary>
    private void InitializeMemoryLifecycle()
    {
        Activated += Window_Activated;
        _idleCollection.Tick += CollectIdleMemory;
        Closed += (_, _) => _idleCollection.Stop();
    }

    /// <summary>失焦时暂停可见内存预览及背景文字取样，五秒后回收不可达对象；重新聚焦立即取消待执行回收并恢复原页面采样。</summary>
    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        bool foreground = args.WindowActivationState != WindowActivationState.Deactivated;
        _foreground = foreground;
        _idleCollection.Stop();
        Themes.ThemeManager.Current.SetForeground(foreground);
        SetMemoryForeground(ThemeRoot, foreground);
        if (!foreground && !_closing && Environment.TickCount64 - _lastCollection >= 60000) _idleCollection.Start();
    }

    /// <summary>只在焦点变化时遍历现有控件，将窗口状态传给内存采样和衣柜动画；不切换页面或重新读取配置。</summary>
    private static void SetMemoryForeground(DependencyObject element, bool foreground)
    {
        if (element is MemorySettingsView memory) memory.SetForeground(foreground);
        if (element is SkinPreview preview) preview.SetForeground(foreground);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            SetMemoryForeground(VisualTreeHelper.GetChild(element, i), foreground);
    }

    /// <summary>持续失焦后回收一次不可达托管对象并压缩空闲堆，每分钟最多一次；不裁剪工作集，不影响有效下载对象。</summary>
    private void CollectIdleMemory(object? sender, object args)
    {
        _idleCollection.Stop();
        if (_foreground || _closing || Environment.TickCount64 - _lastCollection < 60000 ||
            RootFrame.Content is MainPage { HasMemorySensitiveWork: true }) return;
        if (RootFrame.Content is MainPage page) page.ReleaseUnusedViews();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, CollectReleasedMemory);
    }

    /// <summary>让控件卸载回调先执行，再回收脱离视觉树的对象；等待期间若恢复前台或开始业务则跳过，回收仍按一分钟限频。</summary>
    private void CollectReleasedMemory()
    {
        if (_foreground || _closing || Environment.TickCount64 - _lastCollection < 60000 ||
            RootFrame.Content is MainPage { HasMemorySensitiveWork: true }) return;
        _lastCollection = Environment.TickCount64;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
