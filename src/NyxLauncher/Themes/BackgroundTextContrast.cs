using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NyxLauncher.Services;
using Windows.UI;

namespace NyxLauncher.Themes;

/// <summary>仅为图片上暴露的文字与图标调整对比度；卡片和实色按钮保留原来的配色语义。</summary>
public sealed class BackgroundTextContrast
{
    private readonly FrameworkElement _root;
    private readonly Image _image;
    private readonly AppWindow? _window;
    private readonly DispatcherQueueTimer _timer;
    private readonly Dictionary<FrameworkElement, (DependencyProperty Property, object? Original)> _original = new();
    private readonly Dictionary<FrameworkElement, List<(DiscreteObjectKeyFrame Frame, object Original, VisualStateGroup Group)>> _states = new();
    private readonly SolidColorBrush _black = new(Microsoft.UI.Colors.Black);
    private readonly SolidColorBrush _white = new(Microsoft.UI.Colors.White);
    private BackgroundContrast? _sample;

    /// <summary>连接窗口布局和低频更新计时器；调整前景不会改变业务控件、输入或导航状态。</summary>
    public BackgroundTextContrast(FrameworkElement root, Image image, AppWindow? window)
    {
        _root = root; _image = image; _window = window;
        _timer = root.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(120);
        _timer.IsRepeating = false;
        _timer.Tick += Refresh;
        root.LayoutUpdated += LayoutUpdated;
        root.Unloaded += Unloaded;
    }

    /// <summary>替换背景采样，清理旧图上的前景覆盖并在布局稳定后重新计算。</summary>
    public void SetImage(BackgroundContrast? sample) { Restore(); _sample = sample; Invalidate(); }

    /// <summary>将连续的滚动、缩放或设置变化合并为一次对比度更新。</summary>
    public void Invalidate() { if (!_timer.IsRunning) _timer.Start(); }

    /// <summary>窗口或内容布局变化后重新采样实际文字位置，包括侧栏折叠和列表滚动。</summary>
    private void LayoutUpdated(object? sender, object args) { if (_sample is not null) Invalidate(); }

    /// <summary>卸载时停止更新并恢复局部前景，不保留已经移出视觉树的控件。</summary>
    private void Unloaded(object sender, RoutedEventArgs args) { _timer.Stop(); Restore(); }

    /// <summary>恢复所有由自适应覆盖的前景；未显式设置的属性恢复继承，原有画刷对象继续接收主题变化。</summary>
    public void Restore()
    {
        foreach (var pair in _original) RestoreElement(pair.Key, pair.Value);
        _original.Clear();
        if (_window?.TitleBar is { } titleBar) titleBar.ButtonForegroundColor = null;
    }

    /// <summary>恢复原有前景；模板未提供本地画刷时清除覆盖，重新启用样式或继承，避免把 null 写成新的本地值。</summary>
    private void RestoreElement(FrameworkElement element, (DependencyProperty Property, object? Original) saved)
    {
        if (saved.Original is null || saved.Original == DependencyProperty.UnsetValue) element.ClearValue(saved.Property);
        else element.SetValue(saved.Property, saved.Original);
        if (_states.Remove(element, out var states))
        {
            foreach (var entry in states) entry.Frame.Value = entry.Original;
            foreach (var group in states.Select(entry => entry.Group).Distinct()) group.CurrentState?.Storyboard?.Begin();
        }
    }

    /// <summary>根据文字所在区域的图片和祖先半透明底色选择黑白；超过 80% 覆盖的卡片和按钮使用其既有主题。</summary>
    private void Refresh(DispatcherQueueTimer sender, object args)
    {
        if (_sample is null || _image.Source is null || _image.Opacity <= 0 || _root.ActualWidth <= 0) { Restore(); return; }
        var visited = new HashSet<FrameworkElement>();
        foreach (var element in Descendants(_root).OfType<FrameworkElement>())
        {
            var property = ForegroundProperty(element);
            if (property is null || element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
            var rect = element.TransformToVisual(_root).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
            if (rect.Right <= 0 || rect.Bottom <= 0 || rect.X >= _root.ActualWidth || rect.Y >= _root.ActualHeight) continue;
            var surfaces = new List<SolidColorBrush>();
            double coverage = 0;
            Brush? previous = null;
            bool enabled = true;
            for (DependencyObject? node = element; node is not null && node != _root; node = VisualTreeHelper.GetParent(node))
            {
                if (node is Control control && !control.IsEnabled) enabled = false;
                Brush? brush = node switch { Border border => border.Background, Panel panel => panel.Background, Control surfaceControl => surfaceControl.Background, _ => null };
                if (brush is SolidColorBrush solid && !ReferenceEquals(brush, previous))
                {
                    double alpha = solid.Color.A / 255d * solid.Opacity;
                    if (alpha > 0) { surfaces.Add(solid); coverage = 1 - (1 - coverage) * (1 - alpha); }
                }
                if (brush is not null) previous = brush;
            }
            if (!enabled || coverage >= .8) continue;
            uint color = Sample(rect);
            for (int i = surfaces.Count - 1; i >= 0; i--) color = BackgroundContrast.Composite(Argb(surfaces[i].Color), color, surfaces[i].Opacity);
            if (!_original.ContainsKey(element))
            {
                object? original = element.ReadLocalValue(property);
                if (original is not null && original != DependencyProperty.UnsetValue && original is not Brush) continue;
                _original[element] = (property, original);
            }
            visited.Add(element);
            var foreground = BackgroundContrast.TextColor(color) == 0xFFFFFFFF ? _white : _black;
            UpdateSelectorStates(element, foreground);
            if (!ReferenceEquals(element.GetValue(property), foreground)) element.SetValue(property, foreground);
        }
        foreach (var element in _original.Keys.Where(element => !visited.Contains(element)).ToArray())
        { RestoreElement(element, _original[element]); _original.Remove(element); }
        if (_window?.TitleBar is { } titleBar)
        {
            uint color = Sample(new(Math.Max(0, _root.ActualWidth - 138), 0, 138, 32));
            var foreground = BackgroundContrast.TextColor(color) == 0xFFFFFFFF ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            if (titleBar.ButtonForegroundColor != foreground) titleBar.ButtonForegroundColor = foreground;
        }
    }

    /// <summary>同步分类标签的前景动画，防止悬停或选中动画覆盖自适应颜色；保存原值供移除图片和禁用时恢复。</summary>
    private void UpdateSelectorStates(FrameworkElement element, SolidColorBrush foreground)
    {
        if (!_states.TryGetValue(element, out var entries))
        {
            DependencyObject? owner = VisualTreeHelper.GetParent(element);
            while (owner is not null && owner is not SelectorBarItem) owner = VisualTreeHelper.GetParent(owner);
            if (owner is null || string.IsNullOrEmpty(element.Name)) return;
            entries = new();
            foreach (var node in Descendants(owner).OfType<FrameworkElement>())
            foreach (var group in VisualStateManager.GetVisualStateGroups(node))
            foreach (var state in group.States)
            {
                if (state.Name.Contains("Disabled") || state.Storyboard is null) continue;
                foreach (var animation in state.Storyboard.Children.OfType<ObjectAnimationUsingKeyFrames>())
                {
                    if (Storyboard.GetTargetName(animation) != element.Name || Storyboard.GetTargetProperty(animation) != "Foreground") continue;
                    foreach (var frame in animation.KeyFrames.OfType<DiscreteObjectKeyFrame>()) entries.Add((frame, frame.Value, group));
                }
            }
            _states[element] = entries;
        }
        var changed = new HashSet<VisualStateGroup>();
        foreach (var entry in entries)
        {
            if (ReferenceEquals(entry.Frame.Value, foreground)) continue;
            entry.Frame.Value = foreground;
            changed.Add(entry.Group);
        }
        foreach (var group in changed) group.CurrentState?.Storyboard?.Begin();
    }

    /// <summary>取得需要适配的文字或图标前景属性；图片和有独立绘制语义的控件不参与。</summary>
    private static DependencyProperty? ForegroundProperty(FrameworkElement element) => element switch
    {
        TextBlock => TextBlock.ForegroundProperty,
        RichTextBlock => RichTextBlock.ForegroundProperty,
        IconElement => IconElement.ForegroundProperty,
        TextBox or ComboBox => Control.ForegroundProperty,
        _ => null
    };

    /// <summary>将窗口内的文字矩形转换为图片实际绘制坐标后采样，避免 UniformToFill 溢出布局槽时按窗口宽度重复裁切；叠加图片不透明度和窗口底色。</summary>
    private uint Sample(Windows.Foundation.Rect rect)
    {
        var imageRect = _root.TransformToVisual(_image).TransformBounds(rect);
        return _sample!.Sample(imageRect.X, imageRect.Y, imageRect.Width, imageRect.Height,
            _image.ActualWidth, _image.ActualHeight, Argb(((SolidColorBrush)Application.Current.Resources["ShellBrush"]).Color), _image.Opacity);
    }

    /// <summary>转换 WinUI 颜色为纯业务采样算法使用的 ARGB 数值。</summary>
    private static uint Argb(Color color) => (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B);

    /// <summary>遍历当前窗口的视觉树，弹出菜单属于独立且不透明的表面，不纳入此遍历。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }
}
