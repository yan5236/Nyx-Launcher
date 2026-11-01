using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>共用内存设置卡片，支持全局预览与版本独立覆盖。</summary>
public sealed partial class MemorySettingsView : UserControl
{
    private Func<InstalledVersion?>? _version;
    private Func<string?>? _root;
    private Func<IsolationMode>? _isolation;
    private bool _local, _updating;
    private bool _active, _foreground = true;
    private MemorySettings? _settings;
    private InstalledVersion? _previewVersion;
    private int _mods;
    private PhysicalMemory? _lastMemory;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>初始化控件与刷新定时器；卸载时停止采样。</summary>
    public MemorySettingsView()
    {
        InitializeComponent();
        _timer.Tick += Refresh_Tick;
        Unloaded += View_Unloaded;
    }

    /// <summary>提供当前版本、目录及隔离策略；local 决定写入版本配置还是全局配置。</summary>
    public void Configure(Func<InstalledVersion?> version, Func<string?> root, Func<IsolationMode> isolation, bool local)
    { _version = version; _root = root; _isolation = isolation; _local = local; }

    /// <summary>记录板块可见状态，打开时读取继承设置；仅窗口前台且板块可见时持续采样。</summary>
    public void SetActive(bool active)
    {
        _timer.Stop();
        _active = active;
        if (active) { Refresh(true); if (_foreground) _timer.Start(); }
    }

    /// <summary>窗口失焦时暂停采样，重新聚焦后只恢复仍可见板块的定时器，不重扫模组或读写配置。</summary>
    public void SetForeground(bool foreground)
    {
        _foreground = foreground;
        _timer.Stop();
        if (foreground && _active) { Refresh_Tick(null, EventArgs.Empty); _timer.Start(); }
    }

    /// <summary>每半秒用缓存偏好采样内存；忽略失焦或隐藏前已入队的回调，不访问磁盘或改写手动滑块。</summary>
    private void Refresh_Tick(object? sender, object e)
    {
        if (!_active || !_foreground) return;
        try { RenderMemory(); }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
    }

    /// <summary>页面卸载时清除可见状态并停止采样，后续窗口激活不会唤醒已卸载页面。</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e) => SetActive(false);

    /// <summary>打开页面或用户修改时读取偏好并填充控件；仅打开页面重新统计模组，失败清空缓存，避免继续展示旧版本的估算。</summary>
    private void Refresh(bool reloadMods = false)
    {
        if (_version is null) return;
        _updating = true;
        _settings = null;
        _lastMemory = null;
        try
        {
            var version = _version();
            var target = _local ? version ?? throw new IOException("尚未选择版本。") : null;
            var settings = MemorySettingsStore.Read(target);
            if (reloadMods || version != _previewVersion)
                _mods = _root?.Invoke() is { } root ? MemoryAllocation.CountMods(root, version, _isolation!()) : 0;
            _previewVersion = version;
            _settings = settings;
            Automatic.IsChecked = settings.Automatic;
            Manual.IsChecked = !settings.Automatic;
            Allocation.IsEnabled = !settings.Automatic;
            RenderMemory(true);
            Error.IsOpen = false;
        }
        catch (Exception ex) { _settings = null; Error.Message = ex.Message; Error.IsOpen = true; }
        finally { _updating = false; }
    }

    /// <summary>用一次原生查询更新内存数值与占比；相同快照跳过绘制，手动滑块只在初始化时赋值，刷新不触发保存。</summary>
    private void RenderMemory(bool initialize = false)
    {
        if (_settings is not { } settings) return;
        var memory = MemoryAllocation.Snapshot();
        if (!initialize && memory == _lastMemory) return;
        int allocated = MemoryAllocation.Resolve(settings, memory, _previewVersion, _mods);
        bool updating = _updating;
        _updating = true;
        try
        {
            if (initialize)
            {
                Allocation.Maximum = Math.Max(512, memory.TotalMiB / 256 * 256);
                Allocation.Value = settings.Automatic ? allocated : Math.Min(settings.ManualMiB, Allocation.Maximum);
            }
            else if (settings.Automatic && Allocation.Value != allocated) Allocation.Value = allocated;
            UpdateText(UsedLabel, $"已用内存  {(memory.TotalMiB - memory.AvailableMiB) / 1024d:0.00} GB");
            UpdateText(GameLabel, $"游戏分配  {allocated / 1024d:0.00} GB" + (allocated > memory.AvailableMiB ? $"（可用 {memory.AvailableMiB / 1024d:0.00} GB）" : ""));
            UpdateText(TotalLabel, $"总内存 {memory.TotalMiB / 1024d:0.00} GB · 可用 {memory.AvailableMiB / 1024d:0.00} GB");
            UsedColumn.Width = new(Math.Max(0, memory.TotalMiB - memory.AvailableMiB), GridUnitType.Star);
            GameColumn.Width = new(Math.Min(allocated, memory.AvailableMiB), GridUnitType.Star);
            FreeColumn.Width = new(Math.Max(0, memory.AvailableMiB - allocated), GridUnitType.Star);
            _lastMemory = memory;
        }
        finally { _updating = updating; }
    }

    /// <summary>仅在可见文字发生变化时更新文本，避免相同格式化值反复触发布局。</summary>
    private static void UpdateText(TextBlock label, string text)
    {
        if (label.Text != text) label.Text = text;
    }

    /// <summary>用户切换自动或手动模式后保存；程序填充单选状态时不生成版本覆盖。</summary>
    private void Mode_Changed(object sender, RoutedEventArgs e) => Save();

    /// <summary>用户拖动滑块后保存手动内存，允许超过当前空闲量。</summary>
    private void Allocation_Changed(object sender, RangeBaseValueChangedEventArgs e) => Save();

    /// <summary>保存当前作用域的完整偏好，局部修改形成快照；失败恢复持久化值并显示原因。</summary>
    private void Save()
    {
        if (_updating || _version is null) return;
        try
        {
            var target = _local ? _version() ?? throw new IOException("尚未选择版本。") : null;
            var previous = MemorySettingsStore.Read(target);
            MemorySettingsStore.Save(new(Automatic.IsChecked == true, Manual.IsChecked == true ? (int)Allocation.Value : previous.ManualMiB), target);
            Refresh();
        }
        catch (Exception ex) { Refresh(); Error.Message = ex.Message; Error.IsOpen = true; }
    }

}
