# 验证指南

## 常规检查

在仓库根目录执行；SDK 和应用构建配置见[开发指南](development.md)。

```powershell
dotnet run --project tests/NyxLauncher.Checks
pwsh -File scripts/Test-Prototype.ps1
dotnet build NyxLauncher.slnx -c Debug -p:Platform=x64
```

业务检查使用合成数据及临时目录，默认不执行真实游戏安装或登录；首次依赖还原仍可能联网，部分检查依赖 Windows 能力。测试项目使用示例认证配置，不需要开发者真实 ClientId。结构检查不能替代真实控件交互、动画或视觉验收，Debug 构建不能替代 Release/发布验证。

检查分派以 [Program.cs](../tests/NyxLauncher.Checks/Program.cs) 为准；下表帮助选择入口，不复制测试实现。参数接在 `dotnet run --project tests/NyxLauncher.Checks --` 后。某些入口提前返回，另一些先运行相关离线检查，不保证只执行单一场景。

## 定向检查

| 参数 | 范围及副作用 |
| --- | --- |
| `--launcher` | 协议修订和确认持久化、SemVer、GitHub 正式发布字段与地址、无发布/限流/取消、SHA-256、更新锁、替换回滚；仅临时文件和 HTTP 夹具 |
| `--modpack-export` | 三格式导出往返、选择与半选、资源策略、网络失败、路径和覆盖保护、取消清理；使用临时文件及 HTTP 夹具 |
| `--theme` | 外观配置往返、明暗配色独立、边界校验、损坏与写入失败保护 |
| `--wardrobe` | 衣柜副本与索引、PNG 校验、账户限制、官方请求格式与模型投影；只使用合成纹理和协议夹具 |
| `--servers` | 服务器配置重读、精选地址保护、现代及旧版直连参数、SRV 回退与取消、回环 TCP 状态探测；不访问公服或运行游戏 |
| `--advanced-launch` | 内存之外的高级配置、参数与兼容组件规则 |
| `--mod-management` | 模组目录、启停、批量摘要、兼容更新、禁用状态保留、冲突及失败保护；仅使用临时文件和协议夹具 |
| `--connection-pool` | 本地回环连接复用及网络诊断；需要允许监听/连接回环端口 |
| `--microsoft-callback` | 真实 MSAL 与本机 TCP 回调，使用合成授权码核对 PKCE 和无效回调；不打开浏览器或交换真实用户令牌 |
| `--microsoft-launch-arguments <验收目录>` | 复用已安装验收版本，以合成会话构造参数，不启动游戏 |
| `--local-resources <样本目录>` | 读取测试源码指定文件名的样本，输出写入临时目录，不改动样本；不是任意目录批量扫描，默认检查已覆盖合成资源 |
| `--java-scan <验收目录>` | 扫描本机 Java，结果写入验收目录，不改用户 Java 配置 |

## 协议、首页图片与单文件更新

`Release.targets` 使用正式窗口和控件，以临时协议、外观配置及合成游戏/账户验证首次弹窗不默认同意、拒绝不保存、勾选后才能确认、GitHub 协议链接，以及首页自定义图、移除、恢复默认和源图保护。它用正式下载管理器及本地 HTTP 夹具替换**自身的测试 EXE**，断言下载任务完成、备份保留、进程和窗口继续存在，并核对公共微软配置已经内嵌。仅测试产物被替换，用户实际配置和启动器不被替换；生成 `release-checks.log` 和完整新版首页的 `home-readme.png`。这不能代替 GitHub Actions 云端执行或真实 Release 下载验收。

```powershell
$releaseTargets = Join-Path $PWD 'tests/NyxLauncher.UiChecks/Release.targets'
$releaseOutput = Join-Path $PWD '.local/release-ui/'
$releaseIntermediate = Join-Path $PWD '.local/release-ui-obj/'
$releaseBuild = Join-Path $PWD '.local/release-ui-build/'
$releaseConfig = Join-Path $PWD 'config/microsoft-auth.release.json'
dotnet publish src/NyxLauncher/NyxLauncher.csproj -c Release -p:Platform=x64 -p:PublishProfile=SingleFile "-p:MicrosoftAuthConfig=$releaseConfig" "-p:CustomAfterMicrosoftCommonTargets=$releaseTargets" "-p:IntermediateOutputPath=$releaseIntermediate" "-p:OutputPath=$releaseBuild" -o $releaseOutput
if ($LASTEXITCODE -ne 0) { throw '发布界面检查构建失败' }
$releaseProcess = Start-Process (Join-Path $releaseOutput 'NyxLauncher.exe') -Wait -PassThru -WindowStyle Hidden
Get-Content (Join-Path $releaseOutput 'release-checks.log')
if ($releaseProcess.ExitCode -ne 0) { throw '发布界面检查失败' }
```

测试完成后退出前不清理仍在运行的 `.previous`。若重跑同一输出，可在旧进程已退出后清理该测试备份，或改用新的输出目录；请勿将此测试 EXE 当正式发布版分发。

## 主导航转场检查

以下入口使用真实 WinUI 窗口检查主导航各页面，以及设置、下载（含任务）、版本详情、衣柜的 Tab，覆盖动画中间态、重复进入、快速打断和卸载恢复；读取本机配置，主页面可能请求在线目录，循环切页时服务器页首次进入时会自动查询本机已登记的服务器（检查不等待结果，离开页面即取消），但不安装游戏或保存用户偏好。关于页追加检查设置 Tab 入口、首次延迟创建、版本与依赖绑定、GitHub 协议链接、无效链接报错、分段往返滚动方向与位置、内容高度稳定及键盘聚焦自动滚动，并在输出目录保存浅深色截图，不实际打开浏览器。结构脚本另核对第三方清单与项目直接依赖及已还原的 NuGet 分发依赖图（排除仅用于构建的 ILLink 工具）。Tab 检查不注入业务服务。动画开启时检查淡入与位移，关闭时检查直接显示；覆盖两个分支需分别在对应的 Windows 动画设置下运行。

```powershell
$navigationTargets = Join-Path $PWD 'tests/NyxLauncher.UiChecks/Navigation.targets'
$navigationOutput = Join-Path $PWD '.local/navigation-checks/'
dotnet build src/NyxLauncher/NyxLauncher.csproj -c Debug -p:Platform=x64 "-p:CustomAfterMicrosoftCommonTargets=$navigationTargets" "-p:OutputPath=$navigationOutput"
if ($LASTEXITCODE -ne 0) { throw '导航检查构建失败' }
$navigationProcess = Start-Process (Join-Path $navigationOutput 'NyxLauncher.exe') -Wait -PassThru
Get-Content (Join-Path $navigationOutput 'navigation-checks.log')
if ($navigationProcess.ExitCode -ne 0) { throw '导航转场检查失败' }
```

只验收关于页时，使用相同构建，将启动命令增加 `-ArgumentList '--about-only'`；仍检查设置 Tab 入口、延迟创建、协议状态、清单绑定和滚动，并生成浅深色及依赖列表截图，跳过其他页面的动画时序采样。

只验收服务器页时使用同一构建，将启动命令改为 `-ArgumentList '--servers-only'`。它在正式主页面里把服务器配置路径和状态探测替换为临时目录与桩实现，并用带标题栏行的窗口根模拟正式窗口；检查空列表的图标、说明文字和直接添加按钮及其主色、首次导航或切换到新标签时自动查询、返回时复用本次缓存、补查中断的地址、手动更新缓存，以及添加服务器后在其他请求仍进行时自动查询新地址、精选菜单、`Waterfall 1.12.2-1.20.4` 这类长版本号在单行布局与窄窗口换行布局下都完整显示；再用临时版本清单检查启动与编辑共用的版本卡片弹窗：只显示当前目录实例，切换目录时清空旧卡片并保留全局选择，已有默认绑定仍从对应目录启动。卡片带实例图标、版本名、副标题小字和默认标记，内容宽度不超过弹窗内容区以免裁切关闭按钮，单击卡片立即启动、临时选择不写回默认版本、默认实例缺失时由弹窗补选，以及添加表单遮罩覆盖侧栏并位于标题栏下方、版本行在弹窗上方选择、无已安装版本时可保存、未绑定时启动再选、默认版本可清除、精选表单隐藏地址和取消不写入；删除确认显示名称、地址和影响，取消保留记录，确认只移除目标记录。检查保存浅色空状态、深色空状态、自定义卡片、精选列表、窄窗口、版本卡片弹窗、添加表单和删除确认截图；不访问公服、不写用户配置。

该检查验证实际动画属性和最终状态，不替代不同显示器刷新率及低性能设备上的流畅度验收。

## 背景文字恢复检查

在 Windows 桌面会话中执行以下命令，使用独立窗口和正式控件模板验证浅深色下添加背景、移除背景及不透明度降为零后的前景恢复，同时检查分类标签悬停、按下和选中状态、宽图实际绘制范围超出窗口布局槽时的取样坐标，以及可编辑筛选框选区的主题主色。交互检查通过切换真实模板视觉状态执行，不替代鼠标端到端验收。此入口不打开业务页面、不修改用户配置；结果写入独立输出目录的 `contrast-checks.log`，失败返回非零退出码。

```powershell
$contrastTargets = Join-Path $PWD 'tests/NyxLauncher.UiChecks/BackgroundContrast.targets'
$contrastOutput = Join-Path $PWD '.local/contrast-checks/'
dotnet build src/NyxLauncher/NyxLauncher.csproj -c Debug -p:Platform=x64 "-p:CustomAfterMicrosoftCommonTargets=$contrastTargets" "-p:OutputPath=$contrastOutput"
if ($LASTEXITCODE -ne 0) { throw '背景文字检查构建失败' }
$contrastProcess = Start-Process (Join-Path $contrastOutput 'NyxLauncher.exe') -Wait -PassThru
Get-Content (Join-Path $contrastOutput 'contrast-checks.log')
if ($contrastProcess.ExitCode -ne 0) { throw '背景文字恢复检查失败' }
```

## 自定义配色检查

在 Windows 桌面会话中使用临时外观配置和正式设置控件验证自定义配色的三行布局、取色交互与持久化：每行整行可点击，左侧为色调、主色或副色名称，右侧为 48×26 圆角色块；打开取色器后拖动颜色只实时预览共享画刷，不写入临时 `theme.json`，关闭取色器才保存当前明暗组，另一组保持不变，界面不再显示“应用”按钮。检查同时保存浅色与深色截图。此入口用临时存储替换共享外观服务，不写入用户配置，不替代真实鼠标拖动取色和不同缩放比例下的视觉验收；结果写入独立输出目录的 `appearance-checks.log`，失败返回非零退出码。

```powershell
$appearanceTargets = Join-Path $PWD 'tests/NyxLauncher.UiChecks/Appearance.targets'
$appearanceOutput = Join-Path $PWD '.local/appearance-checks/'
dotnet build src/NyxLauncher/NyxLauncher.csproj -c Debug -p:Platform=x64 "-p:CustomAfterMicrosoftCommonTargets=$appearanceTargets" "-p:OutputPath=$appearanceOutput"
if ($LASTEXITCODE -ne 0) { throw '自定义配色检查构建失败' }
$appearanceProcess = Start-Process (Join-Path $appearanceOutput 'NyxLauncher.exe') -Wait -PassThru
Get-Content (Join-Path $appearanceOutput 'appearance-checks.log')
if ($appearanceProcess.ExitCode -ne 0) { throw '自定义配色检查失败' }
```

## 陶瓦联机检查

运行 `dotnet run --project tests/NyxLauncher.Checks -- --terracotta`，验证局域网通告和陶瓦房间成员解析。完整联机验收使用两个已启动 Minecraft Java 版的客户端，检查下载提示、通用设置中的模块管理、房主邀请码、访客弹窗和直连地址、双方的玩家列表，以及返回首页后房间仍在运行。关闭房间或退出游戏后确认访客断开，并在通用设置分别导出运行中和停止后的陶瓦日志。

## 实网与安装检查

### 整合包导出界面

`tests/NyxLauncher.UiChecks/ModpackExport.targets` 使用临时游戏目录验证真实控件的目录展开、父子勾选和半选、资源选项互斥、格式限制、实际 ZIP 生成、成功 Toast 和离开页释放文件树，并保存浅深色截图。它不调用系统保存对话框或打开资源管理器，不替代真实平台上传与第三方启动器导入验收。

```powershell
$exportTargets = Join-Path $PWD 'tests/NyxLauncher.UiChecks/ModpackExport.targets'
$exportOutput = Join-Path $PWD '.local/export-checks/'
dotnet build src/NyxLauncher/NyxLauncher.csproj -c Debug -p:Platform=x64 "-p:CustomAfterMicrosoftCommonTargets=$exportTargets" "-p:OutputPath=$exportOutput"
if ($LASTEXITCODE -ne 0) { throw '导出界面检查构建失败' }
$exportProcess = Start-Process (Join-Path $exportOutput 'NyxLauncher.exe') -Wait -PassThru -WindowStyle Hidden
Get-Content (Join-Path $exportOutput 'export-checks.log')
if ($exportProcess.ExitCode -ne 0) { throw '导出界面检查失败' }
```

### 首页界面

`tests/NyxLauncher.UiChecks/Home.targets` 替换应用入口，用临时目录中的合成版本与离线账户加载真实首页，检查默认窗口与最大化下的画面、控制台和启动按钮尺寸，版本选择器列表，启动进度接管选择器并交还，窄窗口隐藏次要按钮，以及没有已安装版本时的空选择器与禁用启动按钮；同时确认首页没有账户、Java 或目录文字。截图写入输出目录，不读取或修改用户的游戏目录与账户配置。

```powershell
$homeTargets = Join-Path $PWD 'tests/NyxLauncher.UiChecks/Home.targets'
$homeOutput = Join-Path $PWD '.local/home-checks/'
dotnet build src/NyxLauncher/NyxLauncher.csproj -c Debug -p:Platform=x64 "-p:CustomAfterMicrosoftCommonTargets=$homeTargets" "-p:OutputPath=$homeOutput"
if ($LASTEXITCODE -ne 0) { throw '首页界面检查构建失败' }
$homeProcess = Start-Process (Join-Path $homeOutput 'NyxLauncher.exe') -Wait -PassThru
Get-Content (Join-Path $homeOutput 'home-checks.log')
if ($homeProcess.ExitCode -ne 0) { throw '首页界面检查失败' }
```

### 衣柜原生界面

`tests/NyxLauncher.UiChecks/Wardrobe.targets` 替换应用入口，在隔离目录中建立测试身份和皮肤，检查真实控件的账户过滤、卡片选择、搜索、应用中状态、成功后原地更新与选择保留、头像同步、主色上传按钮悬停、右下角提示、走动暂停、缩放边界、相机复位及前后台生命周期；通过 `RenderTargetBitmap` 保存浅深色页面。此检查不替代真实鼠标拖动或系统文件选择器验收。

```powershell
$wardrobeTargets = Join-Path $PWD 'tests/NyxLauncher.UiChecks/Wardrobe.targets'
$wardrobeOutput = Join-Path $PWD '.local/wardrobe-checks/app/'
dotnet build src/NyxLauncher/NyxLauncher.csproj -c Debug -p:Platform=x64 "-p:CustomAfterMicrosoftCommonTargets=$wardrobeTargets" "-p:OutputPath=$wardrobeOutput"
if ($LASTEXITCODE -ne 0) { throw '衣柜检查构建失败' }
$wardrobeProcess = Start-Process (Join-Path $wardrobeOutput 'NyxLauncher.exe') -Wait -PassThru
Get-Content (Join-Path $wardrobeOutput 'wardrobe-checks.log')
if ($wardrobeProcess.ExitCode -ne 0) { throw '衣柜界面检查失败' }
```

运行验收程序前显式设置 `NYX_WARDROBE_LIVE=1` 可追加真实微软账户读取：使用正式嵌入配置静默认证，刷新本地认证缓存并加载真实皮肤和全部披风，但不修改远端穿戴。需要已有有效微软账户，截图可能包含玩家名称及纹理，产物只保留在忽略目录。完成后移除该环境变量。远端应用仍需另行验证，协议夹具通过不代表已更改真实账户。

衣柜分类回归需往返切换皮肤与披风，断言列表内容、选择清除和预览的两个纹理槽；不能只检查卡片数量或仅设置子项 `IsSelected`。同时检查加载结束及离开页面后转圈停止。真实鼠标验收应覆盖选中卡片后切换分类再选卡片，确认不会把披风渲染到人物皮肤上。

### 真实窗口内存生命周期

`tests/NyxLauncher.UiChecks/MemoryLifecycle.targets` 替换验收入口，使用正式资源与真实主窗口执行三轮在线模组详情→侧栏首页，检查启动只创建首页、首次跨模块分类、依赖返回、发布列表弱引用回收，以及另一窗口获得焦点后的采样暂停、隐藏详情自动回收与恢复。采样结束后补验账户首次打开、版本管理入口、详情返回停止采样、版本详情复用及精确文件筛选跳转；没有本地版本时使用不落盘的版本夹具。读取本机配置但不安装游戏或保存偏好；需要桌面会话与 Modrinth 网络访问，运行期间请勿操作验收窗口。

```powershell
$memoryTargets = Join-Path $PWD 'tests/NyxLauncher.UiChecks/MemoryLifecycle.targets'
$memoryOutput = Join-Path $PWD '.local/memory-checks/'
dotnet build src/NyxLauncher/NyxLauncher.csproj -c Release -p:Platform=x64 "-p:CustomAfterMicrosoftCommonTargets=$memoryTargets" "-p:OutputPath=$memoryOutput"
if ($LASTEXITCODE -ne 0) { throw '内存检查构建失败' }
$memoryProcess = Start-Process (Join-Path $memoryOutput 'NyxLauncher.exe') -Wait -PassThru
Get-Content (Join-Path $memoryOutput 'memory-checks.log')
if ($memoryProcess.ExitCode -ne 0) { throw '内存生命周期检查失败' }
```

日志分别记录工作集、私有提交与托管堆。`home-collected` 阶段显式 GC 用于检查可达引用，不代表正常前台回收时机；`background` 阶段由产品自己的失焦策略运行。比较前后版本时使用同一配置、同一驱动和资源数据，不能把工作集下降等同于全部内存泄漏消失。`NYX_MEMORY_BASELINE=1` 仅用于旧版本基线记录，会关闭回归断言，不应用于优化验收。

下列检查必须显式选择。带目录参数的入口使用专用、可丢弃的验收目录，不能指向日常游戏目录、真实存档或已有工作文件；运行前查看对应测试实现。它们可能下载大量资源、启动进程或保留产物，不属于文档修改的默认验证。

| 参数 | 范围及前置条件 |
| --- | --- |
| `--live` | 官方与镜像游戏版本清单 |
| `--live-loaders` / `--live-fabric` / `--live-loader-lists` | 加载器可用性、特定 Fabric 场景或分源版本列表 |
| `--resources-live` | 资源目录查询，区别于下面的真实资源文件下载 |
| `--live-install <验收目录>` | 安装测试定义的原版、Fabric、Forge、NeoForge 客户端 |
| `--live-launch <验收目录>` | 依赖上一项创建的四类验收版本；临时损坏文件检查修复，再真实启动、检测窗口、停止和取消 |
| `--live-fabric-api <验收目录>` | 安装隔离/共享目录场景，并启动其中的游戏验证 API |
| `--live-mod-install <验收目录>` | 创建最小版本夹具并下载真实模组，不启动游戏 |
| `--live-resources <验收目录>` | 获取资源包、光影和数据包发布并校验真实下载 |
| `--live-modpacks <验收目录>` | 安装三种格式的最小整合包，保留安装结果 |
| `--mcim-live` | 此测试进程直连并限定镜像，防止官方回退掩盖故障；下载文件至临时目录后清理 |
| `--java-catalog <验收目录>` / `--java-live <验收目录>` | 三渠道 Java 列表；后者还安装发行包并执行 Java 版本检查 |
| `--external-live <验收目录>` | 已安装验收版本的参数构造、LittleSkin 地址发现和注入器下载，不启动游戏 |
| `--external-avatar` | 读取首个已登录外置角色的公开皮肤，不解密令牌；需要已有账户 |

安装与启动入口支持的 `--direct` 仅改变测试进程代理行为，不是应用默认设置；支持范围以入口源码为准，不要据此修改系统代理或关闭 TLS 校验。

## 结果与证据

优先运行受影响的现有检查；通过后仅因新修改、失败或未解决疑点扩大范围。报告命令、结果、环境及未验证项，遇到网络或漏洞数据源失败应单独说明，不能写成安全检查通过。

真实 UI 或游戏验证记录触发步骤、实际行为及恢复情况。只启动进程不能证明窗口、存档或模组效果；仅验证参数不能证明真实登录；一次游戏/加载器组合成功不能代表全部版本。

本机截图、临时驱动、下载产物与详细流水放入 Git 忽略的本地目录，不向共享文档追加机器路径和固定通过数。可复用的回归检查进入测试项目，必要结论放入对应评审或提交说明。
