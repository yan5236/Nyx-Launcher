# 愚人节版本分类

分类数据只维护在 [AprilFoolsVersions](../src/NyxLauncher/Services/AprilFoolsVersions.cs)，文档不再复制完整 ID 表。匹配使用完整 ID 且区分大小写，不按日期或版本号前缀推断。

[VersionCatalogService](../src/NyxLauncher/Services/VersionCatalogService.cs) 只对远程清单真实存在的条目覆盖分类，不补造版本和下载地址。普通版本即使在四月一日发布也保持原类型；音效、宣传活动、纹理和屏保不作为独立游戏版本加入。

维护时核对官方发布说明和版本清单，再更新数据集合与[对应检查](../tests/NyxLauncher.Checks/Program.cs)。检查精确匹配、普通四月一日版本不误判、去重和排序；具体入口见[验证指南](testing.md)。
