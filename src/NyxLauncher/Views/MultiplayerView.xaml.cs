using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using NyxLauncher.Services;
using Windows.ApplicationModel.DataTransfer;

namespace NyxLauncher.Views;

/// <summary>显示联机首页和房间详情，并持有可跨页面继续运行的陶瓦会话。</summary>
public sealed partial class MultiplayerView : UserControl
{
    private static readonly string PathFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NyxLauncher", "terracotta-path.txt");
    private readonly TerracottaSession _session = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Func<bool> _gameRunning = () => false;
    private Func<string?> _playerName = () => null;
    private string? _executable;
    private (string State, string Value, string[] Players) _room = ("waiting", "", []);
    private string _guestInvite = "";
    private bool _host;
    private bool _detail;
    private bool _prompted;
    private Task _work = Task.CompletedTask;
    private Task _poll = Task.CompletedTask;
    private bool _enabled;
    private bool _busy;
    private bool _closing;
    private bool _active;
    private int _generation;

    /// <summary>安装或切换联机会话时，通知主窗口延后后台堆压缩。</summary>
    public bool IsBusy => _busy;

    internal event Action? ModuleSettingsChanged;
    internal string? ModulePath => File.Exists(_executable) ? _executable : null;
    internal bool CanChangeModule => !_busy && !_enabled && !_closing;

    /// <summary>创建联机页并连接房间状态刷新，不启动外部程序。</summary>
    public MultiplayerView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            if ((_active || _enabled) && _poll.IsCompleted && !_busy && !_closing) _poll = RefreshAsync();
        };
    }

    /// <summary>读取之前选择的模块，并接收游戏运行状态与所选账户的玩家名。</summary>
    public void Configure(Func<bool> gameRunning, Func<string?> playerName)
    {
        _gameRunning = gameRunning;
        _playerName = playerName;
        try { if (File.Exists(PathFile)) _executable = File.ReadAllText(PathFile).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowError(Feedback, e); }
        Render();
        _timer.Start();
    }

    /// <summary>记录联机页是否可见；首次进入且缺少模块时提示下载，离开页面保留房间。</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (!active) return;
        if (!_busy && !_closing && _poll.IsCompleted) _poll = RefreshAsync();
        if (!_prompted && ModulePath is null) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, PromptForModule);
    }

    /// <summary>弹窗提示首次下载；暂缓下载后仍可通过通用设置安装或选择模块。</summary>
    private async void PromptForModule()
    {
        if (!_active || _prompted || _closing || _busy || ModulePath is not null || XamlRoot is not { } root) return;
        _prompted = true;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "下载联机模块",
            Content = "使用联机需要安装陶瓦。你也可以前往设置 → 通用，下载或选择已安装的模块。",
            PrimaryButtonText = "选择目录并下载",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && _active && !_closing)
            await DownloadAsync(root, Feedback);
    }

    /// <summary>选择目录后下载并校验陶瓦，成功后保存路径；取消时保留原选择。</summary>
    internal Task DownloadAsync(XamlRoot root, InfoBar feedback) => !CanChangeModule ? Task.CompletedTask : RunAsync(async token =>
    {
        var folder = await new FolderPicker(root.ContentIslandEnvironment.AppWindowId)
            { CommitButtonText = "下载到此目录" }.PickSingleFolderAsync().AsTask(token);
        token.ThrowIfCancellationRequested();
        if (folder is null) return;
        feedback.Severity = InfoBarSeverity.Informational;
        feedback.Message = "正在下载并校验陶瓦联机…";
        feedback.IsOpen = true;
        Remember(await TerracottaSession.InstallAsync(folder.Path, token));
        feedback.IsOpen = false;
    }, feedback);

    /// <summary>选择已安装的陶瓦 EXE，取消时保留原选择，启用时再验证兼容性。</summary>
    internal Task SelectAsync(XamlRoot root, InfoBar feedback) => !CanChangeModule ? Task.CompletedTask : RunAsync(async token =>
    {
        var picker = new FileOpenPicker(root.ContentIslandEnvironment.AppWindowId) { CommitButtonText = "选择陶瓦 EXE" };
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync().AsTask(token);
        token.ThrowIfCancellationRequested();
        if (file is not null) Remember(file.Path);
    }, feedback);

    /// <summary>通过系统保存对话框导出当前或最近一次陶瓦日志；取消时不写文件。</summary>
    internal Task ExportLogAsync(XamlRoot root, InfoBar feedback) => RunAsync(async token =>
    {
        using var log = _session.OpenLog() ?? throw new IOException("尚无可导出的陶瓦日志，请先启用联机模块。");
        var picker = new FileSavePicker(root.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedFileName = "Terracotta-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            DefaultFileExtension = ".log"
        };
        picker.FileTypeChoices.Add("陶瓦日志", [".log"]);
        var file = await picker.PickSaveFileAsync().AsTask(token);
        token.ThrowIfCancellationRequested();
        if (file is null) return;
        string temporary = file.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = File.Create(temporary)) await log.CopyToAsync(output, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, file.Path, overwrite: true);
        }
        finally { File.Delete(temporary); }
        feedback.Severity = InfoBarSeverity.Success;
        feedback.Message = "联机日志已导出，可能包含网络地址或邀请码。";
        feedback.IsOpen = true;
    }, feedback);

    /// <summary>记住所选可执行文件路径，持久化失败时仍允许本次使用。</summary>
    private void Remember(string executable)
    {
        _executable = executable;
        Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
        string temporary = PathFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, executable);
            File.Move(temporary, PathFile, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>启动或停止陶瓦；活动房间须先在房间详情中关闭或退出。</summary>
    private async void Enabled_Toggled(object sender, RoutedEventArgs args)
    {
        if (_busy || _closing || EnabledSwitch.IsOn == _enabled) return;
        if (_room.State != "waiting")
        {
            EnabledSwitch.IsOn = _enabled;
            return;
        }
        bool requested = EnabledSwitch.IsOn;
        await RunAsync(async token =>
        {
            if (requested) await _session.StartAsync(_executable!, token);
            else await _session.StopAsync();
            _enabled = requested;
        });
    }

    /// <summary>开始扫描本机局域网世界，随后显示房主详情并等待邀请码。</summary>
    private async void Host_Click(object sender, RoutedEventArgs args)
    {
        await RunAsync(async token =>
        {
            await _session.HostAsync(_playerName(), token);
            _room = ("host-scanning", "", []);
            _host = true;
            ShowRoomDetail(true);
        });
    }

    /// <summary>弹窗收集非空邀请码；取消留在首页，陶瓦接受后显示访客详情。</summary>
    private async void Join_Click(object sender, RoutedEventArgs args)
    {
        var input = new TextBox { Header = "邀请码", PlaceholderText = "粘贴房主分享的邀请码", MaxLength = 100 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "加入房间",
            Content = input,
            PrimaryButtonText = "加入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !_active || !_enabled || !_gameRunning()) return;
        await RunAsync(async token =>
        {
            var code = input.Text.Trim();
            await _session.JoinAsync(code, _playerName(), token);
            _guestInvite = code;
            _room = ("guest-connecting", "", []);
            ShowRoomDetail(true);
        });
    }

    /// <summary>返回联机首页，保持陶瓦进程及房间继续运行。</summary>
    private void Back_Click(object sender, RoutedEventArgs args) => ShowRoomDetail(false);

    /// <summary>重新查看当前房间，无需重新创建房间或连接。</summary>
    private void Resume_Click(object sender, RoutedEventArgs args) => ShowRoomDetail(true);

    /// <summary>切换首页和房间详情的显示及焦点，不发送任何陶瓦控制指令。</summary>
    private void ShowRoomDetail(bool show)
    {
        _detail = show;
        Render();
        MultiplayerScroll.ChangeView(null, 0, null, true);
        if (show) BackButton.Focus(FocusState.Programmatic);
        else if (ActiveRoom.ActionButton is Button resume) resume.Focus(FocusState.Programmatic);
    }

    /// <summary>窄窗口采用上下布局，避免挤压玩家列表或复制按钮。</summary>
    private void RoomColumns_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool narrow = args.NewSize.Width < 530;
        RoomColumns.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(RoomInfo, narrow ? 0 : 1);
        Grid.SetRow(RoomInfo, narrow ? 1 : 0);
    }

    /// <summary>关闭房间或退出访客会话；陶瓦确认后清除详情并回到首页。</summary>
    private async void Leave_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        await _session.LeaveAsync(token);
        ClearRoom();
        ShowRoomDetail(false);
    });

    /// <summary>复制当前房间的邀请码或访客直连地址，失败时在页面显示原因。</summary>
    private void Copy_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(ReferenceEquals(sender, CopyAddressButton) ? GuestAddress.Text : ConnectionValue.Text);
            Clipboard.SetContent(data);
        }
        catch (Exception e) { ShowError(Feedback, e); }
    }

    /// <summary>串行执行联机与模块管理操作，阻止在途的旧状态查询覆盖操作结果。</summary>
    private Task RunAsync(Func<CancellationToken, Task> action, InfoBar? feedback = null)
    {
        if (_busy || _closing) return Task.CompletedTask;
        _busy = true;
        _generation++;
        feedback ??= Feedback;
        feedback.IsOpen = false;
        return _work = ExecuteAsync(action, feedback);
    }

    /// <summary>执行操作并向发起页面报告失败，结束时同步开关和管理按钮。</summary>
    private async Task ExecuteAsync(Func<CancellationToken, Task> action, InfoBar feedback)
    {
        try
        {
            Render();
            ModuleSettingsChanged?.Invoke();
            await action(_shutdown.Token);
        }
        catch (Exception e)
        {
            if (!_closing) ShowError(feedback, e);
        }
        finally
        {
            EnabledSwitch.IsOn = _enabled;
            _busy = false;
            if (!_closing)
            {
                Render();
                ModuleSettingsChanged?.Invoke();
            }
        }
    }

    /// <summary>查询陶瓦房间和成员状态；游戏退出时结束房间，页面导航期间仍继续刷新。</summary>
    private async Task RefreshAsync()
    {
        int generation = _generation;
        try
        {
            if (_enabled)
            {
                var room = await _session.ReadStateAsync(_shutdown.Token);
                if (_closing || generation != _generation) return;
                if (!_gameRunning() && room.State != "waiting")
                {
                    await RunAsync(async token =>
                    {
                        await _session.LeaveAsync(token);
                        ClearRoom();
                    });
                    return;
                }

                if (room.State == "waiting" && _room.State != "waiting") ClearRoom();
                if (!_room.Players.SequenceEqual(room.Players)) Players.ItemsSource = room.Players;
                _room = room;
            }
            Render();
        }
        catch (Exception e)
        {
            if (_closing || generation != _generation) return;
            if (_enabled && !_session.IsRunning)
            {
                await RunAsync(async _ =>
                {
                    await _session.StopAsync();
                    _enabled = false;
                    ClearRoom();
                    ShowError(Feedback, e);
                });
                return;
            }
            ShowError(Feedback, e);
            Render();
        }
    }

    /// <summary>根据实际会话状态刷新首页提示、角色详情及各个操作的可用性。</summary>
    private void Render()
    {
        bool installed = ModulePath is not null;
        bool waiting = _room.State == "waiting";
        EnabledSwitch.IsEnabled = !_busy && waiting && (installed || _enabled);
        HostButton.IsEnabled = JoinButton.IsEnabled = _enabled && _gameRunning() && waiting && !_busy;
        ActiveRoom.IsOpen = !waiting && !_detail;
        ActiveRoom.Message = _room.State == "exception" ? "联机连接已断开"
            : _host ? "当前仍有房间正在开启" : "当前仍在访客联机中";
        Overview.Visibility = _detail ? Visibility.Collapsed : Visibility.Visible;
        RoomDetail.Visibility = _detail ? Visibility.Visible : Visibility.Collapsed;
        OverviewStatus.Text = !installed && !_enabled ? "可在设置 → 通用中下载或选择联机模块。"
            : !_enabled ? "请先启用联机模块。"
            : waiting && !_gameRunning() ? "请先从首页启动 Minecraft Java 版。" : "";
        OverviewStatus.Visibility = OverviewStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RoomTitle.Text = _host ? "房主房间" : "访客房间";
        LeaveButton.IsEnabled = !_busy;
        LeaveButton.Content = _host ? "关闭房间" : "退出联机";
        PlayersHint.Text = _room.State == "exception" ? "连接已断开。" : "等待房间成员信息…";
        PlayersHint.Visibility = _room.Players.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        string invite = _host ? (_room.State == "host-ok" ? _room.Value : "") : _guestInvite;
        if (ConnectionValue.Text != invite) ConnectionValue.Text = invite;
        CopyButton.IsEnabled = invite.Length > 0;
        string address = _room.State == "guest-ok" ? _room.Value : "";
        if (GuestAddress.Text != address) GuestAddress.Text = address;
        CopyAddressButton.Visibility = GuestAddress.Visibility = address.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RoleTutorial.Text = _host
            ? "房主教程\n从首页启动 Minecraft Java 版并进入单人世界，按 Esc，选择「对局域网开放」并启动。陶瓦会自动共享它发现的第一个世界，请只开放想共享的世界。把邀请码发给朋友。关闭房间会断开朋友的连接。"
            : "访客教程\n请启动与房主版本和模组一致的 Minecraft Java 版。连接成功后，复制游戏直连地址，在「多人游戏 → 直接连接」中粘贴地址。";
        RoomStatus.Text = _room.State switch
        {
            "waiting" => "",
            "host-scanning" => "正在查找对局域网开放的世界…",
            "host-starting" => "已发现局域网世界，正在创建房间…",
            "host-ok" => "房间已建立，可以分享邀请码。",
            "guest-connecting" or "guest-starting" => "正在连接房主…",
            "guest-ok" => "已连接房主。",
            "exception" => $"陶瓦联机失败或连接已断开（错误 {_room.Value}）。请检查双方网络和游戏状态后重试。",
            _ => "陶瓦返回了无法识别的联机状态，请退出房间后重试。"
        };
    }

    /// <summary>清除房间角色、邀请码和成员列表，让页面恢复首页状态。</summary>
    private void ClearRoom()
    {
        _room = ("waiting", "", []);
        _guestInvite = "";
        _host = _detail = false;
        Players.ItemsSource = null;
    }

    /// <summary>在发起操作的页面显示可读错误，超时时提示用户重试。</summary>
    private static void ShowError(InfoBar feedback, Exception error)
    {
        feedback.Severity = InfoBarSeverity.Error;
        feedback.Message = error is OperationCanceledException ? "陶瓦联机操作超时，请重试。" : error.Message;
        feedback.IsOpen = true;
    }

    /// <summary>窗口退出时取消并等待当前操作，最后停止陶瓦并保留可导出的日志。</summary>
    public async Task StopAsync()
    {
        _closing = true;
        _timer.Stop();
        _shutdown.Cancel();
        await Task.WhenAll(_work, _poll);
        ClearRoom();
        await _session.StopAsync();
        _shutdown.Dispose();
    }
}
