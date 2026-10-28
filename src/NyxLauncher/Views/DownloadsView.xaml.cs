using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NyxLauncher.Views;

/// <summary>展示下载分类标签、搜索与类型筛选，以及对应的静态版本列表。</summary>
public sealed partial class DownloadsView : UserControl
{
    private const string AllTypes = "全部类型";
    private IReadOnlyList<DownloadEntry> _entries = Array.Empty<DownloadEntry>();

    /// <summary>加载下载页原型；只展示本地演示数据，不获取远程清单或发起下载。</summary>
    public DownloadsView()
    {
        InitializeComponent();
        LoadCategory("game");
    }

    /// <summary>切换内容分类时重新绑定该分类的演示条目。</summary>
    private void ContentSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // XAML 加载阶段可能先触发选择变化，此时下方控件尚未连接，交给构造函数完成首次加载。
        if (SearchBox is null || TypeFilter is null || EntryList is null || EmptyHint is null)
        {
            return;
        }

        if (sender.SelectedItem?.Tag is string category)
        {
            LoadCategory(category);
        }
    }

    /// <summary>文本变化（含清除按钮）时即时过滤列表。</summary>
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ApplyFilter();
    }

    /// <summary>类型下拉变化时重新过滤列表。</summary>
    private void TypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilter();
    }

    /// <summary>切换分类并清空搜索、类型筛选，避免上一个分类的条件影响新列表。</summary>
    private void LoadCategory(string category)
    {
        _entries = DownloadCatalog.GetEntries(category);
        SearchBox.PlaceholderText = DownloadCatalog.GetSearchHint(category);
        SearchBox.Text = string.Empty;
        TypeFilter.SelectedIndex = 0;
        ApplyFilter();
    }

    /// <summary>按标题关键字与版本类型过滤演示条目，并更新空结果提示。</summary>
    private void ApplyFilter()
    {
        // XAML 加载时 ComboBox 的初始选中项可能先于列表控件创建而触发本方法。
        if (SearchBox is null || TypeFilter is null || EntryList is null || EmptyHint is null)
        {
            return;
        }

        string query = SearchBox.Text.Trim();
        string type = (TypeFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? AllTypes;
        List<DownloadEntry> filtered = _entries
            .Where(entry =>
                (type == AllTypes || entry.Kind == type) &&
                (query.Length == 0 || entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        EntryList.ItemsSource = filtered;
        EmptyHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
