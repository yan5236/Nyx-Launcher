using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>管理服务器卡片、启动实例选择及进入页面或手动刷新状态。</summary>
public sealed partial class ServersView : UserControl
{
    private sealed record Card(Border Container, TextBlock Description, TextBlock Latency, TextBlock Players, TextBlock Version,
        Image Icon, FontIcon Placeholder);

    /// <summary>跟踪当前列表已安排的地址，新增地址共享同一取消来源和四个并发名额。</summary>
    private sealed class ProbeRun
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly SemaphoreSlim Slots = new(4);
        internal readonly HashSet<string> Pending = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, ServerStatus?> _statuses = new(StringComparer.Ordinal);
    private readonly List<(Card Card, ServerEntry Entry)> _visibleCards = [];
    private ServerStore? _store;
    private GameFolderStore? _folders;
    private Func<ServerVersion, ServerEndpoint, Task>? _launch;
    private Func<string, CancellationToken, Task<ServerStatus?>> _query = QueryAsync;
    private ProbeRun? _probe;
    private bool _active;
    private bool _dialogBusy;
    private bool IsFeatured => Tabs.SelectedItem == FeaturedTab;

    /// <summary>初始化标签与卡片，窗口明暗切换时只重绘缓存简介的颜色，卸载时取消探测。</summary>
    public ServersView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var (card, entry) in _visibleCards)
            {
                bool attempted = _statuses.TryGetValue(entry.Address, out var status);
                UpdateDescription(card, entry.Address, attempted, status);
            }
        });
        Unloaded += (_, _) => _probe?.Cancellation.Cancel();
    }

    /// <summary>接入已登记游戏目录和共享启动流程，读取本机数据且不发起网络请求；验收入口可替换配置路径和探测。</summary>
    internal void Configure(GameFolderStore folders, Func<ServerVersion, ServerEndpoint, Task> launch, string? storePath = null,
        Func<string, CancellationToken, Task<ServerStatus?>>? query = null)
    {
        CloseEditor();
        _probe?.Cancellation.Cancel();
        _probe = null;
        _folders = folders;
        _launch = launch;
        if (query is not null) _query = query;
        _statuses.Clear();
        try { _store = new ServerStore(storePath); }
        catch (Exception ex) when (IsExpected(ex)) { _store = null; ShowError(ex.Message); }
        Render();
    }

    /// <summary>进入页面时查询尚无结果的地址；离开时取消未完成的探测并关闭表单，缓存保留到本次运行结束。</summary>
    internal void SetActive(bool active)
    {
        _active = active;
        if (!active)
        {
            CloseEditor();
            _probe?.Cancellation.Cancel();
            _probe = null;
            UpdateRefreshButton();
            return;
        }
        AutoRefresh();
    }

    /// <summary>切换列表时取消上一标签的探测，恢复滚动位置和缓存，再查询新列表尚无结果的地址。</summary>
    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Cards is null || _store is null) return;
        _probe?.Cancellation.Cancel();
        _probe = null;
        Feedback.IsOpen = false;
        Render();
        ServerScroll.ChangeView(null, 0, null, true);
        AutoRefresh();
    }

    /// <summary>按当前标签重建卡片并还原已有探测结果；精选页隐藏新增入口并显示第三方提示，空列表显示添加入口。</summary>
    private void Render()
    {
        AddButton.Visibility = IsFeatured ? Visibility.Collapsed : Visibility.Visible;
        Disclaimer.Visibility = IsFeatured ? Visibility.Visible : Visibility.Collapsed;
        Cards.Children.Clear();
        _visibleCards.Clear();
        var entries = (_store?.Entries(IsFeatured) ?? []).ToArray();
        foreach (var entry in entries)
        {
            var card = CreateCard(entry);
            Cards.Children.Add(card.Container);
            _visibleCards.Add((card, entry));
            UpdateCard(card, entry);
        }
        Empty.Visibility = _store is not null && entries.Length == 0 && !IsFeatured ? Visibility.Visible : Visibility.Collapsed;
        UpdateRefreshButton();
    }

    /// <summary>服务器页可见期间提交尚无缓存的地址，正在查询的地址继续沿用原请求。</summary>
    private void AutoRefresh()
    {
        if (_active) _ = RefreshAsync(force: false);
    }

    /// <summary>构造一张主题卡片及其分割启动按钮和菜单，精选项只提供修改入口。</summary>
    private Card CreateCard(ServerEntry entry)
    {
        var content = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var placeholder = new FontIcon { Glyph = "\uEC05", FontSize = 24 };
        var icon = new Image { Width = 44, Height = 44 };
        var iconArea = new Grid { Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Center };
        iconArea.Children.Add(placeholder);
        iconArea.Children.Add(icon);
        content.Children.Add(iconArea);

        var identity = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Text = entry.Name, FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TextBrush"]
        };
        ToolTipService.SetToolTip(name, entry.Name);
        var description = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["MutedBrush"]
        };
        identity.Children.Add(name);
        identity.Children.Add(description);
        Grid.SetColumn(identity, 1);
        content.Children.Add(identity);

        var stats = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 150 };
        var latency = AddMetric(stats, "\uEC3B", "延迟");
        var players = AddMetric(stats, "\uE716", "玩家人数");
        var version = AddMetric(stats, "\uE8F1", "服务器版本");        Grid.SetColumn(stats, 2);
        content.Children.Add(stats);

        var accent = (Brush)Application.Current.Resources["AccentButtonBackground"];
        var onAccent = (Brush)Application.Current.Resources["AccentButtonForeground"];
        var launch = new Button
        {
            Style = (Style)Application.Current.Resources["DeepButtonStyle"], Height = 32, Padding = new(10, 0, 10, 0),
            CornerRadius = new(4, 0, 0, 4), Background = accent, BorderBrush = accent, Foreground = onAccent
        };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(new SymbolIcon(Symbol.Play) { Foreground = onAccent });
        label.Children.Add(new TextBlock { Text = "启动", Foreground = onAccent });
        launch.Content = label;
        AutomationProperties.SetName(launch, $"启动 {entry.Name}");
        launch.Click += async (_, _) => await StartAsync(entry, false);
        var choose = new Button
        {
            Content = new FontIcon { Glyph = "\uE70D", FontSize = 12 },
            Style = (Style)Application.Current.Resources["DeepButtonStyle"],
            CornerRadius = new(0, 4, 4, 0), Padding = new(9, 0, 9, 0),
            Height = 32, Background = accent, BorderBrush = accent, Foreground = onAccent
        };
        AutomationProperties.SetName(choose, $"为 {entry.Name} 临时选择启动版本");
        ToolTipService.SetToolTip(choose, "临时选择版本");
        choose.Click += async (_, _) => await StartAsync(entry, true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        actions.Children.Add(launch);
        actions.Children.Add(choose);
        Grid.SetColumn(actions, 3);
        content.Children.Add(actions);
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["OpaqueMenuPresenterStyle"] };
        var edit = new MenuFlyoutItem { Text = "修改信息", Icon = new SymbolIcon(Symbol.Edit) };
        edit.Click += async (_, _) => await EditAsync(entry);
        menu.Items.Add(edit);
        if (!entry.Featured)
        {
            var delete = new MenuFlyoutItem { Text = "删除服务器", Icon = new SymbolIcon(Symbol.Delete) };
            delete.Click += async (_, _) => await DeleteAsync(entry);
            menu.Items.Add(delete);
        }
        var more = new Button { Content = new SymbolIcon(Symbol.More), Flyout = menu, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(more, $"{entry.Name}的操作");
        Grid.SetColumn(more, 4);
        content.Children.Add(more);

        var border = new Border { Style = (Style)Application.Current.Resources["CardStyle"], Child = content };
        border.SizeChanged += (_, args) =>
        {
            // 单行布局需要同时放下图标、简介、指标和两个按钮，窄于 620 时改为把指标移到简介下方占满整行。
            bool compact = args.NewSize.Width < 620;
            Grid.SetRow(stats, compact ? 1 : 0);
            Grid.SetColumn(stats, compact ? 1 : 2);
            Grid.SetColumnSpan(stats, compact ? 4 : 1);
            stats.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
            stats.Spacing = compact ? 10 : 4;
            // 代理服版本名（如 Waterfall 1.12.2-1.20.4）比数字长，宽卡片按卡片宽度给版本留出完整文本的宽度。
            double cap = args.NewSize.Width switch { >= 880 => 250, >= 620 => 200, _ => 150 };
            stats.MaxWidth = compact ? double.PositiveInfinity : cap;
            version.MaxWidth = compact ? double.PositiveInfinity : cap - 20;
            content.RowSpacing = compact ? 8 : 0;
            content.ColumnDefinitions[2].Width = compact ? new GridLength(0) : GridLength.Auto;
        };
        return new(border, description, latency, players, version, icon, placeholder);
    }

    /// <summary>向状态区域添加带图标的指标，并标注该值的用途供辅助技术读取。</summary>
    private static TextBlock AddMetric(StackPanel stats, string glyph, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        var value = new TextBlock { Text = "—", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 125 };
        AutomationProperties.SetName(value, label);
        ToolTipService.SetToolTip(row, label);
        row.Children.Add(value);
        stats.Children.Add(row);
        return value;
    }

    /// <summary>在原卡片更新带颜色的 MOTD、探测指标和图标；未知值保留占位，提示可查看完整简介和指标。</summary>
    private async void UpdateCard(Card card, ServerEntry entry)
    {
        bool attempted = _statuses.TryGetValue(entry.Address, out var status);
        UpdateDescription(card, entry.Address, attempted, status);
        card.Latency.Text = status?.Latency is long ms ? $"{ms} ms" : "—";
        card.Players.Text = status is { Players.Length: > 0 } ? status.Players : "—";
        card.Version.Text = status is { Version.Length: > 0 } ? status.Version : "—";
        SetMetricName(card.Latency, "延迟");
        SetMetricName(card.Players, "玩家人数");
        SetMetricName(card.Version, "服务器版本");
        card.Icon.Source = null;
        card.Placeholder.Visibility = Visibility.Visible;
        if (status?.Icon is not byte[] bytes) return;
        try
        {
            using var data = new MemoryStream(bytes, writable: false);
            var icon = new BitmapImage { DecodePixelWidth = 64 };
            await icon.SetSourceAsync(data.AsRandomAccessStream());
            if (!_statuses.TryGetValue(entry.Address, out var current) || current != status) return;
            card.Icon.Source = icon;
            card.Placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        { /* 无法解码的服务器图标使用占位图标。 */ }
    }

    /// <summary>同步指标的辅助技术名称，并把完整值写入所属行的提示，长版本被省略时也能读到全文。</summary>
    private static void SetMetricName(TextBlock value, string label)
    {
        AutomationProperties.SetName(value, $"{label} {value.Text}");
        if (value.Parent is StackPanel row) ToolTipService.SetToolTip(row, $"{label}：{value.Text}");
    }

    /// <summary>根据卡片底色绘制服务器彩字；仅调整低对比颜色，提示中可读到完整简介和地址。</summary>
    private static void UpdateDescription(Card card, string address, bool attempted, ServerStatus? status)
    {
        card.Description.Inlines.Clear();
        string plainText = status is null ? "" : string.Concat(status.Description.Select(span => span.Text));
        if (string.IsNullOrWhiteSpace(plainText))
        {
            card.Description.Inlines.Add(new Run { Text = attempted && status is null ? "无法获取状态" : address });
            ToolTipService.SetToolTip(card.Description, address);
            return;
        }
        var surface = ((SolidColorBrush)Application.Current.Resources["CardBrush"]).Color;
        uint backdrop = 0xFF000000 | (uint)(surface.R << 16 | surface.G << 8 | surface.B);
        foreach (var span in status!.Description)
        {
            var run = new Run { Text = span.Text };
            if (span.Color is uint rgb)
            {
                uint readable = BackgroundContrast.ReadableText(rgb, backdrop);
                run.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(readable >> 16), (byte)(readable >> 8), (byte)readable));
            }
            card.Description.Inlines.Add(run);
        }
        ToolTipService.SetToolTip(card.Description, new TextBlock { Text = address + "\n" + plainText, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
    }

    /// <summary>手动点击刷新时重新查询当前标签。</summary>
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(force: true);

    /// <summary>自动提交尚无结果的地址，手动刷新提交整个标签；查询最多并发四个，逐个写入结果，最后一组完成时释放本次资源。</summary>
    private async Task RefreshAsync(bool force)
    {
        if (_store is null || (force && _probe is not null)) return;
        var entries = _store.Entries(IsFeatured).Select(entry => entry.Address).Distinct()
            .Where(address => (force || !_statuses.ContainsKey(address)) && _probe?.Pending.Contains(address) != true).ToArray();
        if (entries.Length == 0) return;
        var probe = _probe ??= new ProbeRun();
        probe.Pending.UnionWith(entries);
        var token = probe.Cancellation.Token;
        UpdateRefreshButton();
        try
        {
            await Task.WhenAll(entries.Select(async address =>
            {
                await probe.Slots.WaitAsync(token);
                try
                {
                    var status = await ProbeAsync(address, token);
                    token.ThrowIfCancellationRequested();
                    _statuses[address] = status;
                    foreach (var (card, entry) in _visibleCards)
                        if (entry.Address == address) UpdateCard(card, entry);
                }
                finally { probe.Slots.Release(); }
            }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            probe.Pending.ExceptWith(entries);
            if (probe.Pending.Count == 0)
            {
                if (ReferenceEquals(_probe, probe)) _probe = null;
                probe.Slots.Dispose();
                probe.Cancellation.Dispose();
                UpdateRefreshButton();
            }
        }
    }

    /// <summary>按地址解析端口并查询真实服务器状态，作为界面默认的探测实现。</summary>
    private static async Task<ServerStatus?> QueryAsync(string address, CancellationToken token) =>
        await ServerStatusProbe.QueryAsync(ServerEndpoint.Parse(address), token);

    /// <summary>查询单个地址，格式错误、协议异常和连接超时均转为该卡片的不可用状态。</summary>
    private async Task<ServerStatus?> ProbeAsync(string address, CancellationToken token)
    {
        try { return await _query(address, token); }
        catch (Exception ex) when (!token.IsCancellationRequested &&
            (IsExpected(ex) || ex is System.Net.Sockets.SocketException or OperationCanceledException))
        { return null; }
    }

    /// <summary>探测期间禁用刷新按钮并显示进度，空列表无需发起刷新。</summary>
    private void UpdateRefreshButton()
    {
        RefreshButton.IsEnabled = _probe is null && Cards.Children.Count > 0;
        Refreshing.IsActive = _probe is not null;
        Refreshing.Visibility = Refreshing.IsActive ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>从工具栏或空状态按钮打开新增服务器弹窗。</summary>
    private async void Add_Click(object sender, RoutedEventArgs e) => await EditAsync(null);

    /// <summary>显示列表操作错误，保留现有条目和当前标签。</summary>
    private void ShowError(string message)
    {
        Feedback.Message = message;
        Feedback.IsOpen = true;
    }

    /// <summary>识别本机存储、输入与服务器数据错误，转换成界面反馈。</summary>
    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException;
}
