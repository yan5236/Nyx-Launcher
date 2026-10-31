using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

/// <summary>本地、拖放及在线整合包共用的名称输入弹窗，实时校验并在提交时再次检查冲突。</summary>
public sealed class ModpackInstallDialog : ContentDialog
{
    private readonly TextBox _name;
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private readonly DownloadTaskManager _tasks;
    private readonly string _folder;
    private readonly string? _path;
    private readonly ModrinthModFile? _remote;

    /// <summary>用整合包名预填实例名称；用户确认后只创建任务，不在界面线程执行安装。</summary>
    public ModpackInstallDialog(XamlRoot root, DownloadTaskManager tasks, string folder, string name,
        string? path = null, ModrinthModFile? remote = null)
    {
        XamlRoot = root;
        Title = "安装整合包";
        PrimaryButtonText = "安装";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;
        _tasks = tasks; _folder = folder; _path = path; _remote = remote;
        _name = new TextBox { Header = "版本名称", Text = name, MinWidth = 320, MaxLength = 128 };
        Content = new StackPanel { Spacing = 12, Children = { _name, _error } };
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Themes/AccentButtonResources.xaml") });
        _name.TextChanged += NameChanged;
        PrimaryButtonClick += Install;
        Validate();
    }

    /// <summary>每次编辑即时检查 Windows 名称、已有版本及同目标任务；错误保持可编辑。</summary>
    private void NameChanged(object sender, TextChangedEventArgs e) => Validate();

    /// <summary>更新错误和主按钮状态；不修改用户输入，避免暗中修剪或更换实例名。</summary>
    private void Validate()
    {
        try { _tasks.ValidateModpackName(_folder, _name.Text); _error.IsOpen = false; IsPrimaryButtonEnabled = true; }
        catch (Exception ex) { _error.Message = ex.Message; _error.IsOpen = true; IsPrimaryButtonEnabled = false; }
    }

    /// <summary>提交时重新检查竞争冲突；创建失败保留弹窗和输入，成功交给标准任务界面展示。</summary>
    private void Install(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try { _tasks.AddModpack(_folder, _name.Text, _path, _remote); }
        catch (Exception ex) { args.Cancel = true; _error.Message = ex.Message; _error.IsOpen = true; }
    }
}
