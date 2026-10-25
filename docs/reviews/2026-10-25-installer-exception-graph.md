# Installer 严重异常的包装处理

从原c30／870、47关闭文件、服务21796及1fc256c主线接续。公网DNS初次指向的119.45入口在握手前关闭；按既有主机密钥别名检查原27.156入口，确认仍是WIN-07OBU2PV8C6，随后DNS也恢复27.156。没有依据连接失败重放安装或恢复，服务器公共事务、产品进程均为空，原安装正常。

## 发现

审查恢复取消路径时，发现Installer Core、Windows适配、Presentation及入口的若干IsRecoverable只检查最外层类型。OutOfMemoryException、StackOverflowException、AccessViolationException或AppDomainUnloadedException被AggregateException、InstallerProtocolException或OperationCanceledException包装后，可能转成普通Blocked／Failed，甚至在调用者token已取消时直接转成Cancelled。helper的无条件abort reconciliation还可能用后续的普通读取失败覆盖原严重原因。

这项发现来自实际代码边界及合成回归，不是声称服务器曾真实内存耗尽或访问违规。原c30仍安装，没有向原用户安装器注入这些故障。

## 修正

新增纯InstallerExceptionPolicy统一判断InnerException及AggregateException所有分支中的四类严重原因，按对象引用去重、迭代遍历，不查看异常消息／Detail或凭据。普通错误、真实超时及正常取消不在这个严重集合内。

Core执行／原恢复的取消和协议分支增加同一过滤，常规recovery刷新与helper reconciliation不再吞掉严重图；机器证书后置对账也使用该判断。Windows文件、SCM、包、证书和身份适配的原业务类型白名单／透传要求保持，只为严重包装增加一致的排除。Presentation初始化、执行、AsyncDelegateCommand及CLI入口采用相同判断，避免下层正确传播后又被上层普通回调消费。未改数据格式、业务恢复阶段、权限、公共诊断内容或MCP范围。

## 验证

服务器准确旧源码1fc256c加新回归，Core **18失败／0通过**、Presentation **3失败／0通过**，均为行为断言失败，原始TRX保留。覆盖四种合成严重原因、三种包装，持久准备后的执行、原安装恢复及命令回调；合成对象不会实际耗尽内存、破坏堆栈或触发访问违规。CA2201只在明确构造这些测试对象的语句局部标注意图，生产规则未关闭。

修正后完整Core **1,127／0／0**、完整Presentation **193／0／0**、相关真实Windows适配接口的受控facade测试 **28／0／0**，包括既有普通失败诊断／原因保留和取消处理。新增21项行为回归，另将已有ViewModel及包适配严重错误对照扩展九项；新主线完整CI必须另外观察，不能将局部三组替代全量Windows／产品矩阵。完整format无改动、无工作区警告，服务器最终全部修改源码逐个长度／SHA与本机一致。

本轮未启动主程序、未修改服务器安装或原47文件、证书及MCP授权，也没有本机产品构建／运行／测试或Sandbox。当前安装c30不含这次新源码修正；后续签名候选必须绑定新准确CI和新摘要，不能改写此前成功回执。完整项目、其他身份／故障／Win11／发行继续未完成。
