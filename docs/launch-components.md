# 启动兼容组件

组件随应用的 `Assets/Launch` 分发，构建不另行下载。固定文件名和 SHA-256 由 [LaunchCompatibility](../src/NyxLauncher/Services/LaunchCompatibility.cs) 维护，实际 JAR 与完整许可在[组件目录](../src/NyxLauncher/Assets/Launch)。文档不复制哈希表，升级时必须同时核对来源、许可、文件、代码校验值和相关测试。

| 组件 | 来源与许可 | 当前接入边界 |
| --- | --- | --- |
| Java Launch Wrapper | [上游项目](https://github.com/00ll00/java_launch_wrapper)，[MIT 许可](../src/NyxLauncher/Assets/Launch/JavaLaunchWrapper.LICENSE.txt) | Java 8–18 且未禁用时接入，保留原主类和参数 |
| LWJGL Unsafe Agent | [上游项目](https://github.com/HMCL-dev/lwjgl-unsafe-agent)，[Apache-2.0 许可](../src/NyxLauncher/Assets/Launch/LwjglUnsafeAgent.LICENSE.txt) | Java 25+ 且包含受影响的 LWJGL 3.4.0 / 3.4.1 时接入 |

Wrapper 用于旧 Java 的 Windows 命令行编码兼容，不能视为所有模块路径和 Java Agent 组合的兼容保证；模块化 Forge 主类包含专门的反射访问处理。LWJGL Agent 不替换游戏库。组件缺失或校验失败时中止启动，不运行未经验证的 JAR；遇到兼容问题可在对应版本高级设置中禁用。

高性能 GPU 选项为实际 Java 路径写入当前 Windows 用户图形偏好，关闭时恢复该字段为系统默认并保留其他字段。共用 Java 的版本在各自启动时应用偏好，硬件和驱动决定最终选择。

检查由 [AdvancedLaunchChecks](../tests/NyxLauncher.Checks/AdvancedLaunchChecks.cs) 覆盖配置继承、参数、组件条件与 GPU 字段。业务断言不替代完整游戏运行、中文路径、实际 Java 加载及双显卡硬件验证，执行方式见[验证指南](testing.md)。
