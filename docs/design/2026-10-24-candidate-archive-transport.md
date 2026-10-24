# 同一签名候选的归档与接收

候选身份已绑定签名输出和准确CI，但此前跨环境仍需零散复制八个文件。New-CandidateArchive.ps1现在把规范身份和八个原文件封装为一个固定九项ZIP，返回独立候选摘要、归档SHA256和长度；Expand-CandidateArchive.ps1按生产操作独立返回的两个摘要接收并重验。这是后续验收矩阵和同digest提升的传递入口，不重新编译、签名或替换载荷。

## 生产与接收

生产入口复用Invoke-ClashSharpCandidateIdentity，清单和全部八文件保持读取租约直到复制及源目录末次复验完成。ZIP使用固定名称、顺序、1980时间和零外部属性，原样存储；同一组输入可重复得到相同归档字节。先独占创建.partial，完成源检查和磁盘flush后才移动到正式输出，已有正式输出或.partial均拒绝。失败保留现场，没有成功回执，也不自动重试。

接收前固定归档读取句柄，限制4GiB并核对独立归档摘要；摘要不能由归档或旁边不可信的JSON自行挑选。先检查恰好九项、准确大小写及顺序、零外部属性和各项长度，随后才创建新目录。只将固定路径写入新建普通文件，不采用通用ExtractToDirectory或执行归档中的指令。清单最多64KiB，其他单项最多1GiB；复制还逐段检查真实展开字节数，拒绝虚报、额外和截断字节。长度字段语义见[微软ZipArchiveEntry.Length](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchiveentry.length?view=net-10.0)，不能用该声明代替实际读取。

展开后再次复用规范身份及八文件消费入口，核对全部长度／摘要，并检查整个接收目录恰好九个文件。既有目标目录、重解析父目录、输出ADS／设备名／末尾点空格歧义都拒绝，保留失败目标而不覆盖。归档句柄保持到接收文件的末次复验完成；返回后不是永久目录租约，实际安装或审计还须继续消费同一独立身份。

## 范围与接续

ZIP摘要标识此次传输的完整字节；候选身份继续标识规范清单和原始签名文件，两者都须从可信生产操作保留。自洽摘要不认证任意程序，接收入口不重新检查证书信任／撤销，不导入证书，不启动产品或安装。两个回执的signatureTrustRechecked、acceptanceBound和promotionAuthorized均为false；本入口不替代独立签名策略、平台及安装恢复矩阵或受保护审批。

CI新增必需产品步骤Verify candidate archive transport contract，准确产品CI输入策略同时要求该步骤成功。契约只使用明确的不可执行合成字节和合成签名观察；实际签名候选的生产／接收及原状态核对单独记入[验收记录](../reviews/2026-10-24-candidate-archive-transport.md)。独立候选／矩阵／promotion工作流和真实Win11验收仍需继续实现与执行。
