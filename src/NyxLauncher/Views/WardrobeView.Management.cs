using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using NyxLauncher.Services;

namespace NyxLauncher.Views;

public sealed partial class WardrobeView
{
    /// <summary>导入 PNG 并填写名称与模型，重复纹理复用原条目且保留原名称；仅保存本地副本，点击应用才修改微软账户。</summary>
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || AccountPicker.SelectedItem is not GameAccount { IsMicrosoft: true } || _store.LoadError is not null) return;
        _busy = true; UpdateEnabled();
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            picker.FileTypeFilter.Add(".png");
            var file = await picker.PickSingleFileAsync();
            if (file is null || !_active) return;
            byte[] png;
            using (var input = File.OpenRead(file.Path))
            {
                if (input.Length > 1024 * 1024) throw new InvalidDataException("皮肤不能超过 1 MiB。");
                using var output = new MemoryStream(); await input.CopyToAsync(output); png = output.ToArray();
            }
            WardrobeStore.ValidatePng(png);
            var name = new TextBox { Header = "皮肤名称", Text = Path.GetFileNameWithoutExtension(file.Path), MaxLength = 64 };
            var model = new ComboBox { Header = "模型", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "经典（宽手臂）", "纤细（窄手臂）" }, SelectedIndex = 0 };
            var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(name); panel.Children.Add(model);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "上传皮肤", Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消" };
            WardrobeSkin? added = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try { added = _store.Add(name.Text, model.SelectedIndex == 1, png); }
                catch (Exception ex) { error.Text = ex.Message; args.Cancel = true; }
            };
            await dialog.ShowAsync();
            if (added is null || !_active) return;
            CategoryTabs.SelectedItem = SkinsTab; Search.Text = "";
            RebuildSkins(); FilterCards();
            Cards.SelectedItem = _skinCards.First(c => c.Skin == added);
        }
        catch (Exception ex) { ShowStatus(ex.Message); }
        finally { _busy = false; UpdateEnabled(); }
    }

    /// <summary>保存的皮肤均可重命名和导出，当前穿戴不提供删除；披风不提供皮肤菜单。</summary>
    private void SkinMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: Card { Skin: { } skin } } button) return;
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["OpaqueMenuPresenterStyle"] };
        var rename = new MenuFlyoutItem { Text = "重命名" };
        var export = new MenuFlyoutItem { Text = "导出" };
        var delete = new MenuFlyoutItem { Text = "删除" };
        rename.Click += async (_, _) => await RenameAsync(skin);
        export.Click += async (_, _) => await ExportAsync(skin);
        delete.Click += async (_, _) => await DeleteAsync(skin);
        menu.Items.Add(rename); menu.Items.Add(export);
        if (skin.Id != _currentSkinId) menu.Items.Add(delete);
        menu.ShowAt(button);
    }

    /// <summary>重命名失败保留弹窗和输入，成功后重建本地卡片，不改服务器皮肤。</summary>
    private async Task RenameAsync(WardrobeSkin skin)
    {
        if (_busy) return;
        _busy = true; UpdateEnabled();
        try
        {
            var name = new TextBox { Text = skin.Name, MaxLength = 64 };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(name); panel.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "重命名皮肤", Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消" };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try { _store.Rename(skin, name.Text); }
                catch (Exception ex) { error.Text = ex.Message; args.Cancel = true; }
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) { RebuildSkins(); FilterCards(); }
        }
        catch (Exception ex) { ShowStatus(ex.Message); }
        finally { _busy = false; UpdateEnabled(); }
    }

    /// <summary>通过系统保存窗口导出原始 PNG；同目录临时文件写完再替换，失败不破坏已有目标文件。</summary>
    private async Task ExportAsync(WardrobeSkin skin)
    {
        if (_busy) return;
        _busy = true; UpdateEnabled();
        string? temporary = null;
        try
        {
            byte[] png = _store.Read(skin);
            string safeName = string.Concat(skin.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { SuggestedFileName = safeName, DefaultFileExtension = ".png" };
            picker.FileTypeChoices.Add("PNG 皮肤", new List<string> { ".png" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            temporary = file.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, png); File.Move(temporary, file.Path, true);
            ShowStatus("皮肤已导出。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowStatus(ex.Message); }
        finally
        {
            if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            _busy = false; UpdateEnabled();
        }
    }

    /// <summary>确认后删除非当前皮肤副本；弹窗前后检查当前标识，防止过期菜单删除正在穿戴的皮肤。</summary>
    private async Task DeleteAsync(WardrobeSkin skin)
    {
        if (_busy || skin.Id == _currentSkinId) return;
        _busy = true; UpdateEnabled();
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "删除皮肤", Content = $"从衣柜删除“{skin.Name}”？已应用的游戏皮肤不受影响。", PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (skin.Id == _currentSkinId) return;
            _store.Remove(skin);
        }
        catch (Exception ex) { ShowStatus(ex.Message); }
        finally { RebuildSkins(); FilterCards(); _busy = false; UpdateEnabled(); }
    }
}
