# 服务恢复后查询被拒绝的实际恢复与暂停

当前07bd758e签名候选（源码6d3d849、准确完整CI40190881685／7,318项通过）在公网Server 2025完成新的后置观察故障案例。服务已经由产品恢复Running后，新的SCM配置／状态查询被拒绝；安装器保留未确认状态，解除故障后由新父进程完成原恢复，未重复启动服务。本轮未修改产品代码、重建或重签候选，没有运行本机产品测试或Sandbox。按用户要求完成本节点后收尾暂停。

## 切点与结果

仅对固定ClashSharpMihomo服务临时加入拒绝Administrators配置／状态查询的DACL条目，掩码5。观察器持有事先取得的查询及WRITE_DAC句柄，检查准确父进程、创建时间、账户、载荷、事务、私有恢复阶段和原DACL后才注入；它没有启动、停止、删除服务，也没有写产品事务。相关访问位及DACL写入权限见[Microsoft服务访问权限](https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights)和[SetServiceObjectSecurity](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-setserviceobjectsecurity)。

独立子进程验证后自动撤销准确的自有DACL，另有150秒句柄租约和独立三分钟兜底。正常撤销命中原DACL／注入DACL摘要，兜底未执行；没有放宽无关权限或修改其他服务。

事务`155ca9092f7f55d860ec5330815456435a137128bf3166137d166fedae83f8a3`，真实WPF准备51020、故障恢复25652、再次原恢复74052均正常关闭，父进程退出码0。故障阶段的0表示前端正常关闭，操作本身明确为`installer.machine_helper.response_unconfirmed`。

| UTC事件（2026-10-25） | 原始时间 |
| --- | --- |
| 首次观察服务Running，PID44840 | 13:31:18.0463009 |
| 注入精确查询拒绝 | 13:31:18.0523007 |
| 界面报告回执未确认 | 13:31:48.4940525 |
| 另一进程实际新查询返回ERROR_ACCESS_DENIED(5) | 13:31:55.3835157 |
| 原始DACL恢复并独立核验 | 13:31:55.6273195 |

拒绝期间公共记录保持Prepared第1代，私有PreserveOriginal第3代（stage2）。原始JSON的完整小数秒及偏移独立复核通过；查询拒绝持续37.5750188秒，Running至注入5.9998毫秒，仅用于绑定此次切点，不作性能保证。

解除故障后服务仍Auto／Running，PID44840及创建时间保持，公共Prepared与私有PreserveOriginal保持；撤销测试故障没有替产品完成恢复。新安装器74052执行真实“尝试恢复并保留原安装”，返回`installer.recovery.original_restored`，公共事务清空，私有OriginalClearReady第5代（stage4）。服务仍为同一PID和创建时间，没有重复启动。

持有准备11项、关闭准备12项、查询拒绝12项、精确解除13项、产品恢复13项，共61项独立检查通过。原47文件及备份、完整已装包和7机器文件、原SCM命令摘要／DACL、代理及五证书集合保持，MCP授权未变。四自建任务（含未执行兜底）已归档移除，临时Auth根清理，无公共待处理事务、应用／内核／安装器进程。

## 未命中的首次案例与证据

首次独立案例保留为`not-exercised-helper-defect`。准备父63660已命中预期Prepared切点，但控制器未及时发出释放指令，自动finally撤销只读占用并关闭，原失败结果保留；独立12项核验同一事务后才续接，没有重跑该准备。随后观察器把私有intent的数值operation枚举按公开事务的文本枚举读取，出现InvalidCastException，尚未修改DACL；产品自行完成原恢复，父55184退出0，服务PID54444，公共清空。三任务归档移除、原数据／配置／信任和完整安装内容核验后，才建立新事务。修正后的观察器在真实私有记录上先作只读格式检查，并将独立验证和释放放入同一控制流程。首次未命中没有计作故障通过，其脚本、日志与失败回执一并保存。

规范成功案例SHA256 `d7db23c37b1be1b63a5884dcf7c17f2f0e94c12be306723a7a3229c1158f265b`。两个案例共120个白名单证据文件及清单，服务器和客户端逐项长度／SHA核验；ZIP174,094字节，SHA256 `f6b5c10c2607f0820d584ff25ad292290ccc9957858210226b1faa6c28bb54bf`，清单`689de6da604ab7ac1a615c45a54231327608a2115e3b0de8d4c6f9f5911747ce`。未包含原用户文件、私有基线／记录、密钥或发布载荷。

暂停时仍安装07bd／6d3，Main074D3837／ServiceF965EE20，服务Auto／Running／44840。最新47文件关闭备份为`C:\ClashSharpValidation\scm-observation-20261025-r2\after-native-review`，backup.json SHA256 `c1bfba2ec88451aa610d75faa8f823497379ab4d2285d2a6f021fa7f07e9a7fd`，C盘剩余约148.96GiB。没有遗留测试任务或需要接续的测试进程。

范围仅当前管理员／Server／服务恢复效果后的SCM后置查询拒绝及产品重试，不证明StartService原生RPC回执丢失、所有部分写入／取消／重启或其他身份。桌面仍断开，实际Win11与完整页面／DPI／主题视觉矩阵、公开可信发行继续未完成；本轮无新的生产缺陷结论。后续恢复开发时先核对当前安装、47文件和服务，再从总体验收矩阵选择尚未覆盖的要求，避免重跑已经关闭的案例。
