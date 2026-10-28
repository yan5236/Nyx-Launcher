using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

public sealed partial class SettingsView : UserControl
{
    private DownloadSettingsStore? _store;

    /// <summary>加载设置控件；初始化完成前不写配置。</summary>
    public SettingsView() => InitializeComponent();

    /// <summary>注入设置存储，填充源策略、并行文件数、连接数、限速及隔离选项，初始化不写配置并展示读取错误。</summary>
    public void Configure(DownloadSettingsStore store)
    {
        var settings = store.Current;
        FileSource.SelectedIndex = (int)settings.FileSource;
        VersionSource.SelectedIndex = (int)settings.VersionSource;
        Connections.Value = settings.MaxConnections;
        ParallelDownloads.Value = settings.MaxParallelDownloads;
        Speed.Value = settings.SpeedLimitMiB;
        Isolation.SelectedIndex = (int)settings.Isolation;
        _store = store;
        UpdateLabels();
        SaveError.Message = store.LoadError ?? "";
        SaveError.IsOpen = store.LoadError is not null;
    }

    /// <summary>沿用下载页 Tab 控件切换通用与下载分组。</summary>
    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (GeneralPanel is null || DownloadPanel is null) return;
        bool download = sender.SelectedItem?.Tag as string == "download";
        GeneralPanel.Visibility = download ? Visibility.Collapsed : Visibility.Visible;
        DownloadPanel.Visibility = download ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>源策略变化后保存，版本页下次打开时使用最新策略。</summary>
    private void Source_SelectionChanged(object sender, SelectionChangedEventArgs e) => Save();

    /// <summary>滑块变化时刷新单位及数值，并保存整数限制。</summary>
    private void Limit_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Save();

    /// <summary>显示并行文件上限、最大线程数和 MiB/s 速率；速率为零代表不限速。</summary>
    private void UpdateLabels()
    {
        ConnectionsLabel.Text = $"最大下载线程数：{(int)Connections.Value}";
        ParallelLabel.Text = $"并行下载文件数：{(int)ParallelDownloads.Value}";
        SpeedLabel.Text = Speed.Value == 0 ? "速率限制：不限速" : $"速率限制：{(int)Speed.Value} MiB/s";
    }

    /// <summary>保存全部下载与隔离偏好；失败时保留原配置、恢复并行滑块并显示未保存。</summary>
    private void Save()
    {
        if (_store is null) return;
        UpdateLabels();
        string? error = _store.Save(_store.Current with
        {
            FileSource = (SourceStrategy)FileSource.SelectedIndex,
            VersionSource = (SourceStrategy)VersionSource.SelectedIndex,
            MaxConnections = (int)Connections.Value,
            MaxParallelDownloads = (int)ParallelDownloads.Value,
            SpeedLimitMiB = (int)Speed.Value,
            Isolation = (IsolationMode)Isolation.SelectedIndex
        });
        if (error is not null)
        {
            var store = _store;
            _store = null;
            ParallelDownloads.Value = store.Current.MaxParallelDownloads;
            _store = store;
            UpdateLabels();
        }
        SaveError.Message = error ?? "";
        SaveError.IsOpen = error is not null;
    }
}
