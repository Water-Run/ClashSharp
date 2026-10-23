# 固定候选的只读载荷执行证据

`Invoke-CandidatePayloadAudit.ps1`在受信任的验收宿主上消费签名生产操作独立返回的候选摘要，执行唯一固定参数`--verify-payload`。入口不接受自选命令、脚本或从清单加载的回调；调用者必须先信任该准确候选，摘要自洽不能认证任意程序。

## 消费期间的归属

共享`Invoke-ClashSharpCandidateIdentity`先锁住并验证规范清单，再锁住八个正式形状输出并核对全部字节。只有全部匹配才进入可信收集回调；清单及文件的读取租约保持到实际进程终止、回调返回和目录合同复验完成。消费返回后不再持有永久锁。

独立只读重验入口复用该函数，不再维护第二份清单解析逻辑。嵌套回调保存到独立变量，避免PowerShell动态作用域将外层Consume误解析成内层同名参数。若回调抛错或末次库存核对失败，不返回成功结果，读取租约仍正常释放。回调的外部副作用不具有回滚语义，因此此入口只组合可信的窄范围收集器。

## 实际执行与证据

输出目录必须是release之外的新目录。已有目录在创建进程前拒绝，失败文件保留供观察，不覆盖、不自动重放。进程记录包含本次GUID、独立候选摘要、当地CIM平台观察、固定参数、PID、起止时间及终止／退出状态。

收集器隐藏启动独立Installer进程，异步消费两个输出流，正常退出码须为0，stderr须为空，JSON须在4,096字符内，且恰好八个互异字段。超时仅终止本次持有句柄的进程树，不按进程名称清理其他进程。此处只允许受信任候选的固定诊断入口，不是运行任意可执行文件的资源隔离沙箱；输出长度是在可信进程返回后验证。

成功观察还必须与租约中的候选主MSIX摘要、payload目录的文件数和字节总量一致。machineFileCount是MSIX内部机器组件数，与外层payload文件数不同，不要求两者大小有序。字段类型、版本、可执行文件名、UTC时间顺序和有限执行时间均单独核对。

规范收据为无BOM UTF8、紧凑JSON、单个结尾LF，返回摘要就是实际字节SHA256。范围固定为`installer-read-only-payload`，绑定候选身份、来源提交、一次实际运行及平台；调用纯规范化函数的合成观察不获得执行认证。

## 平台与验收边界

收集器直接读取Win32_OperatingSystem的Version、BuildNumber、ProductType及运行时OSArchitecture。ProductType为2或3始终归入Server；不会仅因26100等共享build而标记为Win11。当前10.0客户端按22000边界区分Windows10与Windows11，类型与版本／build必须一致。字段语义见[微软Win32_OperatingSystem说明](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-operatingsystem)，版本边界参考[Windows11发行信息](https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information)。

这份收据只证明固定包的一次只读载荷执行。installationVerified、win11AcceptanceSatisfied和promotionAuthorized恒为false，即使收集宿主确实是Win11也一样。完整安装、真实界面、数据／代理／证书恢复、身份／故障矩阵及受保护发布各需另外的实际证据；不得用此收据补齐这些结果。
