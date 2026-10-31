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

    /// <summary>仅在当前内存板块可见时采样，打开时立即恢复最新继承状态。</summary>
    public void SetActive(bool active)
    {
        _timer.Stop();
        if (active) { Refresh(true); _timer.Start(); }
    }

    /// <summary>每半秒只采样系统内存并更新预览，使用缓存偏好与模组数，不访问磁盘或改写手动滑块。</summary>
    private void Refresh_Tick(object? sender, object e)
    {
        try { RenderMemory(); }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
    }

    /// <summary>页面卸载时释放定时采样。</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e) => _timer.Stop();

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
            ResetButton.Visibility = _local ? Visibility.Visible : Visibility.Collapsed;
            ResetButton.IsEnabled = target is not null && File.Exists(MemorySettingsStore.PathFor(target));
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

    /// <summary>只删除当前版本覆盖并立即显示最新全局值；失败保留并展示原配置。</summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        try { if (_local && _version?.Invoke() is { } version) MemorySettingsStore.Reset(version); Refresh(); }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
    }
}
