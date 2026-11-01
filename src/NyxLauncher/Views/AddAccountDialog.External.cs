using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

public sealed record ExternalProfileRow(string Id, string Name, string ServerUrl);

public sealed partial class AddAccountDialog
{
    private readonly ExternalServerStore _servers = new();
    private ExternalServer? _server;
    private ExternalServer? _editingServer;
    private ExternalTokens? _externalTokens;
    private string _externalStep = "servers";

    /// <summary>进入服务器目录，默认 LittleSkin 可直接进入凭据输入；区域限制期间不进入。</summary>
    private void External_Click(object sender, RoutedEventArgs e) { if (_restricted) return; ShowServers(); }

    /// <summary>切换外置登录步骤和底部操作，并沿用其他登录步骤的平移淡入；返回使用相反方向。</summary>
    private void ShowExternalStep(string step, bool forward = true)
    {
        _externalStep = step;
        Types.Visibility = AccountName.Visibility = Visibility.Collapsed;
        ExternalPanel.Visibility = Visibility.Visible;
        BackButton.Visibility = _previous is null ? Visibility.Visible : Visibility.Collapsed;
        ServerHeader.Visibility = ServerList.Visibility = step == "servers" ? Visibility.Visible : Visibility.Collapsed;
        CreateServerPanel.Visibility = step == "create" ? Visibility.Visible : Visibility.Collapsed;
        CredentialsPanel.Visibility = step == "credentials" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPanel.Visibility = step == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        SecondaryButtonText = step switch { "create" => "确定", "credentials" => "登录", "profiles" => "完成登录", _ => "" };
        DefaultButton = step == "servers" ? ContentDialogButton.None : ContentDialogButton.Secondary;
        DialogTitle.Text = step switch { "create" => _editingServer is null ? "创建认证服务器" : "编辑认证服务器", "profiles" => "选择角色", _ => "外置登录" };
        Error.IsOpen = false;
        AnimateStep(forward);
    }

    /// <summary>重置临时认证状态并显示最新目录；可向后返回此步骤而不关闭弹窗。</summary>
    private void ShowServers(bool forward = true)
    {
        ExternalPassword.Password = ""; _externalTokens = null;
        ServerList.ItemsSource = _servers.Servers;
        ShowExternalStep("servers", forward);
        if (_servers.LoadError is { } error) { Error.Message = error; Error.IsOpen = true; }
    }

    /// <summary>按当前步骤返回服务器目录或凭据输入；从目录返回时交给原有账户类型导航。</summary>
    private bool BackExternal()
    {
        if (_externalStep == "servers") return false;
        if (_externalStep == "profiles") { _externalTokens = null; ShowExternalStep("credentials", false); }
        else ShowServers(false);
        return true;
    }

    /// <summary>将底部取消右侧的确认按钮分派到创建、登录或选角操作，不在内容区重复放置提交按钮。</summary>
    private void ConfirmExternal()
    {
        if (_externalStep == "create") SaveServer();
        else if (_externalStep == "credentials") LoginExternal();
        else if (_externalStep == "profiles") FinishExternal();
    }

    /// <summary>新建服务器时清空输入，用户点击底部确定后才验证和保存。</summary>
    private void CreateServer_Click(object sender, RoutedEventArgs e)
    {
        _editingServer = null; ServerUrl.Text = ServerName.Text = "";
        ShowExternalStep("create"); ServerUrl.Focus(FocusState.Programmatic);
    }

    /// <summary>载入所选目录项供编辑；保留已有账户的认证边界，不迁移其令牌。</summary>
    private void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ExternalServer server }) return;
        _editingServer = server; ServerName.Text = server.Name; ServerUrl.Text = server.Url;
        ShowExternalStep("create");
    }

    /// <summary>删除服务器目录项后刷新列表；错误留在原页面，默认服务器也可删除且重启不复活。</summary>
    private void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ExternalServer server }) return;
        try { _servers.Remove(server); ServerList.ItemsSource = _servers.Servers; }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
    }

    /// <summary>验证真实 Yggdrasil 地址后新增或修改目录，使用可选显示名称；失败保持输入。</summary>
    private void SaveServer() => StartExternal(async token =>
    {
        using var login = new ExternalLoginService();
        var found = await login.DiscoverAsync(ServerUrl.Text, token);
        token.ThrowIfCancellationRequested();
        if (_closed) return;
        var server = string.IsNullOrWhiteSpace(ServerName.Text) ? found.Server : found.Server with { Name = ServerName.Text.Trim() };
        if (_editingServer is null) _servers.Add(server); else _servers.Update(_editingServer, server);
        ShowServers(false);
    });

    /// <summary>点击目录卡片后显示账户密码，编辑或删除按钮独立处理。</summary>
    private void Server_Click(object sender, ItemClickEventArgs e)
    { if (_externalStep == "servers" && e.ClickedItem is ExternalServer server && _servers.Servers.Contains(server)) ShowCredentials(server); }

    /// <summary>展示认证地址并锁定重登账户标识，凭据从不沿用到另一个站点。</summary>
    private void ShowCredentials(ExternalServer server)
    {
        _server = server; _externalTokens = null; ExternalPassword.Password = "";
        ServerLabel.Text = server.Name + "\n" + server.Url;
        ExternalUsername.Text = _previous?.ExternalUsername ?? "";
        ExternalUsername.IsReadOnly = _previous is not null;
        ShowExternalStep("credentials"); ExternalUsername.Focus(FocusState.Programmatic);
    }

    /// <summary>提交一次认证后清除密码，以头像和名称展示角色；重登只允许原 UUID，关闭后拒绝迟到结果。</summary>
    private void LoginExternal() => StartExternal(async token =>
    {
        using var login = new ExternalLoginService();
        string password = ExternalPassword.Password; ExternalPassword.Password = "";
        var result = await login.LoginAsync(_server!, ExternalUsername.Text.Trim(), password, token);
        token.ThrowIfCancellationRequested();
        if (_closed) return;
        var profiles = result.SelectedProfile is { } selected ? new[] { selected } : result.AvailableProfiles;
        if (_previous is not null) profiles = profiles.Where(p => string.Equals(p.Id, _previous.Uuid, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (profiles.Length == 0) throw new InvalidOperationException("原角色已不可用，请检查皮肤站角色设置。");
        _externalTokens = result;
        ProfileList.ItemsSource = profiles.Select(p => new ExternalProfileRow(p.Id.ToLowerInvariant(), p.Name, _server!.Url)).ToArray();
        ProfileList.SelectedIndex = 0;
        ShowExternalStep("profiles");
    });

    /// <summary>绑定所选角色并保存加密令牌；保存失败保留已绑定结果，避免重复绑定导致协议错误。</summary>
    private void FinishExternal() => StartExternal(async token =>
    {
        if (ProfileList.SelectedItem is not ExternalProfileRow row) throw new InvalidOperationException("请选择角色。");
        var profile = _externalTokens!.SelectedProfile ?? _externalTokens.AvailableProfiles.Single(p => p.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase));
        using var login = new ExternalLoginService();
        var result = await login.SelectAsync(_server!, _externalTokens, profile, token);
        _externalTokens = result;
        token.ThrowIfCancellationRequested();
        if (_closed) return;
        _accounts.SaveExternal(ExternalLoginService.CreateAccount(_server!, ExternalUsername.Text.Trim(), result), _previous);
        _externalTokens = null; Hide();
    });

    /// <summary>串行运行认证动作并复用窗口关闭取消机制，重复提交不启动新请求。</summary>
    private void StartExternal(Func<CancellationToken, Task> action)
    { if (_loginCancellation is null && !_closed) _loginTask = RunExternalAsync(action); }

    /// <summary>网络期间禁用内容和底部确认，取消按钮保持可用；错误恢复当前步骤，不记录凭据。</summary>
    private async Task RunExternalAsync(Func<CancellationToken, Task> action)
    {
        using var cancellation = new CancellationTokenSource(); _loginCancellation = cancellation;
        ExternalPanel.IsEnabled = BackButton.IsEnabled = IsSecondaryButtonEnabled = false; Error.IsOpen = false;
        ExternalRing.Visibility = Visibility.Visible; ExternalRing.IsActive = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { if (!_closed) { Error.Message = "认证请求已取消或超时，请重试。"; Error.IsOpen = true; } }
        catch (Exception ex) { if (!_closed) { Error.Message = ex.Message; Error.IsOpen = true; } }
        finally
        {
            _loginCancellation = null; ExternalPanel.IsEnabled = BackButton.IsEnabled = IsSecondaryButtonEnabled = true;
            ExternalRing.Visibility = Visibility.Collapsed; ExternalRing.IsActive = false;
        }
    }

    /// <summary>验证标准卡片并去重导入后进入登录；区域限制、重登或忙碌时不打断当前认证。</summary>
    public void ImportServer(string text)
    {
        if (_restricted || _loginCancellation is not null || _previous is not null || _closed) return;
        ShowServers();
        StartExternal(async token =>
        {
            using var login = new ExternalLoginService();
            var found = await login.DiscoverAsync(text, token);
            token.ThrowIfCancellationRequested();
            if (!_closed) ShowCredentials(_servers.Add(found.Server));
        });
    }

    /// <summary>浏览器标准文本拖拽使用复制语义；区域限制、忙碌或重登时不接受导入。</summary>
    private void External_DragOver(object sender, DragEventArgs e)
    { e.Handled = true; if (!_restricted && _loginCancellation is null && _previous is null && e.DataView.Contains(StandardDataFormats.Text)) e.AcceptedOperation = DataPackageOperation.Copy; }

    /// <summary>仅在读取文本期间持有系统拖放延迟，读取后立即释放再导入；区域限制或异常时同样结束拖放，避免浮标残留。</summary>
    private async void External_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_restricted) return;
        try
        {
            string text;
            var deferral = e.GetDeferral();
            try
            {
                if (!e.DataView.Contains(StandardDataFormats.Text)) return;
                text = await e.DataView.GetTextAsync();
            }
            finally { deferral.Complete(); }
            if (text.StartsWith("authlib-injector:yggdrasil-server:", StringComparison.OrdinalIgnoreCase)) ImportServer(text);
        }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
    }
}
