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
| `--theme` | 外观配置往返、明暗配色独立、边界校验、损坏与写入失败保护 |
| `--wardrobe` | 衣柜副本与索引、PNG 校验、账户限制、官方请求格式与模型投影；只使用合成纹理和协议夹具 |
| `--advanced-launch` | 内存之外的高级配置、参数与兼容组件规则 |
| `--mod-management` | 模组目录、启停、批量摘要、兼容更新、禁用状态保留、冲突及失败保护；仅使用临时文件和协议夹具 |
| `--connection-pool` | 本地回环连接复用及网络诊断；需要允许监听/连接回环端口 |
| `--microsoft-callback` | 真实 MSAL 与本机 TCP 回调，使用合成授权码核对 PKCE 和无效回调；不打开浏览器或交换真实用户令牌 |
| `--microsoft-launch-arguments <验收目录>` | 复用已安装验收版本，以合成会话构造参数，不启动游戏 |
| `--local-resources <样本目录>` | 读取测试源码指定文件名的样本，输出写入临时目录，不改动样本；不是任意目录批量扫描，默认检查已覆盖合成资源 |
| `--java-scan <验收目录>` | 扫描本机 Java，结果写入验收目录，不改用户 Java 配置 |

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

## 实网与安装检查

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
