using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using System.Collections.ObjectModel;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>版本模组列表及文件操作；远程摘要和更新检查可取消，卡片不加载下载页详情。</summary>
public sealed partial class ModManagementView : UserControl
{
    private readonly ModrinthCatalogService _catalog = new();
    private IReadOnlyList<InstalledMod> _mods = [];
    private readonly ObservableCollection<InstalledMod> _visibleMods = [];
    private GameFolderStore? _folders;
    private DownloadSettingsStore? _settings;
    private Func<bool>? _blocked;
    private InstalledVersion? _version;
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _operation;
    private bool _active;
    private bool _busy;
    public event EventHandler? DownloadRequested;
    public event EventHandler<(ResourceProject Project, InstalledVersion Version)>? ModFilesRequested;

    /// <summary>创建控件并在卸载时取消列表与文件更新请求，避免隐藏页面继续写入。</summary>
    public ModManagementView()
    {
        InitializeComponent();
        ModList.ItemsSource = _visibleMods;
        Unloaded += (_, _) => SetActive(false);
    }

    /// <summary>共享游戏目录、源设置和运行保护；不在配置时访问磁盘或网络。</summary>
    public void Configure(GameFolderStore folders, DownloadSettingsStore settings, Func<bool> blocked)
    {
        _folders = folders; _settings = settings; _blocked = blocked;
        _catalog.Source = () => settings.Current.VersionSource;
    }

    /// <summary>进入时读取实际目录；离开时取消旧请求并清空模组及选择，迟到结果不得回填隐藏列表。</summary>
    public void SetActive(bool active, InstalledVersion? version = null)
    {
        if (_active == active && (!active || _version == version)) return;
        _active = active;
        _load?.Cancel(); _operation?.Cancel();
        if (!active)
        {
            _mods = [];
            _selected.Clear();
            _visibleMods.Clear();
            RefreshSelection();
            return;
        }
        _version = version;
        _selected.Clear();
        _mods = []; Filter();
        SearchInput.Text = ""; StateFilter.SelectedIndex = 0;
        _ = ReloadAsync();
    }

    /// <summary>重新验证版本与目录归属，并采用当前隔离设置；无选中版本时拒绝操作。</summary>
    private string DirectoryPath() => ModManagementService.Resolve(_folders?.Selected ?? "",
        _version ?? throw new IOException("尚未选择版本。"), _settings!.Current.Isolation);

    /// <summary>先展示本地文件，再补摘要及兼容更新；网络失败保留本地操作，取消和过期结果不发布。</summary>
    private async Task ReloadAsync(string? operationError = null)
    {
        _load?.Cancel();
        using var request = new CancellationTokenSource();
        _load = request;
        LoadProgress.Visibility = Visibility.Visible; Status.IsOpen = false;
        if (operationError is not null) ShowError(operationError);
        ModList.IsEnabled = false;
        try
        {
            string directory = DirectoryPath();
            var local = await Task.Run(() => ModManagementService.ScanAsync(directory, request.Token));
            request.Token.ThrowIfCancellationRequested();
            _mods = local; Filter(); ModList.IsEnabled = !_busy;
            var identified = await _catalog.IdentifyModsAsync(local, request.Token);
            request.Token.ThrowIfCancellationRequested();
            MergeMetadata(identified, false);
            var updated = await _catalog.FindModUpdatesAsync(identified, _version!, request.Token);
            request.Token.ThrowIfCancellationRequested();
            MergeMetadata(updated, true);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { if (!request.IsCancellationRequested) ShowError(operationError ?? ex.Message); }
        finally
        {
            if (ReferenceEquals(_load, request))
            {
                _load = null;
                if (!_busy) { LoadProgress.Visibility = Visibility.Collapsed; ModList.IsEnabled = true; }
            }
        }
    }

    /// <summary>合并后台摘要及更新信息，保留加载期间启停操作产生的新路径和状态。</summary>
    private void MergeMetadata(IReadOnlyList<InstalledMod> incoming, bool includeUpdates)
    {
        var byHash = incoming.DistinctBy(x => x.Sha1).ToDictionary(x => x.Sha1);
        _mods = _mods.Select(mod => byHash.TryGetValue(mod.Sha1, out var info)
            ? mod with { Project = info.Project, VersionId = info.VersionId, Published = info.Published,
                Update = includeUpdates ? info.Update : mod.Update } : mod).ToArray();
        Filter();
    }

    /// <summary>按名称和启停状态增量同步可见项，仅替换变化的卡片，保留其他卡片、列表实例与滚动位置。</summary>
    private void Filter()
    {
        if (ModList is null) return;
        string query = SearchInput.Text.Trim();
        var matches = _mods.Where(x => (x.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            x.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (StateFilter.SelectedIndex == 0 || x.Disabled == (StateFilter.SelectedIndex == 2))).ToArray();
        _filtering = true;
        try
        {
            for (int i = _visibleMods.Count - 1; i >= 0; i--)
                if (!matches.Any(x => x.Path == _visibleMods[i].Path)) _visibleMods.RemoveAt(i);
            for (int i = 0; i < matches.Length; i++)
            {
                var existing = _visibleMods.FirstOrDefault(x => x.Path == matches[i].Path);
                if (existing is null) _visibleMods.Insert(i, matches[i]);
                else
                {
                    int previousIndex = _visibleMods.IndexOf(existing);
                    if (previousIndex != i) _visibleMods.Move(previousIndex, i);
                    if (existing != matches[i]) _visibleMods[i] = matches[i];
                }
            }
            _selected.IntersectWith(matches.Select(x => x.Path));
            foreach (var mod in matches.Where(x => _selected.Contains(x.Path) && !ModList.SelectedItems.Contains(x))) ModList.SelectedItems.Add(mod);
        }
        finally { _filtering = false; }
        RefreshSelection();
        Empty.Text = _mods.Count == 0 ? "暂无模组" : "没有匹配的模组";
        Empty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>按钮发起本地搜索。</summary>
    private void Search_Click(object sender, RoutedEventArgs e) => Filter();
    /// <summary>Enter 提交搜索并阻止按键继续传播。</summary>
    private void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Filter(); } }
    /// <summary>状态选择改变时立即过滤现有列表。</summary>
    private void State_Changed(object sender, SelectionChangedEventArgs e) => Filter();
    /// <summary>请求主导航直接打开下载页的模组分类。</summary>
    private void Download_Click(object sender, RoutedEventArgs e) => DownloadRequested?.Invoke(this, EventArgs.Empty);
    /// <summary>复核游戏与安装状态，并拒绝对离开页面后取消的操作继续写入。</summary>
    private void EnsureWritable()
    {
        _operation?.Token.ThrowIfCancellationRequested();
        if (!_active || _blocked?.Invoke() == true) throw new IOException("请先停止游戏并等待安装完成。");
    }

    /// <summary>串行执行文件操作；安装、更新和删除结束后重读文件，启停仅更新对应卡片且保留后台元数据请求，失败显示具体原因。</summary>
    private async Task RunAsync(Func<CancellationToken, Task> action, bool reload = true)
    {
        if (_busy) return;
        _busy = true;
        if (reload) _load?.Cancel();
        using var operation = new CancellationTokenSource();
        _operation = operation;
        ModList.IsEnabled = InstallButton.IsEnabled = false;
        foreach (var control in BatchPanel.Children.OfType<Control>()) control.IsEnabled = false;
        if (reload) LoadProgress.Visibility = Visibility.Visible;
        Status.IsOpen = false;
        string? error = null;
        try { EnsureWritable(); await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            _operation = null; _busy = false;
            ModList.IsEnabled = InstallButton.IsEnabled = true;
            foreach (var control in BatchPanel.Children.OfType<Control>()) control.IsEnabled = true;
            LoadProgress.Visibility = _load is null ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_active && reload) await ReloadAsync(error);
        else if (_active && error is not null) ShowError(error);
    }

    /// <summary>选择单个 JAR，复用本地归档识别、加载器检查与不覆盖复制，直接安装到当前版本目录。</summary>
    private async void Install_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        picker.FileTypeFilter.Add(".jar");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        EnsureWritable();
        var resource = await Task.Run(() => LocalResource.Read(file.Path), token);
        if (resource.Category != "mod" || !resource.Supports(_version!)) throw new IOException("此文件不是适用于当前加载器的模组。");
        string directory = resource.Resolve(_folders!.Selected!, _version!, _settings!.Current.Isolation, null);
        if (File.Exists(Path.Combine(directory, Path.GetFileName(file.Path) + ".disabled"))) throw new IOException("此模组已有禁用文件。");
        EnsureWritable();
        await resource.CopyAsync(directory);
    });

    /// <summary>切换单个文件启停状态，冲突或文件变化时保留原文件。</summary>
    private async void Toggle_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: InstalledMod mod }) await RunAsync(token => ToggleModAsync(mod, token), reload: false); }

    /// <summary>按已检测的兼容发布下载并替换，发布前重新检查当前目录、游戏状态和取消状态。</summary>
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledMod { Update: not null } mod })
            await RunAsync(token => UpdateModAsync(mod, token));
    }

    /// <summary>供单项与批量更新共用；下载前后复核真实版本兼容性和隔离目录，并保留文件服务的失败保护。</summary>
    private async Task UpdateModAsync(InstalledMod mod, CancellationToken token)
    {
        var update = mod.Update!;
        string directory = DirectoryPath();
        string root = _folders!.Selected!;
        var version = _version!;
        string loader = version.Loader.Split(':')[0].Trim().ToLowerInvariant();
        ModInstallationService.ResolveMods(root, version, update, version.GameVersion, loader, _settings!.Current.Isolation);
        using var transport = new DownloadTransport(_settings!.Current);
        await ModManagementService.UpdateAsync(directory, mod, update, transport, () =>
        {
            EnsureWritable();
            if (DirectoryPath() != directory) throw new IOException("游戏目录已变化，请重新打开模组管理。");
            ModInstallationService.ResolveMods(root, version, update, version.GameVersion, loader, _settings.Current.Isolation);
        }, token);
    }

    /// <summary>用现有摘要打开详情；已识别模组提供浏览器主页与精确版本文件列表入口，关闭弹窗后再触发导航。</summary>
    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledMod mod }) return;
        try
        {
            var version = _version;
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = mod.Title, CloseButtonText = "关闭",
                PrimaryButtonText = mod.Project is not null ? "Modrinth 主页" : "",
                SecondaryButtonText = mod.Project is not null && version is not null ? "下载" : "",
                Content = new TextBlock { Text = string.Join("\n\n", new[] { mod.Description, mod.FileName, mod.State }.Where(x => x.Length > 0)), TextWrapping = TextWrapping.Wrap } };
            var result = await dialog.ShowThemedAsync();
            if (mod.Project is not { } project) return;
            if (result == ContentDialogResult.Primary)
            {
                if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("https://modrinth.com/mod/" + Uri.EscapeDataString(project.Id))))
                    ShowError("无法打开浏览器，请检查默认浏览器设置。");
            }
            else if (result == ContentDialogResult.Secondary && version is not null && _active)
                ModFilesRequested?.Invoke(this, (project, version));
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    /// <summary>打开资源管理器并选中当前文件，验证归属及文件仍在，失败显示原因。</summary>
    private async void Location_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledMod mod }) return;
        try
        {
            string directory = DirectoryPath(); ModManagementService.Validate(directory, mod);
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(directory);
            var options = new Windows.System.FolderLauncherOptions();
            options.ItemsToSelect.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(mod.Path));
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder, options)) throw new IOException("无法打开文件所在位置。");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    /// <summary>确认后复核当前文件并移入系统回收站；取消不修改文件，不永久删除。</summary>
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledMod mod }) return;
        await RunAsync(async _ =>
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "删除“" + mod.Title + "”？", Content = mod.FileName,
                PrimaryButtonText = "移入回收站", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary) return;
            EnsureWritable(); ModManagementService.Validate(DirectoryPath(), mod);
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(mod.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        });
    }

    /// <summary>仅在操作或加载失败时显示具体错误，不添加常驻说明文案。</summary>
    private void ShowError(string message) { Status.Message = message; Status.IsOpen = true; }
}
