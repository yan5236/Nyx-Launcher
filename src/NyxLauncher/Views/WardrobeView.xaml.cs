using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

public sealed partial class WardrobeView : UserControl
{
    private AccountStore? _accounts;
    private readonly WardrobeStore _store = new();
    private CancellationTokenSource? _request;
    private bool _active, _binding, _busy, _loading, _applying;
    private WardrobeProfile? _profile;
    private byte[]? _currentSkin, _currentCape;
    private bool _currentSlim;
    private string? _currentSkinId;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Card? _selected;
    private readonly List<Card> _skinCards = [], _capeCards = [];

    /// <summary>衣柜卡片仅保存公开纹理与本地皮肤标识，不包含认证缓存。</summary>
    private sealed class Card : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public WardrobeSkin? Skin { get; init; }
        public string? CapeId { get; init; }
        public bool IsCape { get; init; }
        public byte[]? Png { get; init; }
        public bool Slim { get; init; }
        public bool Upload { get; init; }
        public WriteableBitmap? Thumbnail { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>通知名称与缩略图原地更新，不替换列表或清除选择。</summary>
        public void NotifyUpdated()
        {
            PropertyChanged?.Invoke(this, new(nameof(Name)));
            PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
        }
        public Visibility UploadVisibility => Upload ? Visibility.Visible : Visibility.Collapsed;
        public Visibility MenuVisibility => Skin is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>创建衣柜，所有账户与网络数据在首次激活时读取。</summary>
    public WardrobeView() { InitializeComponent(); _toastTimer.Tick += Toast_Elapsed; }

    /// <summary>注入与首页共享的账户存储；衣柜选择只影响衣柜，不修改下次游戏启动账户。</summary>
    public void Configure(AccountStore accounts) { _accounts = accounts; }

    /// <summary>进入时刷新微软身份，退出取消请求并释放纹理列表；后台任务不得覆盖下一次打开的页面。</summary>
    public void SetActive(bool active)
    {
        _active = active; Preview.SetActive(active);
        if (active) { BindAccounts(); _ = LoadAsync(); }
        else
        {
            _request?.Cancel(); _request = null; _busy = _loading = _applying = false;
            _profile = null; _selected = null; _currentSkin = _currentCape = null;
            _currentSkinId = null; _toastTimer.Stop(); SuccessToast.IsOpen = false;
            _skinCards.Clear(); _capeCards.Clear(); Cards.ItemsSource = null;
            Preview.SetTextures(null, false, null); UpdateEnabled();
        }
    }

    /// <summary>关闭窗口时取消衣柜网络操作并停止模型动画。</summary>
    public Task StopAsync() { SetActive(false); return Task.CompletedTask; }

    /// <summary>重新读取已登记微软账户并保留衣柜选择；无正版账户时展示唯一必要的限制提示。</summary>
    private void BindAccounts()
    {
        string? key = (AccountPicker.SelectedItem as GameAccount)?.Key ?? _accounts?.Selected?.Key;
        var accounts = _accounts?.Accounts.Where(a => a.IsMicrosoft).ToArray() ?? [];
        _binding = true;
        AccountPicker.ItemsSource = accounts;
        AccountPicker.SelectedItem = accounts.FirstOrDefault(a => a.Key == key) ?? accounts.FirstOrDefault();
        _binding = false;
        UpdateEnabled();
    }

    /// <summary>切换账户立即清除旧档案并重新请求，绝不沿用其他账户的披风或身份。</summary>
    private async void Account_Changed(object sender, SelectionChangedEventArgs e)
    { if (!_binding && _active) await LoadAsync(); }

    /// <summary>手动刷新同时重读账户列表，支持外部换肤或当前账户重新授权后的恢复。</summary>
    private async void Refresh_Click(object sender, RoutedEventArgs e) { BindAccounts(); await LoadAsync(); }

    /// <summary>静默认证后安全保存刷新缓存；身份已删除或被替换时由账户存储拒绝迟到结果。</summary>
    private async Task<(GameAccount Account, string Token)> AuthenticateAsync(GameAccount account, CancellationToken token)
    {
        var latest = _accounts!.Accounts.SingleOrDefault(a => a.Key == account.Key)
            ?? throw new InvalidOperationException("账户已删除，请重新选择。");
        var result = await MicrosoftLoginService.AuthenticateAsync(latest, false, null, token);
        token.ThrowIfCancellationRequested();
        _accounts.SaveMicrosoft(result.Account, latest, select: false);
        return (result.Account, result.Session.AccessToken!);
    }

    /// <summary>获取皮肤与披风并先去重保存当前皮肤，确保换肤后仍保留副本；保存失败不启用应用，取消或换账户的旧响应不发布。</summary>
    private async Task LoadAsync()
    {
        _request?.Cancel();
        using var request = new CancellationTokenSource(); _request = request;
        _profile = null; _selected = null; _currentSkin = _currentCape = null;
        _currentSkinId = null;
        _skinCards.Clear(); _capeCards.Clear(); Cards.ItemsSource = null; Preview.SetTextures(null, false, null);
        _busy = _loading = true; Status.IsOpen = false; UpdateEnabled();
        try
        {
            if (AccountPicker.SelectedItem is not GameAccount account)
            { ShowStatus("衣柜仅支持微软账户，请先在账户页添加微软账户。", InfoBarSeverity.Informational); return; }
            var auth = await AuthenticateAsync(account, request.Token);
            using var service = new WardrobeService();
            var profile = await service.GetProfileAsync(auth.Account, auth.Token, request.Token);
            bool slim = profile.Skin?.Slim ?? account.IsAlex;
            byte[] skin = profile.Skin is { } texture ? await service.DownloadAsync(texture, false, request.Token)
                : await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", slim ? "alex.png" : "steve.png"), request.Token);
            var capes = new List<(WardrobeTexture Texture, byte[] Png)>();
            foreach (var cape in profile.Capes)
                capes.Add((cape, await service.DownloadAsync(cape, true, request.Token)));
            request.Token.ThrowIfCancellationRequested();
            if (_request != request || !_active) return;
            _currentSkinId = _store.Add(auth.Account.Name + "的皮肤", slim, skin).Id;
            _profile = profile; _currentSkin = skin; _currentSlim = slim;
            _currentCape = capes.FirstOrDefault(c => c.Texture.Active).Png;
            _capeCards.Add(new Card { Name = "不使用披风", IsCape = true, Thumbnail = SkinPreview.Thumbnail(skin, slim, back: true) });
            foreach (var (cape, png) in capes)
                _capeCards.Add(new Card { Name = cape.Name, IsCape = true, CapeId = cape.Id, Png = png, Thumbnail = SkinPreview.Thumbnail(skin, slim, png, true) });
            RebuildSkins(); FilterCards();
            Preview.SetTextures(skin, slim, _currentCape);
            Status.IsOpen = false;
            if (_store.LoadError is not null) ShowStatus(_store.LoadError);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_request == request) ShowStatus(ex.Message); }
        finally { if (_request == request) { _request = null; _busy = _loading = false; UpdateEnabled(); } }
    }

    /// <summary>从已保存副本生成唯一皮肤卡片，当前穿戴排在首位并标记；自定义名称不被刷新覆盖，损坏单项仍可管理。</summary>
    private void RebuildSkins()
    {
        _skinCards.Clear();
        foreach (var skin in _store.Skins.OrderByDescending(s => s.Id == _currentSkinId))
        {
            try
            {
                byte[] png = _store.Read(skin);
                _skinCards.Add(new Card { Name = skin.Name + (skin.Id == _currentSkinId ? "（当前）" : ""), Skin = skin, Png = png, Slim = skin.Slim, Thumbnail = SkinPreview.Thumbnail(png, skin.Slim) });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { _skinCards.Add(new Card { Name = skin.Name + "（无法读取）", Skin = skin }); }
        }
        _skinCards.Add(new Card { Name = "上传新皮肤", Upload = true });
    }

    /// <summary>搜索按显示名称即时筛选，上传入口始终保留；重新绑定会清除过期选择。</summary>
    private void FilterCards()
    {
        if (Cards is null) return;
        _selected = null;
        Cards.SelectedItem = null;
        var source = CategoryTabs.SelectedItem == CapesTab ? _capeCards : _skinCards;
        Cards.ItemsSource = source.Where(c => c.Upload || c.Name.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        Preview.SetTextures(_currentSkin, _currentSlim, _currentCape); UpdateEnabled();
    }

    /// <summary>使用 SelectorBar 已提交的选中项切换列表并播放右侧正文转场；子项 IsSelected 此时可能尚未同步，不据此判断分类。</summary>
    private void Tab_Changed(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        if (Cards is null) return;
        FilterCards();
        TabBody.PlayTransition();
    }

    /// <summary>仅筛选内存中的卡片，不请求网络或写入文件。</summary>
    private void Search_Changed(object sender, TextChangedEventArgs e) => FilterCards();

    /// <summary>单击有效分类卡片只改变预览，按卡片类型选择纹理槽；拒绝切换过程中迟到的另一分类选择。</summary>
    private void Card_Changed(object sender, SelectionChangedEventArgs e)
    {
        _selected = Cards.SelectedItem as Card;
        if (_selected is { } selected && selected.IsCape != (CategoryTabs.SelectedItem == CapesTab)) _selected = null;
        if (_selected is { Upload: false } card)
        {
            if (card.IsCape) Preview.SetTextures(_currentSkin, _currentSlim, card.Png);
            else Preview.SetTextures(card.Png, card.Slim, _currentCape);
        }
        UpdateEnabled();
    }

    /// <summary>统一启用操作；仅加载时隐藏列表，应用时保留页面并锁定选择，按钮显示应用中。</summary>
    private void UpdateEnabled()
    {
        if (ApplyButton is null) return;
        LoadingPanel.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = _loading;
        Cards.Visibility = _loading ? Visibility.Collapsed : Visibility.Visible;
        ApplyButton.Content = _applying ? "应用中" : "应用";
        AccountPicker.IsEnabled = CategoryTabs.IsEnabled = Search.IsEnabled = !_applying;
        bool account = AccountPicker.SelectedItem is GameAccount { IsMicrosoft: true };
        UploadButton.IsEnabled = account && !_busy && _store.LoadError is null;
        RefreshButton.IsEnabled = !_busy;
        Cards.IsEnabled = account && !_busy;
        ApplyButton.IsEnabled = account && !_busy && _profile is not null && _selected is { Upload: false } &&
            _selected.IsCape == (CategoryTabs.SelectedItem == CapesTab) && (_selected.IsCape || _selected.Png is not null);
    }

    /// <summary>固定账户与卡片执行官方写请求；成功原地更新穿戴和头像，失败保留选择，取消或离页后的响应不发布。</summary>
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected is not { Upload: false } card || _profile is null || AccountPicker.SelectedItem is not GameAccount account) return;
        bool cape = card.IsCape;
        if (cape != (CategoryTabs.SelectedItem == CapesTab)) return;
        using var request = new CancellationTokenSource(); _request = request;
        _busy = _applying = true; Status.IsOpen = false; UpdateEnabled();
        try
        {
            var auth = await AuthenticateAsync(account, request.Token);
            using var service = new WardrobeService();
            if (cape)
            {
                var profile = await service.GetProfileAsync(auth.Account, auth.Token, request.Token);
                await service.ApplyCapeAsync(auth.Account, auth.Token, profile, card.CapeId, request.Token);
            }
            else await service.ApplySkinAsync(auth.Account, auth.Token, card.Png!, card.Slim, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (_request != request || !_active) return;
            AcceptAppliedCard(card);
            if (!cape) AccountAvatar.UpdateMicrosoft(account.Uuid, card.Png!);
            ShowStatus(cape ? "披风已应用。" : "皮肤已应用。", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_request == request) ShowStatus(ex.Message); }
        finally { if (_request == request) { _request = null; _busy = _applying = false; UpdateEnabled(); } }
    }

    /// <summary>记录成功穿戴并更新卡片名称和披风缩略图；保留列表、搜索、选择和模型视角，不重新请求档案。</summary>
    private void AcceptAppliedCard(Card card)
    {
        if (card.IsCape) _currentCape = card.Png;
        else
        {
            _currentSkin = card.Png; _currentSlim = card.Slim; _currentSkinId = card.Skin!.Id;
            foreach (var skinCard in _skinCards.Where(c => c.Skin is not null))
            {
                skinCard.Name = skinCard.Skin!.Name + (skinCard.Skin.Id == _currentSkinId ? "（当前）" : "") +
                    (skinCard.Png is null ? "（无法读取）" : "");
                skinCard.NotifyUpdated();
            }
            foreach (var capeCard in _capeCards)
            {
                capeCard.Thumbnail = SkinPreview.Thumbnail(_currentSkin!, _currentSlim, capeCard.Png, true);
                capeCard.NotifyUpdated();
            }
        }
    }

    /// <summary>成功消息在右下角展示三秒 Toast，连续成功重新计时；错误仍保留底部提示，不显示认证正文或令牌。</summary>
    private void ShowStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        if (severity == InfoBarSeverity.Success)
        {
            SuccessToast.Message = message; SuccessToast.IsOpen = true;
            _toastTimer.Stop(); _toastTimer.Start();
            return;
        }
        Status.Message = message; Status.Severity = severity; Status.IsOpen = true;
    }

    /// <summary>成功提示到期自动关闭并停止计时器，避免页面长期持有空闲回调。</summary>
    private void Toast_Elapsed(object? sender, object e) { _toastTimer.Stop(); SuccessToast.IsOpen = false; }

    /// <summary>切换人物走动并同步按钮文字。</summary>
    private void Pause_Click(object sender, RoutedEventArgs e) => PauseButton.Content = Preview.TogglePause() ? "继续" : "暂停";
    /// <summary>按固定倍率放大预览，实际范围由模型控件限制。</summary>
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Preview.Zoom(1.15f);
    /// <summary>按固定倍率缩小预览，实际范围由模型控件限制。</summary>
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Preview.Zoom(1 / 1.15f);
    /// <summary>恢复人物自身右前方视角与默认缩放。</summary>
    private void Reset_Click(object sender, RoutedEventArgs e) => Preview.ResetCamera();
}
