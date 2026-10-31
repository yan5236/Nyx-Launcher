using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;
using Microsoft.Windows.Storage.Pickers;

namespace NyxLauncher.Views;

/// <summary>显示可兼容的本地版本及真实 mods 路径；点选后创建现有下载任务，失败保留弹窗显示原因。</summary>
public sealed partial class ModInstallDialog : ContentDialog
{
    private readonly Action<InstalledVersion> _install;
    private readonly Action<string> _downloadToFolder;
    private bool _closed;
    private readonly Func<InstalledVersion, IReadOnlyList<ModInstallTarget>>? _worlds;
    private readonly Action<InstalledVersion, string>? _installWorld;
    private readonly IReadOnlyList<ModInstallTarget> _versions;
    private InstalledVersion? _selectedVersion;

    /// <summary>绑定安装候选和两种任务回调；可选存档提供器用于数据包的第二步，存档回调接收客户端和真实存档路径；按窗口限制列表高度，无候选仍允许手动选目录。关闭后忽略文件夹选择器迟到结果。</summary>
    public ModInstallDialog(XamlRoot root, string fileName, IReadOnlyList<ModInstallTarget> targets, Action<InstalledVersion> install, Action<string> downloadToFolder,
        Func<InstalledVersion, IReadOnlyList<ModInstallTarget>>? worlds = null, Action<InstalledVersion, string>? installWorld = null)
    {
        InitializeComponent();
        XamlRoot = root;
        FileName.Text = fileName;
        Title = new InstallDialogTitle("选择安装版本", Visibility.Collapsed);
        DialogContent.Width = Math.Clamp(root.Size.Width - 100, 240, 460);
        DialogContent.MaxHeight = Math.Max(160, root.Size.Height - 220);
        Targets.ItemsSource = targets;
        EmptyHint.Visibility = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _versions = targets;
        _worlds = worlds;
        _installWorld = installWorld;
        if (worlds is not null) Title = new InstallDialogTitle("1 / 2 · 选择安装版本", Visibility.Collapsed);
        _install = install;
        _downloadToFolder = downloadToFolder;
        Closed += (_, _) => _closed = true;
    }

    /// <summary>通过当前窗口的系统选择器选择直接下载目录；取消不创建任务，失败保留弹窗，关闭后的选择结果丢弃。</summary>
    private async void ChooseDirectory_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        IsPrimaryButtonEnabled = Targets.IsEnabled = false;
        Error.IsOpen = false;
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { CommitButtonText = "下载到此文件夹" };
            var folder = await picker.PickSingleFolderAsync();
            if (_closed || folder is null) return;
            _downloadToFolder(folder.Path);
            Hide();
        }
        catch (Exception ex) { if (!_closed) { Error.Message = ex.Message; Error.IsOpen = true; } }
        finally { IsPrimaryButtonEnabled = Targets.IsEnabled = true; }
    }

    /// <summary>点击客户端时按需进入存档列表，最终目标创建任务成功后关闭；失败保留当前步骤，重复点击由列表禁用及任务管理器保护。</summary>
    private void Target_Click(object sender, ItemClickEventArgs e)
    {
        if (!Targets.IsEnabled || e.ClickedItem is not ModInstallTarget target) return;
        Targets.IsEnabled = false;
        try
        {
            if (_selectedVersion is not null)
            { _installWorld!(_selectedVersion, target.WorldPath!); Hide(); }
            else if (_worlds is not null)
            {
                var worlds = _worlds(target.Version);
                _selectedVersion = target.Version;
                Targets.ItemsSource = worlds;
                Title = new InstallDialogTitle("2 / 2 · 选择存档", Visibility.Visible);
                EmptyHint.Text = "此客户端没有存档，可返回选择其他版本或选择文件夹下载。";
                EmptyHint.Visibility = worlds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else { _install(target.Version); Hide(); }
        }
        catch (Exception ex) { Error.Message = ex.Message; Error.IsOpen = true; }
        finally { Targets.IsEnabled = true; }
    }
    /// <summary>卡片进入可视树时从当前存档读取图标，版本卡片使用已有占位图。</summary>
    private void Icon_Loaded(object sender, RoutedEventArgs e) => LoadIcon((Image)sender);

    /// <summary>虚拟化卡片切换绑定时重新读取图标，避免复用上一存档的图片。</summary>
    private void Icon_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => LoadIcon((Image)sender);

    /// <summary>通过文件流解码所选存档的 icon.png，避免本地 file URI 加载失败；缺失或损坏时保留草方块，迟到结果不得覆盖已回收卡片。</summary>
    private async void LoadIcon(Image image)
    {
        var request = new object();
        image.Tag = request;
        image.Source = new BitmapImage(new Uri("ms-appx:///Assets/java-mojang.png"));
        if (image.DataContext is not ModInstallTarget { WorldPath: { } world }) return;
        try
        {
            string path = Path.Combine(world, "icon.png");
            GameInstallationService.EnsureNoLinks(path);
            using var stream = File.OpenRead(path);
            if (stream.Length > 8 * 1024 * 1024) return;
            var bitmap = new BitmapImage { DecodePixelWidth = 64 };
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            if (ReferenceEquals(image.Tag, request)) image.Source = bitmap;
        }
        catch (Exception) { /* 存档图片为可选内容，读取或解码失败时保留占位，不阻断安装。 */ }
    }

    /// <summary>从存档步骤返回客户端列表，不创建任务；保留直接下载入口。</summary>
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVersion is null || !Targets.IsEnabled) return;
        _selectedVersion = null;
        Targets.ItemsSource = _versions;
        Title = new InstallDialogTitle("1 / 2 · 选择安装版本", Visibility.Collapsed);
        EmptyHint.Visibility = _versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Error.IsOpen = false;
    }

}

/// <summary>安装卡片复用客户端和存档显示布局；WorldPath 仅用于存档，目标目录沿用隔离规则。</summary>
public sealed record ModInstallTarget(InstalledVersion Version, string ModsPath, string? WorldPath = null)
{
    /// <summary>无版本详情的存档卡片折叠整行，避免名称与路径之间留下空白。</summary>
    public Visibility DetailVisibility => string.IsNullOrEmpty(Version.Detail) ? Visibility.Collapsed : Visibility.Visible;
    public string AccessibleName => Version.AccessibleName + "，安装到 " + ModsPath;
}

/// <summary>弹窗标题及左上角返回箭头的显示状态；每次切换步骤重新绑定。</summary>
public sealed record InstallDialogTitle(string Text, Visibility BackVisibility);
