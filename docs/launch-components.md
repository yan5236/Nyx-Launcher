# 启动兼容组件

以下构件不修改源码，随输出目录 `Assets/Launch` 分发，构建不另行下载。升级时核对上游兼容要求、许可、固定 SHA-256 及相关启动测试。

| 组件 | 固定来源 | SHA-256 | 许可 |
| --- | --- | --- | --- |
| Java Launch Wrapper 1.4.4 | [官方发布](https://github.com/00ll00/java_launch_wrapper/releases/tag/v1.4.4) | `88eb3ee58854e916437831697ef6a2fd493da9bc4ffb018f620cd88f090952aa` | MIT，Copyright (c) 2022 00ll00；完整文本在 `Assets/Launch/JavaLaunchWrapper.LICENSE.txt` |
| LWJGL Unsafe Agent 2.0 | [Maven Central](https://repo.maven.apache.org/maven2/org/glavo/lwjgl-unsafe-agent/2.0/) | `6c07d3508dc090a12076876bdc227a19ce195a41c9e2c6535c3f82213b1b2015` | Apache-2.0，Copyright 2026 Glavo；完整文本在 `Assets/Launch/LwjglUnsafeAgent.LICENSE.txt` |

[Wrapper 文档](https://github.com/00ll00/java_launch_wrapper)规定在 JVM 参数与主类之间插入 `-jar <wrapper>`，用于修复 Windows 上旧 Java 的命令行编码问题（JDK-8272352，Java 19 已修复）。本应用仅在 Java 8–18 且未禁用时插入，保留游戏原主类和参数。上游已知限制包括无法修复模块路径，以及依赖应用 classpath 的 Java Agent 在 Wrapper 修补前加载；遇到兼容问题可在对应版本禁用 Wrapper。

[Agent 文档](https://github.com/HMCL-dev/lwjgl-unsafe-agent)要求 Java 25+，主要修复 LWJGL 3.4.0–3.4.1 的性能问题。本应用仅在匹配版本时插入 `-javaagent:<agent>`；不替换游戏 LWJGL 库，不向旧 Java 注入。

Windows 图形偏好使用 `GpuPreference=2` 请求高性能 GPU，路径键做法可参考 [NVIDIA 官方文档](https://docs.omniverse.nvidia.com/usd/latest/usdview/quickstart.html)。不执行管理员级修改。

2026-10-31 验证：高级配置继承、局部覆盖、重置、损坏及写入失败保护、Windows 参数往返、GC 冲突、组件开关和 GPU 字段保留均通过业务检查；现有离线检查、XAML 结构与 x64 Debug 构建通过。临时 WinUI 验收验证两处卡片、局部保存、重开和恢复，全局配置原文件保持不变。Java 8 实际加载 Wrapper、Java 25 实际加载 Agent 的独立小程序通过，包含中文 classpath 与带空格参数。未验证 Release、完整游戏运行或双显卡硬件选择；NuGet 漏洞数据查询因网络失败出现 NU1900，不代表漏洞审计通过。
