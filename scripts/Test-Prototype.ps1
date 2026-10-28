$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../src/NyxLauncher'
[xml]$shell = Get-Content (Join-Path $project 'MainPage.xaml') -Raw -Encoding utf8
$ns = [System.Xml.XmlNamespaceManager]::new($shell.NameTable)
$ns.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$navigation = $shell.SelectSingleNode('//ui:NavigationView', $ns)

# 验证原型的固定导航入口和账户菜单顺序；不启动应用或执行业务操作。
if ($navigation.SelectSingleNode('ui:NavigationView.PaneHeader', $ns)) {
    throw '侧栏不应包含应用品牌区域。'
}
$items = $navigation.SelectNodes('ui:NavigationView.MenuItems/ui:NavigationViewItem', $ns)
if (($items | ForEach-Object { $_.Content }) -join ',' -cne '首页,版本管理,下载') {
    throw '主导航必须依次为首页、版本管理、下载。'
}
if ($navigation.IsSettingsVisible -ne 'False') {
    throw '设置入口只能放在账户弹层中。'
}
$flyout = $navigation.SelectSingleNode('ui:NavigationView.PaneFooter//ui:MenuFlyout', $ns)
$menu = @($flyout.ChildNodes | Where-Object NodeType -eq Element)
if ($menu.Count -ne 3 -or $menu[0].Text -ne '账户' -or
    $menu[1].LocalName -ne 'MenuFlyoutSeparator' -or $menu[2].Text -ne '设置') {
    throw '账户弹层必须按账户、分隔线、设置排列。'
}

$xamlFiles = Get-ChildItem $project -Recurse -Filter '*.xaml' |
    Where-Object FullName -notmatch '\\(bin|obj)\\'
foreach ($file in $xamlFiles) {
    [xml]$document = Get-Content $file.FullName -Raw -Encoding utf8
    foreach ($source in $document.SelectNodes('//@Source')) {
        if ($source.Value.StartsWith('ms-appx:///Assets/')) {
            $asset = Join-Path $project $source.Value.Substring('ms-appx:///'.Length)
            if (-not (Test-Path -LiteralPath $asset)) {
                throw "缺少界面资源：$asset"
            }
        }
    }
}
Write-Output '通过：XAML 语法、三项导航、侧栏无品牌、账户菜单顺序与图片资源。'
