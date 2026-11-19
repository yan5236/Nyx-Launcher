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
if ($navigation.IsPaneToggleButtonVisible -ne 'False' -or $navigation.PaneOpening -ne 'Navigation_PaneOpening' -or
    $navigation.PaneClosing -ne 'Navigation_PaneClosing') {
    throw '侧栏必须支持左上角折叠按钮并同步账户入口布局。'
}
[xml]$window = Get-Content (Join-Path $project 'MainWindow.xaml') -Raw -Encoding utf8
$wns = [System.Xml.XmlNamespaceManager]::new($window.NameTable)
$wns.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$titleBar = $window.SelectSingleNode('//ui:TitleBar', $wns)
if ($titleBar.IsPaneToggleButtonVisible -ne 'True' -or $titleBar.PaneToggleRequested -ne 'TitleBar_PaneToggleRequested') {
    throw '折叠按钮必须位于窗口标题栏，侧栏内部不重复显示。'
}
$items = $navigation.SelectNodes('ui:NavigationView.MenuItems/ui:NavigationViewItem', $ns)
if (($items | ForEach-Object { $_.Content }) -join ',' -cne '首页,版本管理,下载,衣柜,服务器,联机') {
    throw '主导航必须依次为首页、版本管理、下载、衣柜、服务器、联机。'
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

# 首页启动台：世界画面承托固定尺寸控制台，版本用选择器切换，不显示账户、Java 或目录等信息。
[xml]$homeDoc = Get-Content (Join-Path $project 'Views/HomeView.xaml') -Raw -Encoding utf8
$homeNs = [System.Xml.XmlNamespaceManager]::new($homeDoc.NameTable)
$homeNs.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$homeNs.AddNamespace('x', $xns)
$homeNs.AddNamespace('views', 'using:NyxLauncher.Views')
$launch = $homeDoc.SelectSingleNode("//ui:Button[@x:Name='LaunchButton']", $homeNs)
if (-not $launch -or $launch.Width -ne '196' -or $launch.Height -ne '56' -or
    $launch.Style -ne '{StaticResource DeepButtonStyle}' -or -not $launch.SelectSingleNode('ui:StackPanel', $homeNs)) {
    throw '首页启动按钮必须固定尺寸并复用主色样式，最大化后不能拉伸。'
}
$selector = $homeDoc.SelectSingleNode("//views:ScrollSafeComboBox[@x:Name='VersionList']", $homeNs)
if (-not $selector -or $selector.SelectionChanged -ne 'Version_SelectionChanged' -or
    -not $selector.SelectSingleNode('ui:ComboBox.ItemTemplate', $homeNs)) {
    throw '首页版本选择必须使用带项目模板且滚轮安全的 ScrollSafeComboBox。'
}
if (-not $homeDoc.SelectSingleNode("//ui:ProgressBar[@x:Name='StageProgress']", $homeNs) -or
    -not $homeDoc.SelectSingleNode("//ui:TextBlock[@x:Name='StageLabel']", $homeNs)) {
    throw '首页必须显示启动阶段和进度。'
}
if ($homeDoc.SelectSingleNode("//ui:ToggleButton|//ui:ToggleSwitch", $homeNs)) {
    throw '首页不应放置装饰性开关。'
}
foreach ($word in @('Java', '账户', '游戏目录', '星夜', '星空')) {
    if ($homeDoc.OuterXml.Contains($word)) { throw "首页不应显示与启动无关的信息：$word" }
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
    throw '下载页 Tab 必须提供与真实资源分类对应的标签。'
}
$search = $downloads.SelectSingleNode('//ui:AutoSuggestBox', $dns)
if (-not $search -or $search.GetAttribute('TextChanged') -ne 'SearchBox_TextChanged') {
    throw 'Tab 栏下方必须提供可过滤列表的 AutoSuggestBox 搜索框。'
}
if ($search.ParentNode.ParentNode.LocalName -ne 'TabContentPanel' -or
    $search.ParentNode.ParentNode.GetAttribute('Grid.Row') -ne '1' -or
    $search.ParentNode.GetAttribute('Grid.Row') -notin @('', '0')) {
    throw '搜索框必须位于 Tab 栏下方的第二行。'
}
$combo = $downloads.SelectSingleNode("//*[local-name()='ScrollSafeComboBox']", $dns)
if (-not $combo) { throw '搜索框右侧必须提供类型筛选选择器。' }
if (-not [object]::ReferenceEquals($combo.ParentNode, $search.ParentNode) -or $combo.GetAttribute('Grid.Column') -ne '1') {
    throw '类型筛选必须与搜索框同一行并位于其右侧。'
}
$types = @($combo.SelectNodes('ui:ComboBoxItem', $dns) | ForEach-Object { $_.Content })
if (($types) -join ',' -cne '全部类型,正式版,快照版,远古版,愚人节版') {
    throw '类型筛选必须包含全部类型、正式版、快照版、远古版、愚人节版。'
}
$list = $downloads.SelectSingleNode('//ui:ListView', $dns)
if (-not $list) { throw '版本内容必须使用 ListView 呈现。' }
if (-not [object]::ReferenceEquals($list.ParentNode.ParentNode, $search.ParentNode.ParentNode) -or
    $list.ParentNode.GetAttribute('Grid.Row') -ne '1') {
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

# 资源目录使用独立的提交式搜索和筛选，复用游戏的加载状态控件。
[xml]$resources = Get-Content (Join-Path $project 'Views/ResourceCatalogView.xaml') -Raw -Encoding utf8
$rns = [System.Xml.XmlNamespaceManager]::new($resources.NameTable)
$rns.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$rns.AddNamespace('x', $xns)
$resourceSearch = $resources.SelectSingleNode('//ui:TextBox[@x:Name="SearchBox"]', $rns)
if (-not $resourceSearch -or $resourceSearch.HasAttribute('TextChanged') -or
    -not $resources.SelectSingleNode('//ui:Button[@Content="搜索" and @Click="Search_Click"]', $rns)) {
    throw '资源搜索必须由独立按钮或 Enter 提交，不能逐字发起请求。'
}
foreach ($name in @('GameVersionFilter', 'LoaderFilter', 'CategoryFilter', 'SourceFilter')) {
    if (-not $resources.SelectSingleNode("//*[local-name()='ScrollSafeComboBox'][@x:Name='$name']", $rns)) {
        throw "资源目录缺少筛选：$name"
    }
}
$versionFilter = $resources.SelectSingleNode("//*[local-name()='ScrollSafeComboBox'][@x:Name='GameVersionFilter']", $rns)
if ($versionFilter.IsEditable -ne 'True' -or $versionFilter.TextSubmitted -ne 'GameVersionFilter_TextSubmitted') {
    throw 'Minecraft 版本筛选必须保留下拉并允许提交手动输入。'
}
if (-not $resources.SelectSingleNode('//*[local-name()="VersionLoadStatus"]', $rns)) {
    throw '资源目录必须复用游戏的加载、失败及重试控件。'
}
if (-not $resources.SelectSingleNode('//ui:TextBlock[@Text="{x:Bind Description}"]', $rns) -or
    $resources.SelectNodes('//ui:InlineUIContainer//ui:FontIcon', $rns).Count -ne 4 -or
    -not $resources.SelectSingleNode('//ui:ItemsControl[@ItemsSource="{x:Bind LoaderIcons}"]', $rns)) {
    throw '资源卡片必须包含简介及下载量、加载器、版本、来源和更新日期图标。'
}
if (-not $resources.SelectSingleNode('//*[local-name()="ResourceIcon"]', $rns) -or
    $resources.SelectSingleNode('//ui:Border[@Width="64"]', $rns)) {
    throw '资源图标必须使用生命周期独立、无背景框的图标控件。'
}
foreach ($category in @('mod','modpack','resourcepack','datapack','shader')) {
    if (-not (Test-Path (Join-Path $project "Assets/resource-$category.svg"))) {
        throw "缺少分类占位图标：$category"
    }
}
if ($downloads.OuterXml.Contains('演示数据') -or (Test-Path (Join-Path $project 'Views/DownloadCatalog.cs'))) {
    throw '资源目录不能继续保留演示清单。'
}

# 设置页复用相同 Tab 控件，源策略独立，滑块范围明确，不加入目标文件夹。
[xml]$settings = Get-Content (Join-Path $project 'Views/SettingsView.xaml') -Raw -Encoding utf8
$sns = [System.Xml.XmlNamespaceManager]::new($settings.NameTable)
$sns.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$sns.AddNamespace('x', $xns)
$settingTabs = $settings.SelectNodes('//ui:SelectorBar/ui:SelectorBarItem', $sns)
if (($settingTabs | ForEach-Object { $_.Text }) -join ',' -cne '通用,启动,Java,下载,关于') {
    throw '设置页必须按通用、启动、Java、下载、关于顺序展示五个 Tab。'
}

# 核对关于页覆盖所有还原依赖及版本，升级、遗漏和重复条目都必须同步维护清单。
$notices = @(Get-Content (Join-Path $project 'Assets/ThirdPartyNotices.json') -Raw -Encoding utf8 | ConvertFrom-Json)
$packages = @($notices | ForEach-Object { $_.Packages })
if ($packages.Count -ne @($packages | Sort-Object -Unique).Count) { throw '第三方清单不能重复登记 NuGet 包。' }
foreach ($notice in $notices) {
    if (-not $notice.Name -or -not $notice.Version -or -not $notice.Description -or -not $notice.License) {
        throw '第三方清单必须包含名称、版本、用途和许可。'
    }
    foreach ($link in @($notice.ProjectUrl, $notice.LicenseUrl)) {
        $uri = $null
        if (-not [Uri]::TryCreate($link, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') {
            throw "第三方项目链接必须是绝对 HTTPS 地址：$link"
        }
    }
}
[xml]$appProject = Get-Content (Join-Path $project 'NyxLauncher.csproj') -Raw -Encoding utf8
foreach ($package in $appProject.Project.ItemGroup.PackageReference) {
    if ($package.Include -and "$($package.Include)/$($package.Version)" -notin $packages) {
        throw "第三方清单缺少直接依赖或版本过期：$($package.Include)"
    }
}
$assetsPath = Join-Path $project 'obj/project.assets.json'
if (Test-Path $assetsPath) {
    $assets = Get-Content $assetsPath -Raw -Encoding utf8 | ConvertFrom-Json
    # 单文件发布额外还原 ILLink 构建工具；它不随应用分发，不属于运行依赖致谢清单。
    $restoredPackages = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' -and $_.Name -notlike 'Microsoft.NET.ILLink.Tasks/*' } | ForEach-Object Name)
    if (Compare-Object $packages $restoredPackages) { throw '第三方清单与还原后的直接及间接 NuGet 依赖不一致。' }
}
foreach ($component in @('Java Launch Wrapper', 'LWJGL Unsafe Agent', 'authlib-injector', 'Skia')) {
    if ($component -notin $notices.Name) { throw "第三方清单缺少非 NuGet 组件：$component" }
}
foreach ($name in @('FileSource', 'VersionSource')) {
    $source = $settings.SelectSingleNode("//*[local-name()='ScrollSafeComboBox'][@x:Name='$name']", $sns)
    if ($source.SelectNodes('ui:ComboBoxItem', $sns).Count -ne 3) {
        throw "源策略必须提供三个选项：$name"
    }
}
$connections = $settings.SelectSingleNode("//ui:Slider[@x:Name='Connections']", $sns)
$parallel = $settings.SelectSingleNode("//*[@x:Name='DownloadPanel']//ui:Slider[@x:Name='ParallelDownloads']", $sns)
$speed = $settings.SelectSingleNode("//ui:Slider[@x:Name='Speed']", $sns)
if ($connections.Minimum -ne '1' -or $connections.Maximum -ne '64' -or
    $parallel.Minimum -ne '1' -or $parallel.Maximum -ne '64' -or $parallel.StepFrequency -ne '1' -or
    $speed.Minimum -ne '0' -or $speed.Maximum -ne '100') {
    throw '下载限制滑块范围与存储契约不一致。'
}
if ((Get-Content (Join-Path $project 'Views/DownloadTasksView.xaml') -Raw -Encoding utf8).Contains('ParallelDownloads')) {
    throw '并行下载滑块应位于设置的下载分组，不能放在下载任务列表。'
}
if ($settings.OuterXml.Contains('目标文件夹') -or $settings.OuterXml.Contains('选择文件夹')) {
    throw '设置页不应添加文件夹选项。'
}

# Java 主版本复用渠道页的普通按钮卡片，不保留列表选中状态，点选后直接前进。
[xml]$javaDialog = Get-Content (Join-Path $project 'Views/InstallJavaDialog.xaml') -Raw -Encoding utf8
$javaNs = [System.Xml.XmlNamespaceManager]::new($javaDialog.NameTable)
$javaNs.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$javaNs.AddNamespace('x', $xns)
$majorButtons = $javaDialog.SelectNodes('//ui:StackPanel[@x:Name="MajorPanel"]/ui:Button', $javaNs)
if ($javaDialog.DocumentElement.SecondaryButtonText -ne '' -or
    $javaDialog.DocumentElement.SecondaryButtonClick -ne 'Install_Click' -or
    ($majorButtons | ForEach-Object { $_.Tag }) -join ',' -cne '8,17,21,25' -or
    @($majorButtons | Where-Object { $_.Click -ne 'Major_Click' -or $_.HorizontalAlignment -ne 'Stretch' }).Count -ne 0 -or
    $javaDialog.SelectSingleNode('//ui:StackPanel[@x:Name="MajorPanel"]//ui:ListView', $javaNs)) {
    throw 'Java 主版本必须使用无选中状态的按钮卡片直接进入渠道，安装按钮只用于最终确认。'
}

# 安装配置页：编辑名称、右侧安装按钮、三个加载器图标及同一行灰色状态。
[xml]$install = Get-Content (Join-Path $project 'Views/InstallationView.xaml') -Raw -Encoding utf8
$ins = [System.Xml.XmlNamespaceManager]::new($install.NameTable)
$ins.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$ins.AddNamespace('x', $xns)
$name = $install.SelectSingleNode("//ui:TextBox[@x:Name='VersionName']", $ins)
$installButton = $install.SelectSingleNode("//ui:Button[@x:Name='InstallButton']", $ins)
if (-not $name -or $name.IsReadOnly -eq 'True' -or $name.GetAttribute('Grid.Column') -ne '1' -or
    $installButton.GetAttribute('Grid.Column') -ne '2' -or $installButton.IsEnabled -eq 'False' -or
    $installButton.GetAttribute('Click') -ne 'Install_Click' -or $installButton.GetAttribute('Style') -ne '{StaticResource DeepButtonStyle}' -or
    -not $installButton.SelectSingleNode('.//ui:FontIcon', $ins) -or
    -not $installButton.SelectSingleNode('.//ui:TextBlock[@Text="安装"]', $ins)) {
    throw '版本名称必须可编辑，右侧深色安装按钮应绑定实际安装操作。'
}
if ($install.SelectSingleNode("//ui:Button[@x:Name='RetryButton' or @Click='Retry_Click']", $ins)) {
    throw '安装配置页不应再显示重新检测按钮。'
}
foreach ($loader in @('Forge', 'Fabric', 'NeoForge')) {
    $title = $install.SelectSingleNode("//ui:TextBlock[@Text='$loader']", $ins)
    $status = $install.SelectSingleNode("//ui:TextBlock[@x:Name='${loader}Status']", $ins)
    if (-not $title -or -not $status -or -not [object]::ReferenceEquals($title.ParentNode, $status.ParentNode) -or
        $status.FontSize -ne '13' -or $status.Foreground -ne '{ThemeResource MutedBrush}' -or
        -not $title.ParentNode.SelectSingleNode('ui:FontIcon[@Grid.Column="3"]', $ins)) {
        throw "$loader 必须有同一行的灰色状态和右箭头。"
    }
    $asset = "ms-appx:///Assets/$($loader.ToLowerInvariant()).png"
    $image = $title.ParentNode.SelectSingleNode("ui:Image[@Source='$asset']", $ins)
    $card = $title.ParentNode.ParentNode
    if (-not $image -or $image.Width -ne '32' -or $image.Height -ne '32' -or
        $card.LocalName -ne 'Button' -or $card.MinHeight -ne '56' -or $card.Click -ne 'Loader_Click' -or $card.Tag -ne $loader) {
        throw "$loader 图标必须合并进紧凑列表卡片。"
    }
}

# 加载器与 API 共用无确认步骤的列表，普通绑定兼容两种发布模型的版本和日期/稳定性。
[xml]$dialog = Get-Content (Join-Path $project 'Views/LoaderVersionDialog.xaml') -Raw -Encoding utf8
$dialogNs = [System.Xml.XmlNamespaceManager]::new($dialog.NameTable)
$dialogNs.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
if ($dialog.DocumentElement.LocalName -ne 'ContentDialog' -or
    -not $dialog.SelectSingleNode('//ui:TextBlock[@Text="选择加载器版本"]', $dialogNs) -or
    -not $dialog.SelectSingleNode('//ui:Button[@Click="Close_Click"]', $dialogNs) -or
    -not $dialog.SelectSingleNode('//ui:ListView[@IsItemClickEnabled="True" and @ItemClick="Version_Click"]', $dialogNs) -or
    -not $dialog.SelectSingleNode('//ui:TextBlock[@Text="{Binding Version}"]', $dialogNs) -or
    -not $dialog.SelectSingleNode('//ui:TextBlock[@Text="{Binding Detail}"]', $dialogNs)) {
    throw '加载器弹窗必须包含关闭按钮和点选即关闭的真实版本列表。'
}
if ($list.IsItemClickEnabled -ne 'True' -or $list.ItemClick -ne 'EntryList_ItemClick' -or
    -not $install.SelectSingleNode("//ui:Button[@Click='Back_Click']", $ins)) {
    throw '安装配置页必须提供游戏条目进入和返回入口。'
}

# 账户弹窗保留原生键盘语义，确认在取消右侧，返回入口位于标题中。
[xml]$accountDialog = Get-Content (Join-Path $project 'Views/AddAccountDialog.xaml') -Raw -Encoding utf8
$accountNs = [System.Xml.XmlNamespaceManager]::new($accountDialog.NameTable)
$accountNs.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$accountNs.AddNamespace('x', $xns)
if ($accountDialog.DocumentElement.PrimaryButtonText -ne '取消' -or
    $accountDialog.DocumentElement.SecondaryButtonClick -ne 'Confirm_Click' -or
    -not $accountDialog.SelectSingleNode('//ui:ContentDialog.Title//ui:Button[@Click="Back_Click"]', $accountNs)) {
    throw '账户弹窗必须左取消、右确认，返回按钮在标题左侧。'
}
# 微软入口可触发真实授权，并提供取消期间的阶段展示；离线与外置入口由区域限制在运行时决定。
if (-not $accountDialog.SelectSingleNode('//ui:Button[@Click="Microsoft_Click" and not(@IsEnabled="False")]', $accountNs) -or
    -not $accountDialog.SelectSingleNode('//ui:ProgressRing[@IsActive="False"]', $accountNs)) {
    throw '微软账户必须具有可用入口及认证进度区域。'
}
if (-not $accountDialog.SelectSingleNode('//ui:InfoBar[@x:Name="RegionNotice" and @IsOpen="False" and @IsClosable="False"]', $accountNs) -or
    -not $accountDialog.SelectSingleNode('//ui:Button[@x:Name="OfflineButton" and not(@IsEnabled="False")]', $accountNs) -or
    -not $accountDialog.SelectSingleNode('//ui:Button[@x:Name="ExternalButton" and not(@IsEnabled="False")]', $accountNs)) {
    throw '账户弹窗必须提供运行时区域限制说明，离线与外置入口默认可用。'
}
if ($install.SelectSingleNode('//ui:Button[@x:Name="ApiButton"]', $ins).GetAttribute('Margin') -notin @('', '0')) {
    throw 'Fabric API 卡片必须与加载器卡片左侧对齐。'
}

# 危险区必须在基础页滚动内容末尾，删除使用独立按钮和事件，其他 Tab 不应显示它。
[xml]$details = Get-Content (Join-Path $project 'Views/VersionDetailsView.xaml') -Raw -Encoding utf8
$detailsNs = [System.Xml.XmlNamespaceManager]::new($details.NameTable)
$detailsNs.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$detailsNs.AddNamespace('x', $xns)
$danger = $details.SelectSingleNode('//ui:ScrollViewer[@x:Name="BasicPanel"]/ui:StackPanel/ui:StackPanel[last()]', $detailsNs)
if (-not $danger -or -not $danger.SelectSingleNode('ui:TextBlock[@Text="危险区"]', $detailsNs) -or
    -not $danger.SelectSingleNode('.//ui:Button[@Content="删除版本" and @Click="Delete_Click" and @AutomationProperties.Name="删除当前版本"]', $detailsNs)) {
    throw '基础页底部必须提供危险区和可访问的删除版本按钮。'
}

# 服务器页：版本选择必须是卡片弹窗，页面和表单都不再使用下拉选择器。
[xml]$servers = Get-Content (Join-Path $project 'Views/ServersView.xaml') -Raw -Encoding utf8
$srv = [System.Xml.XmlNamespaceManager]::new($servers.NameTable)
$srv.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$srv.AddNamespace('x', $xns)
$srv.AddNamespace('views', 'using:NyxLauncher.Views')
if ($servers.SelectSingleNode("//*[local-name()='ComboBox' or local-name()='ScrollSafeComboBox']", $srv)) {
    throw '服务器页版本选择不得使用下拉选择器，必须打开卡片弹窗。'
}
$editorLayer = $servers.SelectSingleNode("//ui:Grid[@x:Name='EditorLayer']", $srv)
$versionEntry = $servers.SelectSingleNode("//ui:Button[@x:Name='EditorVersion']", $srv)
if (-not $editorLayer -or -not $versionEntry -or $versionEntry.GetAttribute('Click') -ne 'EditorVersion_Click' -or
    -not $versionEntry.SelectSingleNode('.//views:VersionIcon', $srv)) {
    throw '服务器表单必须在页面上层打开，并提供带图标的版本入口。'
}
[xml]$versionDialog = Get-Content (Join-Path $project 'Views/ServerVersionDialog.xaml') -Raw -Encoding utf8
$vdns = [System.Xml.XmlNamespaceManager]::new($versionDialog.NameTable)
$vdns.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$vdns.AddNamespace('views', 'using:NyxLauncher.Views')
if ($versionDialog.DocumentElement.LocalName -ne 'ContentDialog' -or
    -not $versionDialog.SelectSingleNode('//ui:ListView[@IsItemClickEnabled="True" and @ItemClick="Version_Click"]', $vdns) -or
    -not $versionDialog.SelectSingleNode('//views:VersionIcon', $vdns) -or
    -not $versionDialog.SelectSingleNode('//ui:TextBlock[@Text="{x:Bind Name}"]', $vdns) -or
    -not $versionDialog.SelectSingleNode('//ui:TextBlock[@Text="{x:Bind Detail}"]', $vdns) -or
    -not $versionDialog.SelectSingleNode('//ui:TextBlock[@Text="默认"]', $vdns)) {
    throw '版本选择必须使用带图标、版本名、副标题小字和默认标记的卡片弹窗。'
}

# 下拉选择器必须使用 ScrollSafeComboBox：原生 ComboBox 收起时可能被滚轮改选，滚动页面时容易误操作。
$projectSources = Get-ChildItem $project -Recurse -Include '*.xaml', '*.cs' -File |
    Where-Object FullName -notmatch '\\(bin|obj)\\'
foreach ($file in $projectSources) {
    $raw = Get-Content $file.FullName -Raw -Encoding utf8
    if ($raw -match '<(\w+:)?ComboBox[ >]' -or $raw -match 'new\s+([\w.]+\.)?ComboBox\s*[\({]') {
        throw "下拉选择器必须使用 ScrollSafeComboBox，避免收起时滚轮改选：$($file.Name)"
    }
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
Write-Output '通过：XAML 语法、导航与账户菜单、页面标题移除、下载页结构、安装配置页、详情危险区、设置 Tab/源策略/滑块范围、账户区域限制说明、服务器版本卡片弹窗、选择器滚轮安全及图片资源。'

