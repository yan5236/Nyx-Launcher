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
    private void Select(OfflineAccount account)
    {
        Error.IsOpen = false;
        Error.Severity = InfoBarSeverity.Error;
        try { _accounts!.Select(account); }
        catch (Exception ex) { Refresh(); Error.Message = ex.Message; Error.IsOpen = true; }
    }

    /// <summary>重新创建离线登录会话并选中该账户；成功明确提示离线身份已就绪，校验或保存失败显示原因。</summary>
    private void RetryLogin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OfflineAccount account }) return;
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
        if (sender is not Button { Tag: OfflineAccount account }) return;
        Error.IsOpen = false;
        Error.Severity = InfoBarSeverity.Error;
        try { _accounts!.Remove(account); }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
    }

    /// <summary>打开 WinUI 两步账户弹窗；模态期间禁用入口，关闭后恢复。</summary>
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        Error.IsOpen = false;
        Error.Severity = InfoBarSeverity.Error;
        AddButton.IsEnabled = false;
        try { await new AddAccountDialog(_accounts!) { XamlRoot = XamlRoot }.ShowAsync(); }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
        finally { AddButton.IsEnabled = true; }
    }
}

/// <summary>账户卡片的显示状态；选中标记属于界面，不写入身份模型或游戏参数。</summary>
public sealed class AccountRow(OfflineAccount account) : INotifyPropertyChanged
{
    public OfflineAccount Account { get; } = account;
    public string Name => Account.Name;
    public string Avatar => Account.Avatar;
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
