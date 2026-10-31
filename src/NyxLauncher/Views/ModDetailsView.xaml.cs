using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NyxLauncher.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace NyxLauncher.Views;

/// <summary>模组详情的游戏版本、加载器及文件三级选择；页面请求和弹窗随生命周期关闭，下载交给共享任务管理器。</summary>
public sealed partial class ModDetailsView : UserControl
{
    private readonly ModrinthCatalogService _service = new();
    private ResourceProject? _project;
    private IReadOnlyList<ModrinthModFile> _files = [];
    private CancellationTokenSource? _request;
    private ModInstallDialog? _dialog;
    private DownloadTaskManager? _tasks;
    private DownloadSettingsStore? _settings;
    private GameFolderStore? _folders;
    private string _game = "";
    private string _loader = "";
    private bool _active;
    public event EventHandler? BackRequested;

    /// <summary>初始化详情、失败重试和卸载清理；构造期间不访问网络。</summary>
    public ModDetailsView()
    {
        InitializeComponent();
        LoadStatus.RetryRequested += RetryRequested;
        Unloaded += View_Unloaded;
    }

    /// <summary>注入下载模块、目录和隔离设置；不创建独立下载客户端或后台任务系统。</summary>
    public void Configure(DownloadTaskManager tasks, GameFolderStore folders, DownloadSettingsStore settings)
    { _tasks = tasks; _folders = folders; _settings = settings; }

    /// <summary>从目录进入时清除详情历史；外部版本和加载器只用于置顶和标注，不跳过玩家的逐级选择。</summary>
    public void Open(ResourceProject project, string preferredGame = "", string preferredLoader = "")
    {
        _history.Clear();
        _preferredGame = preferredGame;
        _preferredLoader = preferredLoader;
        ShowProject(project, "", "");
    }

    /// <summary>切换项目并取消旧请求和弹窗；依赖入口可传入游戏与加载器直接进入文件列表，兼容性由真实发布筛选。</summary>
    private void ShowProject(ResourceProject project, string game, string loader)
    {
        Close();
        _active = true;
        _project = project;
        _files = [];
        _game = game;
        _loader = loader;
        _dependencies = null;
        ModTitle.Text = project.Title;
        Description.Text = project.Description;
        ToolTipService.SetToolTip(Description, project.Description);
        ProjectIcon.Project = project;
        Notice.IsOpen = false;
        UpdateBackLabel();
        RefreshChoices();
        _ = LoadAsync();
    }

    /// <summary>离开页面取消在途发布和前置请求并关闭版本弹窗；已创建的下载任务继续运行。</summary>
    public void Close()
    {
        _active = false;
        _request?.Cancel();
        _request = null;
        _dialog?.Hide();
        _dialog = null;
        CancelDependencies();
        LoadStatus.Stop();
    }

    /// <summary>页面卸载时释放界面请求的所有权。</summary>
    private void View_Unloaded(object sender, RoutedEventArgs e) => Close();

    /// <summary>有前置跳转历史时恢复来源模组的阶段、文件和滚动位置；只有最外层详情才返回资源目录。</summary>
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!_history.TryPop(out var previous)) { BackRequested?.Invoke(this, EventArgs.Empty); return; }
        Close();
        _active = true;
        _project = previous.Project;
        _files = previous.Files;
        _game = previous.Game;
        _loader = previous.Loader;
        _preferredGame = previous.PreferredGame;
        _preferredLoader = previous.PreferredLoader;
        _dependencies = previous.Dependencies;
        ModTitle.Text = _project.Title;
        Description.Text = _project.Description;
        ToolTipService.SetToolTip(Description, _project.Description);
        ProjectIcon.Project = _project;
        Notice.IsOpen = false;
        LoadStatus.Visibility = Visibility.Collapsed;
        UpdateBackLabel();
        RefreshChoices();
        Choices.UpdateLayout();
        FindScrollViewer(Choices)?.ChangeView(null, previous.Offset, null, true);
    }

    /// <summary>仅撤回当前选择阶段，保留项目及已加载发布；文件阶段退至加载器，加载器阶段退至游戏版本。</summary>
    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        _dependencies = null;
        if (_loader.Length > 0) _loader = "";
        else _game = "";
        RefreshChoices();
    }

    /// <summary>复制模组名称到系统剪贴板；失败通过页内提示反馈，不影响当前选择。</summary>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        try
        {
            var data = new DataPackage();
            data.SetText(_project.Title);
            Clipboard.SetContent(data);
            ShowNotice("已复制模组名称", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowNotice("复制失败：" + ex.Message, InfoBarSeverity.Error); }
    }

    /// <summary>通过系统浏览器打开受信任的 Modrinth 项目地址；使用项目编号避免依赖可变的 slug。</summary>
    private async void Website_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri("https://modrinth.com/mod/" + Uri.EscapeDataString(_project.Id))) && _active)
                ShowNotice("无法打开浏览器，请检查默认浏览器设置。", InfoBarSeverity.Error);
        }
        catch (Exception ex) { if (_active) ShowNotice("打开失败：" + ex.Message, InfoBarSeverity.Error); }
    }

    /// <summary>失败时重新读取当前项目全部发布，不复用失败或不完整的结果。</summary>
    private void RetryRequested(object? sender, EventArgs e) => _ = LoadAsync();

    /// <summary>使用可取消的真实版本清单填充阶段；空列表独立显示，网络错误复用既有加载和重试控件。</summary>
    private async Task LoadAsync()
    {
        if (!_active || _project is null) return;
        _request?.Cancel();
        using var request = new CancellationTokenSource();
        _request = request;
        Choices.Visibility = EmptyHint.Visibility = Visibility.Collapsed;
        LoadStatus.Visibility = Visibility.Visible;
        LoadStatus.Start("模组版本");
        try
        {
            var files = await _service.FetchModFilesAsync(_project.Id, request.Token);
            request.Token.ThrowIfCancellationRequested();
            _files = files;
            LoadStatus.Stop();
            LoadStatus.Visibility = Visibility.Collapsed;
            RefreshChoices();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (HttpRequestException ex)
        { if (!request.IsCancellationRequested) LoadStatus.ShowError(ex.Message); }
        finally { if (ReferenceEquals(_request, request)) _request = null; }
    }

    /// <summary>按阶段展示真实文件支持的选项；外部筛选置顶并标注，其余数字版本降序，特殊版本保留发布时间顺序，文件阶段同步前置卡片。</summary>
    private void RefreshChoices()
    {
        IEnumerable<ModDetailChoice> choices;
        if (_game.Length == 0)
        {
            choices = _files.SelectMany(x => x.GameVersions).Distinct()
                .OrderByDescending(x => x == _preferredGame)
                .ThenByDescending(x => Version.TryParse(x, out var v) ? v : new Version())
                .Select(x => new ModDetailChoice(x, "", "ms-appx:///Assets/java-mojang.png", "", null,
                    FilterLabel: x == _preferredGame ? "筛选版本" : ""));
            StageText.Text = "1 / 3 · 选择 Minecraft 版本";
        }
        else if (_loader.Length == 0)
        {
            choices = _files.Where(x => x.GameVersions.Contains(_game)).SelectMany(x => x.Loaders).Distinct()
                .OrderByDescending(x => x == _preferredLoader)
                .Select(x => new ModDetailChoice(ResourceProject.LoaderName(x), "",
                    ResourceProject.LoaderIcon(x), "", null, x, x == _preferredLoader ? "筛选加载器" : ""));
            StageText.Text = $"2 / 3 · {_game} · 选择加载器";
        }
        else
        {
            choices = _files.Where(x => x.GameVersions.Contains(_game) && x.Loaders.Contains(_loader))
                .Select(x => new ModDetailChoice(x.FileName, x.Detail, "", x.Badge, x));
            StageText.Text = $"3 / 3 · {_game} · {ResourceProject.LoaderName(_loader)} · 选择模组文件";
        }
        var rows = choices.ToArray();
        Choices.ItemsSource = rows;
        Choices.Visibility = Visibility.Visible;
        EmptyHint.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviousStep.IsEnabled = _game.Length > 0;
        RefreshDependencies();
        Choices.UpdateLayout();
        FindScrollViewer(Choices)?.ChangeView(null, 0, null, true);
        if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            var animation = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(180) };
            Storyboard.SetTarget(animation, Choices);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
    }

    /// <summary>逐级选择游戏与加载器，点击文件后扫描兼容的本地版本并显示真实目标路径；成功创建任务后保留文件列表。</summary>
    private async void Choice_Click(object sender, ItemClickEventArgs e)
    {
        if (!_active || _dialog is not null || e.ClickedItem is not ModDetailChoice choice) return;
        Notice.IsOpen = false;
        if (_game.Length == 0) { _game = choice.Title; RefreshChoices(); return; }
        if (_loader.Length == 0) { _loader = choice.Loader; RefreshChoices(); return; }
        if (choice.File is not { } file || _tasks is null || _settings is null) return;
        ModInstallDialog? dialog = null;
        try
        {
            string folder = _folders?.Selected ?? throw new IOException("请先选择游戏目录。");
            var targets = InstalledVersionService.Scan(folder).Where(x => ModInstallationService.IsCompatible(x, _game, _loader))
                .Select(x => new ModInstallTarget(x, Path.Combine(VersionIsolation.Resolve(folder, x, _settings.Current.Isolation), "mods")))
                .ToArray();
            dialog = new ModInstallDialog(XamlRoot, file.FileName, targets, target =>
            {
                _tasks.AddMod(file, folder, target, _game, _loader);
                ShowNotice("已添加到下载任务：" + file.FileName, InfoBarSeverity.Success);
            });
            _dialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex) { if (_active) ShowNotice(ex.Message, InfoBarSeverity.Error); }
        finally
        {
            if (ReferenceEquals(_dialog, dialog))
            { _dialog = null; if (_active) Choices.Focus(FocusState.Programmatic); }
        }
    }

    /// <summary>用统一页内提示反馈复制、浏览器及任务创建结果，不打断阶段选择。</summary>
    private void ShowNotice(string message, InfoBarSeverity severity)
    { Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true; }
}

/// <summary>三级卡片显示模型；游戏和加载器在标题同行标注外部筛选，文件另显示发布渠道字母及日期和类型副标题。</summary>
public sealed record ModDetailChoice(string Title, string Detail, string Icon, string Badge, ModrinthModFile? File,
    string Loader = "", string FilterLabel = "")
{
    public Visibility IconVisibility => File is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => File is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DetailVisibility => File is null ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>正式版字母使用品牌绿，Beta 偏橙、Alpha 红，与发布渠道保持一致。</summary>
    public SolidColorBrush BadgeForeground => new(File?.Channel switch
    {
        "beta" => Windows.UI.Color.FromArgb(255, 178, 102, 15),
        "alpha" => Windows.UI.Color.FromArgb(255, 196, 43, 58),
        _ => Windows.UI.Color.FromArgb(255, 57, 116, 80)
    });
    /// <summary>字母底色使用对应渠道的浅色，保持既有卡片对比度。</summary>
    public SolidColorBrush BadgeBackground => new(File?.Channel switch
    {
        "beta" => Windows.UI.Color.FromArgb(255, 255, 240, 217),
        "alpha" => Windows.UI.Color.FromArgb(255, 253, 232, 235),
        _ => Windows.UI.Color.FromArgb(255, 224, 235, 214)
    });
    /// <summary>将筛选标注和文件说明加入读屏名称，省略空字段及多余分隔符。</summary>
    public string AccessibleName => string.Join("，", new[] { Title, FilterLabel, Detail }.Where(x => x.Length > 0));
}
