using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>全局与版本共用高级启动卡片，加载时不写配置，局部编辑只写当前版本。</summary>
public sealed partial class AdvancedLaunchSettingsView : UserControl
{
    private Func<InstalledVersion?>? _target;
    private bool _local, _updating;
    private AdvancedLaunchSettings? _saved;

    /// <summary>初始化布局；尚未连接作用域时忽略控件事件。</summary>
    public AdvancedLaunchSettingsView() => InitializeComponent();

    /// <summary>连接当前版本委托；全局模式始终读写全局文件。</summary>
    public void Configure(Func<InstalledVersion?> target, bool local) { _target = target; _local = local; }

    /// <summary>打开卡片时重新读取配置，关闭时无需后台任务。</summary>
    public void SetActive(bool active) { if (active) Refresh(); }

    /// <summary>获取保存目标；局部模式缺少版本时报错，防止意外写入全局。</summary>
    private InstalledVersion? Target() => _local ? _target?.Invoke() ?? throw new IOException("尚未选择版本。") : null;

    /// <summary>填充当前作用域值并更新恢复按钮；坏配置保持禁写并显示错误。</summary>
    private void Refresh()
    {
        if (_target is null) return;
        _updating = true;
        _saved = null;
        try
        {
            var target = Target();
            var value = AdvancedLaunchSettingsStore.Read(target);
            JvmInput.Text = value.JvmArguments;
            GameInput.Text = value.GameArguments;
            Collector.SelectedIndex = (int)value.Collector;
            DisableWrapper.IsChecked = value.DisableJavaWrapper;
            DisableAgent.IsChecked = value.DisableLwjglAgent;
            HighPerformance.IsChecked = value.HighPerformanceGpu;
            ResetButton.Visibility = _local ? Visibility.Visible : Visibility.Collapsed;
            ResetButton.IsEnabled = target is not null && File.Exists(AdvancedLaunchSettingsStore.PathFor(target));
            _saved = value;
            Error.IsOpen = false;
        }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
        finally { _updating = false; }
    }

    /// <summary>文本编辑结束时保存，避免逐字符写盘和创建未改动的局部覆盖。</summary>
    private void Input_LostFocus(object sender, RoutedEventArgs e) => Save();

    /// <summary>用户点击复选框后保存全部高级选项。</summary>
    private void Option_Click(object sender, RoutedEventArgs e) => Save();

    /// <summary>用户切换 GC 时保存，程序初始化事件由保存入口过滤。</summary>
    private void Collector_Changed(object sender, SelectionChangedEventArgs e) => Save();

    /// <summary>仅实际改动生成作用域快照；失败保留输入并显示错误，允许修正后重试。</summary>
    private void Save()
    {
        if (_updating || _target is null || _saved is null) return;
        var value = new AdvancedLaunchSettings(JvmInput.Text, GameInput.Text, (GarbageCollector)Collector.SelectedIndex,
            DisableWrapper.IsChecked == true, DisableAgent.IsChecked == true, HighPerformance.IsChecked == true);
        if (value == _saved) return;
        try
        {
            AdvancedLaunchSettingsStore.Save(value, Target());
            _saved = value;
            ResetButton.IsEnabled = _local;
            Error.IsOpen = false;
        }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
    }

    /// <summary>移除本版本高级覆盖并刷新；不触碰其他版本与内存偏好。</summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        try { if (_local && Target() is { } target) AdvancedLaunchSettingsStore.Reset(target); Refresh(); }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
    }
}
