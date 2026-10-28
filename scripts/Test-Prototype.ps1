$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../src/NyxLauncher'
[xml]$shell = Get-Content (Join-Path $project 'MainPage.xaml') -Raw -Encoding utf8
$ns = [System.Xml.XmlNamespaceManager]::new($shell.NameTable)
$ns.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$xns = 'http://schemas.microsoft.com/winfx/2006/xaml'
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

# 页面标题已按需求全部移除；PageTitleStyle 不再定义或使用。
foreach ($file in $xamlFiles) {
    $raw = Get-Content $file.FullName -Raw -Encoding utf8
    if ($raw.Contains('PageTitleStyle')) {
        throw "页面标题样式不应再出现：$($file.Name)"
    }
}
if ($shell.SelectSingleNode("//ui:TextBlock[@Text='账户' or @Text='设置']", $ns)) {
    throw '账户与设置页不应保留页面标题。'
}
function Find-TextBlockByText([string]$path, [string]$text) {
    [xml]$document = Get-Content $path -Raw -Encoding utf8
    $manager = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
    $manager.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
    return $document.SelectSingleNode("//ui:TextBlock[@Text='$text']", $manager)
}
if (Find-TextBlockByText (Join-Path $project 'Views/HomeView.xaml') '首页') {
    throw '首页不应保留页面标题。'
}
if (Find-TextBlockByText (Join-Path $project 'Views/VersionsView.xaml') '版本管理') {
    throw '版本管理页不应保留页面标题。'
}

# 下载页结构：六个分类 Tab、搜索框、类型筛选、列表化条目。
[xml]$downloads = Get-Content (Join-Path $project 'Views/DownloadsView.xaml') -Raw -Encoding utf8
$dns = [System.Xml.XmlNamespaceManager]::new($downloads.NameTable)
$dns.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$dns.AddNamespace('x', $xns)
if ($downloads.SelectSingleNode("//ui:TextBlock[@Text='下载']", $dns)) {
    throw '下载页不应保留页面标题。'
}
$selector = $downloads.SelectSingleNode('//ui:SelectorBar', $dns)
if (-not $selector) { throw '下载页必须使用 SelectorBar 作为分类 Tab。' }
$tabs = $selector.SelectNodes('ui:SelectorBarItem', $dns)
if (($tabs | ForEach-Object { $_.Text }) -join ',' -cne '游戏,模组,整合包,资源包,数据包,光影') {
    throw '下载页 Tab 必须依次为游戏、模组、整合包、资源包、数据包、光影。'
}
if (($tabs | ForEach-Object { $_.Tag }) -join ',' -cne 'game,mod,modpack,resourcepack,datapack,shader') {
    throw '下载页 Tab 必须提供与演示数据对应的分类标签。'
}
$search = $downloads.SelectSingleNode('//ui:AutoSuggestBox', $dns)
if (-not $search -or $search.GetAttribute('TextChanged') -ne 'SearchBox_TextChanged') {
    throw 'Tab 栏下方必须提供可过滤列表的 AutoSuggestBox 搜索框。'
}
if ($search.ParentNode.GetAttribute('Grid.Row') -ne '1') {
    throw '搜索框必须位于 Tab 栏下方的第二行。'
}
$combo = $downloads.SelectSingleNode('//ui:ComboBox', $dns)
if (-not $combo) { throw '搜索框右侧必须提供类型筛选 ComboBox。' }
if (-not [object]::ReferenceEquals($combo.ParentNode, $search.ParentNode) -or $combo.GetAttribute('Grid.Column') -ne '1') {
    throw '类型筛选必须与搜索框同一行并位于其右侧。'
}
$types = @($combo.SelectNodes('ui:ComboBoxItem', $dns) | ForEach-Object { $_.Content })
if (($types) -join ',' -cne '全部类型,正式版,快照版,远古版,愚人节版') {
    throw '类型筛选必须包含全部类型、正式版、快照版、远古版、愚人节版。'
}
$list = $downloads.SelectSingleNode('//ui:ListView', $dns)
if (-not $list) { throw '版本内容必须使用 ListView 呈现。' }
if ($list.ParentNode.GetAttribute('Grid.Row') -ne '2') {
    throw '版本列表必须位于搜索框下方的第三行。'
}
$template = $list.SelectSingleNode('ui:ListView.ItemTemplate/ui:DataTemplate', $dns)
if (-not $template) { throw '版本内容必须使用 ListView 数据模板呈现。' }
if ($template.GetAttribute('DataType', $xns) -ne 'views:DownloadEntry') {
    throw '列表数据模板必须绑定 DownloadEntry。'
}
$titleColumn = $template.SelectSingleNode('.//ui:StackPanel[@Grid.Column="1"]', $dns)
if (-not $titleColumn) { throw '列表条目必须在图标右侧显示标题区域。' }
$titleBindings = @($titleColumn.SelectNodes('ui:TextBlock', $dns) | ForEach-Object { $_.Text })
if ($titleBindings.Count -ne 2 -or $titleBindings[0] -ne '{x:Bind Title}' -or $titleBindings[1] -ne '{x:Bind ReleaseDate}') {
    throw '列表主标题必须显示版本名称，且发布时间位于主标题下方。'
}
$icon = $template.SelectSingleNode('ui:Grid/ui:Border[1]/ui:FontIcon', $dns)
if (-not $icon) { throw '列表条目左侧必须包含图标。' }
$typeBadge = $template.SelectSingleNode('.//ui:TextBlock[@Text="{x:Bind Kind}"]', $dns)
if (-not $typeBadge) { throw '列表条目应显示版本类型标签。' }

# 每个 Tab 标签都必须在分类映射中存在；游戏分类的真实数据由服务获取。
$catalog = Get-Content (Join-Path $project 'Views/DownloadCatalog.cs') -Raw -Encoding utf8
foreach ($tag in ($tabs | ForEach-Object { $_.Tag })) {
    if (-not $catalog.Contains("[""$tag""]")) {
        throw "演示数据缺少分类：$tag"
    }
}

# 设置页复用相同 Tab 控件，源策略独立，滑块范围明确，不加入目标文件夹。
[xml]$settings = Get-Content (Join-Path $project 'Views/SettingsView.xaml') -Raw -Encoding utf8
$sns = [System.Xml.XmlNamespaceManager]::new($settings.NameTable)
$sns.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$sns.AddNamespace('x', $xns)
$settingTabs = $settings.SelectNodes('//ui:SelectorBar/ui:SelectorBarItem', $sns)
if (($settingTabs | ForEach-Object { $_.Text }) -join ',' -cne '通用,下载') {
    throw '设置页必须使用通用、下载两个 Tab。'
}
foreach ($name in @('FileSource', 'VersionSource')) {
    $source = $settings.SelectSingleNode("//ui:ComboBox[@x:Name='$name']", $sns)
    if ($source.SelectNodes('ui:ComboBoxItem', $sns).Count -ne 3) {
        throw "源策略必须提供三个选项：$name"
    }
}
$connections = $settings.SelectSingleNode("//ui:Slider[@x:Name='Connections']", $sns)
$speed = $settings.SelectSingleNode("//ui:Slider[@x:Name='Speed']", $sns)
if ($connections.Minimum -ne '1' -or $connections.Maximum -ne '64' -or
    $speed.Minimum -ne '0' -or $speed.Maximum -ne '100') {
    throw '下载限制滑块范围与存储契约不一致。'
}
if ($settings.OuterXml.Contains('目标文件夹') -or $settings.OuterXml.Contains('选择文件夹')) {
    throw '设置页不应添加文件夹选项。'
}

# 检查 XAML 引用的本地图片资源是否存在。
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
Write-Output '通过：XAML 语法、导航与账户菜单、页面标题移除、下载页结构、设置 Tab/源策略/滑块范围及图片资源。'
