# 候选依赖说明

开发安装包现在携带 THIRD-PARTY-NOTICES.zip。相同文件进入主 MSIX 的
ThirdParty/THIRD-PARTY-NOTICES.zip；因此安装后的应用包中也保留完整说明。
生成器位于 [ThirdPartyNotices.psm1](../../ClashSharp/Installer/ThirdPartyNotices.psm1)，
由正式与开发打包共用。它不操作安装、网络接管、证书存储或 NuGet 缓存。

## 输入与原文

输入范围是主程序、MihomoService、RecoveryWatchdog、WPF Installer 四个生产入口。
它们的直接和传递 NuGet 包来自实际 project.assets.json，并逐个对照项目锁文件。
不在普通 PackageReference 锁内的 SDK 下载依赖由独立的包名、准确版本和内容摘要固定。
构建依赖也保留在清单内，清单不会宣称每个包的每个文件都随应用分发。

读取实际 nupkg 时持有同一个只读文件句柄。签名包先调用 SDK 自带 NuGet 读取器的
内容完整性验证，再比较 NuGet 内容摘要；无签名包使用该读取器的内容摘要算法。
这项校验与对 ZIP 字节直接计算的 SHA-256 分别记录，因为两者的语义不同。
见 [NuGet 的 PackageArchiveReader 实现](https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.Packaging/PackageArchiveReader.cs)。
这里不增加另一套签名证书信任策略，依赖还原与最终发布签名仍各自负责原有边界。

许可、通知和 nuspec 均从已校验的原始包读取。缺少许可文件的依赖，必须准确匹配
ThirdParty/catalog.json 中的包名、版本与许可声明，才能使用固定来源文本。
目录内的七份外部资料保留原始字节、来源 URL、长度和摘要；Git 对原文禁用换行转换。
生成过程不从动态许可 URL 下载文件，也不会把某一依赖的 MIT 文本替换成另一作者的声明。

## 归档与打包

inventory.json 记录包身份、NuGet 内容 SHA-512、归档 SHA-256、声明许可、使用项目和
原始文档引用。文档按内容地址去重，RTF 保留可识别扩展名。清单不输出用户目录、
包缓存路径、还原源凭据或构建时间；固定排序和 ZIP 时间使相同输入生成相同字节。

打包先生成并读取检查归档，再把它提供给主 MSIX 构建。最终 MSIX 必须包含唯一且
名称准确的内部 ZIP，其字节摘要、长度、包数量及所有文档引用必须符合原始输入。
安装器目录中的副本在发布前再次校验，provenance 记录它与包内副本的对应关系。
原有 Installer payload 仍为四个文件，机器载荷仍为七个条目；发行目录增加一份
可直接阅读的说明 ZIP。未修改安装器的执行权限或生产编译开关。

所有归档读取均不解压路径。缺少文档、大小超限、重复或大小写冲突、额外文档、
内容地址错误、清单引用错误和实际 MSIX 副本不一致均拒绝。
失败时只删除本次新建的输出文件；已有输出不能覆盖。

## 证据及剩余工作

本机实际依赖生成覆盖 61 个 NuGet/SDK 输入、90 份原始文档，ZIP 为 274668 字节，
SHA-256 为 a5f35fb6df07b3edd4194412607ee45bac104d9abf578fb36fbbb523bff88aa4。
这些字节描述该次实际依赖归档；不同还原得到的合法重新签名归档会单独记录其真实摘要。

契约验证通过 30 个场景、53 项断言。除合成包外，复制一份已还原的真实签名包到测试
目录，确认正常内容通过，再修改内容并确认 NuGet 签名完整性异常；原缓存保持不变。
还验证了四项目覆盖、确定性、私有路径不输出、原文补充，以及损坏归档和最终 MSIX
错误副本的拒绝。PowerShell 5.1/7 解析和 18 项目 Release 构建通过。

GeoData 的固定 README、LICENSE、工作流和四个最终数据摘要已经随包保存。
该工作流会拉取动态上游分支，因此当前说明不声称恢复了所有上游输入修订。
生成器仓库的许可也不替代各个数据来源的条款。mihomo 对应源码及第三方数据的完整
发行归档继续推进；正式签名、Windows 11 安装矩阵和桌面验收仍是独立的未完成项。
