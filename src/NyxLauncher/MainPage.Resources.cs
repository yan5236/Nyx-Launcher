using Microsoft.UI.Xaml.Controls;
using NyxLauncher.Services;
using NyxLauncher.Views;

namespace NyxLauncher;

public sealed partial class MainPage
{
    /// <summary>识别拖入文件并复用下载安装弹窗；整合包保留原导入入口，本地资源安装等待复制结束，异常由拖放入口提示。</summary>
    private async Task ImportResourceAsync(string path)
    {
        var resource = await Task.Run(() => LocalResource.Read(path));
        if (resource.Category == "modpack")
        {
            Navigation.SelectedItem = VersionsItem;
            await VersionsContent.ImportModpackAsync(path);
            return;
        }
        string folder = _folders.Selected ?? "";
        var isolation = _settings.Current.Isolation;
        var versions = Directory.Exists(folder) ? InstalledVersionService.Scan(folder) : [];
        var targets = versions.Where(resource.Supports).Select(x => new ModInstallTarget(x,
            Path.Combine(VersionIsolation.Resolve(folder, x, isolation), resource.DirectoryName))).ToArray();
        var dialog = new ModInstallDialog(XamlRoot, Path.GetFileName(path), targets, _ => { }, _ => { },
            worlds: resource.Category == "datapack" ? target =>
            {
                string root = ResourceInstallationService.ResolveRoot(folder, target, target.GameVersion, isolation);
                return ResourceInstallationService.Worlds(root).Select(world => new ModInstallTarget(
                    new InstalledVersion(Path.GetFileName(world), world, "", "", "", null),
                    Path.Combine(world, "datapacks"), world)).ToArray();
            } : null,
            preferredVersion: _folders.RestoreVersion(versions)?.Name,
            localInstall: async (target, destination) =>
            {
                if (target is not null) destination = resource.Resolve(folder, target, isolation, destination);
                await resource.CopyAsync(destination!);
            });
        await dialog.ShowThemedAsync();
    }
}
