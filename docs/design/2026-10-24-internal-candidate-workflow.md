# 内部签名候选与独立载荷工作流

internal-candidate.yml将准确产品CI、原受控构建、签名候选身份、固定归档和另一个作业中的只读载荷执行接在一起。入口仅接受main上的手动dispatch及准确CI run／源码SHA；其他ref直接失败。两个作业只有contents/read和actions/read，动作固定完整提交，不保留checkout凭据，不读取仓库签名秘密、不创建environment或Release、不配置持久runner。

## 生产作业

先用现有Get-ProductCiArtifacts.ps1核对真实必需步骤、四归档及原始TRX，再单独检出准确源码、使用该源码的固定SDK、locked restore和固定离线输入。控制器与构建源码分别记录；源码必须干净且与CI证据一致，证据读取租约跨越构建及候选封装。

内部签名SignTool明确固定为Windows SDK 10.0.26100.0。入口组合检查直接解析普通文件路径并核对可信Microsoft签名及时间戳，缺少工具在创建密钥前拒绝；生产控制器将该版本传入原CLASHSHARP_WINDOWS_SDK_VERSION参数，并在finally恢复原环境。该固定版本也列于[官方windows-2025镜像清单](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md)；镜像变化仍需实际工具检查，不能只凭标签推断存在。

New-InternalCandidate.ps1只在本仓库指定工作流、main手动运行和显式github-hosted上下文中使用；这些条件是可信调用者的误用保护，不是对任意环境变量的身份认证。它为当前用户创建两天有效的新MSIX／Authenticode测试证书，MSIX publisher严格来自源码清单，Installer subject含独立随机标识。Authenticode私钥不可导出，MSIX PFX以48随机字节生成的密码加密；密码不放入环境变量或子进程参数。签名目录的PFX／CER保持读租约，原有材料拒绝覆盖。

InternalSigning.psm1在密钥创建前保留已确认不存在的独立CNG容器名；密钥、证书和临时Root的归属仅存于该模块实例的私有状态，清理要求原始活跃token的对象引用及同一SID，反序列化副本无权选择删除目标。临时Root只添加已创建证书的公共DER，使用隔离高权限runner的机器Root。完成后按准确raw证书摘要、文件摘要、预存证书集合及已保留密钥槽清理，确认所有拥有的CNG键缺席及原证书集合保持，才返回成功引用。未知对象不删除。

正常和异常路径都进入finally；强制终止进程后的内存归属没有持久恢复承诺，因此整个构建入口限定为可销毁hosted CI，取消／超时还依赖runner销毁。持久服务器上的显式模块探针由单独控制器观察并回收，不将此工具作为常驻签名服务。证书生成参考[微软New-SelfSignedCertificate](https://learn.microsoft.com/en-us/powershell/module/pki/new-selfsignedcertificate?view=windowsserver2025-ps)，具名密钥缺席检查采用[CngKey.Exists](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cngkey.exists?view=net-10.0)。

原build.ps1的准确证书、可信时间戳、原生verify、BOM及载荷门不变；内部用途调用正式形状路径，不启用Development。身份及归档从原八个输出产生。上传仅允许internal-candidate.zip、candidate-identity.json、ci-evidence.json和内部结果四文件，且必须在临时信任／签名文件／私钥清理确认后执行；失败仅上传构建日志及独立清理观察。每个run attempt使用新artifact名，既有记录不覆盖。

失败路径另保存operation-result.json，记录停止阶段、实际清理对象计数、原集合保持及清理是否确认；它不声明整个候选成功，也不包含私钥或密钥容器名。失败诊断artifact只含build.log和这份清理观察，避免仅凭“原构建异常仍被抛出”推断清理已通过。

## 独立作业

新Windows Server runner按生产作业的artifact ID下载，只消费needs输出传来的归档摘要、候选身份及两项指纹；下载的JSON不选择批准摘要。GitHub传输机制参考[跨作业artifact说明](https://docs.github.com/en/actions/tutorials/store-and-share-data)，内部ZIP仍由现有接收器单独核对准确摘要和九项库存。

Invoke-InternalCandidateAudit.ps1先检查干净控制器及真实CIM平台／x64，再固定归档读取句柄，展开准确字节，核对源码及内部签名引用。实际Installer签名元数据必须匹配独立指纹且含时间戳；不导入生产作业临时Root，Windows链状态如实记录，不声称再次完成信任／撤销验证。只有既有固定--verify-payload入口可执行，进程、终态、规范载荷回执及原归档引用一并保留。

当前矩阵只有windows-2025／windows-server；Server即使build26100也不算Win11。成功范围仅installer-read-only-payload；installationVerified、win11AcceptanceSatisfied、publicReleaseAuthorized和promotionAuthorized仍为false。独立收集器、实际源码及平台都记录在回执中。完整安装、标准用户／替代管理员、恢复／重启／故障、真实Win11界面及受保护公共签名发行仍须另外完成。

## 复用已生产的同一候选

internal-payload.yml提供单独的手动收集入口。调用者从可信生产操作独立保留源run／artifact ID、原ZIP摘要、候选身份和两个签名指纹；新作业先用Get-InternalCandidateSource.ps1读取新鲜API，确认同仓库main手动生产工作流、完整job列表、成功生产作业及全部关键步骤、准确artifact名称／ID／来源／digest／有效期。元数据不是字节验证或执行批准，输入引用仍须由可信调用者选择。

来源工作流可因后续收集器失败而整体failure，但生产作业必须实际成功；回执分别记录sourceWorkflowConclusion与producerExecutionVerified，acceptanceVerified和promotionAuthorized保持false。这样可以保留原artifact，在新隔离作业中重验相同字节，不重新构建或签名。来源解析契约属于收集控制器，不追溯改写旧候选已有的产品CI事实。

跨run按已验证artifact ID下载，独立SHA和指纹继续直接传给原审计器；不会从下载的JSON自行挑选批准值。收集作业显式准备证据父目录，操作目录仍须全新，失败现场保留。新入口只有读权限、固定动作及窄范围证据上传，不调用构建、签名或证书导入。
