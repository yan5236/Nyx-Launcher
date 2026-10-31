using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

public sealed partial class ModDetailsView
{
    /// <summary>复用模组安装弹窗与文件夹选择器，并将当前所选版本置顶；数据包增加存档步骤，资源包和光影直接写入客户端资源目录。</summary>
    private ModInstallDialog CreateResourceDialog(ModrinthModFile file, string folder)
    {
        var isolation = _settings!.Current.Isolation;
        var targets = (Directory.Exists(folder) ? InstalledVersionService.Scan(folder) : [])
            .Where(x => x.Error is null && x.GameVersion == _game)
            .Select(x => new ModInstallTarget(x,
                Path.Combine(VersionIsolation.Resolve(folder, x, isolation), file.Category switch
                { "datapack" => "saves", "shader" => "shaderpacks", _ => "resourcepacks" })))
            .ToArray();
        return new ModInstallDialog(XamlRoot, file.FileName, targets, target =>
        {
            _tasks!.AddResource(file, folder, target, _game);
            ShowNotice("已添加到下载任务：" + file.FileName, InfoBarSeverity.Success);
        }, directory =>
        {
            _tasks!.AddModToFolder(file, directory);
            ShowNotice("已添加到下载任务：" + file.FileName, InfoBarSeverity.Success);
        }, file.Category == "datapack" ? target =>
        {
            string root = ResourceInstallationService.ResolveRoot(folder, target, _game, isolation);
            return ResourceInstallationService.Worlds(root).Select(world =>
            {
                return new ModInstallTarget(new InstalledVersion(Path.GetFileName(world), world, "", "", "", null),
                    Path.Combine(world, "datapacks"), world);
            }).ToArray();
        } : null, (target, world) =>
        {
            _tasks!.AddResource(file, folder, target, _game, world);
            ShowNotice("已添加到下载任务：" + file.FileName, InfoBarSeverity.Success);
        }, preferredVersion: Directory.Exists(folder) ? _folders?.RestoreVersion(InstalledVersionService.Scan(folder))?.Name : null);
    }
}
