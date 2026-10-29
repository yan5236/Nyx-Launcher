using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;
using System.ComponentModel;

namespace NyxLauncher.Views;

/// <summary>紧凑账户卡片直接切换身份，使用原生模态对话框添加账户。</summary>
public sealed partial class AccountsView : UserControl
{
    private AccountStore? _accounts;
    private bool _refreshing;
    private AccountRow[] _rows = [];
    private AddAccountDialog? _dialog;
    private bool _closing;

    /// <summary>初始化账户列表；持久化服务由主页面提供。</summary>
    public AccountsView() => InitializeComponent();

    /// <summary>绑定共享账户集合，订阅已保存的选择变化，并显示读取错误。</summary>
    public void Configure(AccountStore accounts)
    {
        _accounts = accounts;
        accounts.Changed += (_, _) => Refresh();
        Refresh();
        if (accounts.LoadError is string error) { Error.Message = error; Error.IsOpen = true; }
    }

    /// <summary>同步列表与左侧选中指示条，避免程序设置选择时再次写入配置。</summary>
    private void Refresh()
    {
        _refreshing = true;
        try
        {
            if (!_rows.Select(x => x.Account).SequenceEqual(_accounts!.Accounts))
            {
                _rows = _accounts.Accounts.Select(x => new AccountRow(x)).ToArray();
                AccountList.ItemsSource = _rows;
            }
            foreach (var row in _rows) row.SetSelected(row.Account == _accounts.Selected);
            AccountList.SelectedItem = _rows.SingleOrDefault(x => x.Account == _accounts.Selected);
            Empty.Visibility = _accounts.Accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AccountList.Visibility = _accounts.Accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _refreshing = false; }
    }

    /// <summary>键盘选择也立即保存；失败恢复原选中卡片并说明原因。</summary>
    private void Account_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && AccountList.SelectedItem is AccountRow row) Select(row.Account);
    }

    /// <summary>点击整张卡片即切换，支持鼠标与触摸，不设置额外切换按钮。</summary>
    private void Account_Click(object sender, ItemClickEventArgs e)
    { if (e.ClickedItem is AccountRow row) Select(row.Account); }

    /// <summary>集中处理选择写入异常；服务只有保存成功才会发布新身份。</summary>
    private void Select(GameAccount account)
    {
        Error.IsOpen = false;
        Error.Severity = InfoBarSeverity.Error;
        try { _accounts!.Select(account); }
        catch (Exception ex) { Refresh(); Error.Message = ex.Message; Error.IsOpen = true; }
    }

    /// <summary>离线账户重新校验，微软账户在可取消弹窗中重新授权；只接受与原游戏 UUID 一致的身份。</summary>
    private async void RetryLogin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameAccount account }) return;
        if (account.IsMicrosoft) { await ShowAccountDialogAsync(account); return; }
        try
        {
            _ = GameLaunchService.CreateSession(account);
            _accounts!.Select(account);
            Error.Severity = InfoBarSeverity.Success;
            Error.Message = $"离线账户 {account.Name} 已重新登录。";
        }
        catch (Exception ex) { Error.Severity = InfoBarSeverity.Error; Error.Message = ex.Message; }
        Error.IsOpen = true;
    }

    /// <summary>仅删除卡片对应的本地账户记录；共享存储同步选择与空状态，保存失败不改变列表。</summary>
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameAccount account }) return;
        Error.IsOpen = false;
        Error.Severity = InfoBarSeverity.Error;
        try { _accounts!.Remove(account); }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
    }

    /// <summary>打开 WinUI 两步账户弹窗；模态期间禁用入口，关闭后恢复。</summary>
    private async void Add_Click(object sender, RoutedEventArgs e)
        => await ShowAccountDialogAsync(null);

    /// <summary>共用添加和重登弹窗，防止重复打开；异常仍留在账户页，窗口关闭后不接受新的登录。</summary>
    private async Task ShowAccountDialogAsync(GameAccount? previous)
    {
        if (_closing || _dialog is not null) return;
        Error.IsOpen = false;
        Error.Severity = InfoBarSeverity.Error;
        AddButton.IsEnabled = false;
        try
        {
            _dialog = new AddAccountDialog(_accounts!, previous) { XamlRoot = XamlRoot };
            await _dialog.ShowAsync();
            await _dialog.StopAsync();
        }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
        finally { _dialog = null; AddButton.IsEnabled = !_closing; }
    }

    /// <summary>关闭启动器时取消账户交互并等待在途认证结束，避免保存迟到结果或遗留监听端口。</summary>
    public async Task StopAsync()
    { _closing = true; if (_dialog is not null) await _dialog.StopAsync(); }
}

/// <summary>账户卡片的显示状态；选中标记属于界面，不写入身份模型或游戏参数。</summary>
public sealed class AccountRow(GameAccount account) : INotifyPropertyChanged
{
    public GameAccount Account { get; } = account;
    public string Name => Account.Name;
    public string TypeLabel => Account.TypeLabel;
    public string RetryLabel => "重试登录 " + Name;
    public string DeleteLabel => "删除账户 " + Name;
    public Visibility SelectedVisibility { get; private set; } = Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>仅在保存成功后的选择变化时移动指示条，不重建卡片或重播列表动画。</summary>
    public void SetSelected(bool selected)
    {
        var visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        if (visibility == SelectedVisibility) return;
        SelectedVisibility = visibility;
        PropertyChanged?.Invoke(this, new(nameof(SelectedVisibility)));
    }

    /// <summary>向辅助技术提供可读名称和账户类型，不朗读 UUID 或资源路径。</summary>
    public override string ToString() => Name + "，" + TypeLabel;
}
