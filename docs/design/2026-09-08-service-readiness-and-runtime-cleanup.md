# 服务装配、就绪确认与运行目录清理

M5c 的安装、修复、卸载事务曾返回成功，但当时只观察了 SCM 的瞬时 Running 状态。
随后在 Verified 阶段终止 parent、排空 helper、以新进程继续同一事务时，服务复核失败。
进一步采集发现服务配置和 DACL 未变化，服务却从 Running 变为 Stopped。

客体 `2d83e4f8-a3fc-44fa-add2-327aef96b792` 的 .NET Runtime 1026 事件确认：容器无法
构造 `MihomoServicePipeServer`。多个内部实现的构造函数同样不能由既有类型注册解析。
这些失败运行均已销毁，输入与主机代理未变；不计入故障矩阵通过结果。原 M5c 记录
仍能证明事务路径和当时的资源观察，不能证明服务持续可用。

## 服务装配与生命周期

`MihomoServiceComposition` 用显式工厂注册生产实现，选择带服务配置的 materializer，
集中维护日志、代际文件、子进程、控制器、命令处理和管道之间的依赖。入口只解析
选项、接入 Windows Service lifetime 并调用该装配方法。

移出 supervisor 构造函数中的目录创建和旧运行文件清理。`InitializeAsync` 与命令、
关闭共用串行边界，成功后仅初始化一次；Worker 在接收请求前等待初始化完成。
独立 Start/Reload 调用也经过相同初始化，预取消不创建目录，不启动 mihomo。
真实容器测试解析完整 hosted-service 图，验证没有目录或子进程副作用。

微软文档说明[容器按构造函数解析要求 public 构造函数](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection#constructor-injection-behavior)。
显式工厂保留内部实现的可见性，避免把构造函数选择留给反射。

## 安装就绪确认

SCM 完整配置、DACL 与运行状态检查保持。Installer Windows 层引用纯 Core 中的
既有 IPC 合同，复用版本、请求关联、严格响应和有界帧解析，不依赖 WinUI。

1. 打开当前受保护安装关联对应的本地管道，以匿名模拟级别连接。
2. 查询已连接管道的 server PID，并与固定服务的 SCM PID、状态及完整配置核对。
3. 身份通过后才发送带安装凭据的 Hello；检查响应关联、协议、服务版本及子进程
   Stopped 状态。安装不启动用户网络接管。
4. 响应后再检查服务进程和配置，确认服务没有退出或更换。

连接、帧收发及后置检查共用 15 秒截止时间。外部取消仍返回取消，内部超时返回
稳定诊断；连接由操作释放。检查失败不提交成功阶段，不清除待恢复记录。
相同检查用于首次安装、修复和已提交阶段的重放。

## 卸载私有运行数据

真正启动的服务会在机器数据根下创建当前关联的私有运行目录。卸载在确认服务
不存在后，先移除机器载荷，再清理由受保护关联推导的精确私有目录，最后删除
关联并复核两个空根。清理失败保留关联，供同一事务重试。

清理保留机器根锁，完整预检所有条目，拒绝重解析点、硬链接、非普通文件以及超量或过深
的目录树，之后才移除只读代际文件并从叶到根删除目录。其他关联目录、用户源
配置以及无关文件不会被这个步骤删除。回归覆盖精确范围、只读文件、重放、
错误对象类型、超量树完整保留与取消。

真实临时硬链接回归还确认：发现多个链接时，在任何删除或只读属性修改前拒绝，
目录外别名的内容、只读属性和目录内别名全部保留。

本地 Release x64 完整构建通过，0 警告、0 错误；完整 format 加载 1400 个文件，
0 处变更。四份 `1.0.0-m5d-*.trx` 合计 4391 项通过，0 失败、0 跳过；Windows
测试排除了仅应在 CI/客体执行的 6 项真实用户证书存储用例。另以真实命名管道
验证连续 4 次 Hello，前一个客户端仍持有句柄时，后续请求与停止都能完成。

## 新候选的实际验证

修复提交 `de343b4` 与硬链接补充提交 `c0495f4` 均已推送 main。后者的
[CI 构建、格式、测试和离线打包全部通过](https://github.com/Water-Run/ClashSharp/actions/runs/34176167742)。
下载并核对的四份 TRX 合计 4398 项通过，0 失败、0 跳过：Main 2333、Core 970、
Presentation 125、Windows 970。真实用户证书存储测试在 CI 中执行；Core 行覆盖率
93.53%，分支覆盖率 86.73%。

实际事务使用包含新服务的同次 CI MSIX，以及从 `c0495f4` 编译的独立签名测试入口。
入口直接组合生产 parent/helper，没有替换服务、证书、包或事务适配器。客体内签名
EXE 摘要为 `ead27db5f3e6f402b80e4ae33c1e6e65846ea83a3355ae9c508fa860fdfc67d0`，
主 MSIX 摘要为 `798469d64d5df57bf4a85a31df6e41a77b3fe4604dc51b031725805e54d4414f`。

运行 `6074f108af844fd49008af5f44e5ac68` 完成正常安装、修复、卸载，三个事务均返回
`installer.completed`，没有待恢复记录。安装和修复执行新的就绪握手，期间未观察到
服务的 .NET Runtime 1026、服务事件 11 或 SCM 7034 崩溃事件。

| 中断或占用场景 | 实际观察与恢复 |
|---|---|
| 安装 MachineReserved / PackageCommitted / Verified | parent 在对应持久阶段退出 42；排空 helper 后，新 parent 读取同一阶段并完成安装，再卸载 |
| 卸载 MachineRemovalAuthorized / MachineRemoved / PackageRemoved / Verified | parent 在对应持久阶段退出 42；新 parent 继续同一卸载，完成产品清理 |
| 卸载时占用服务主程序文件 | 返回 `Uncertain`、`installer.machine_helper.response_unconfirmed`，阶段保持 MachineRemovalAuthorized 且 `RecoveryPending=true`；释放句柄后，新 parent 完成卸载 |

七个终止场景和一个文件占用场景全部通过。文件占用时没有声称完整回滚，安装器
保留可恢复状态；parent 被终止后的 helper EOF 也不是正常退出证明。每个场景的
产品卸载快照都先于测试兜底清理，确认精确包、服务、两类安装证书、服务目录和
状态文件已移除。随后客体九项清理通过，客体 `dc9aa490-604a-42d3-be61-d80cb3d55291`
已销毁，只读输入和主机代理未变。脱敏收据为
`artifacts/verification/installer-engine-m5d.json`。

同一 CI 开发安装器在远程 Windows Server 的无全局 .NET 环境完成 `--verify-payload`：
自包含运行时、5 个 WPF 原生依赖、4 个外部载荷文件和 7 个 machine 文件均通过，
临时目录和本次进程已清理。该入口没有执行正常安装或修改证书、代理；收据为
`artifacts/verification/installer-green-validation-m5d.json`。

## 安装中断后的系统重启

同一签名 EXE 和 MSIX 又在运行 `cbbce43715eb470982983410ea5d242b` 中完成重启恢复。
安装达到 Verified 后终止 parent，排空 helper，由独立检查进程记录日志摘要，再从
客体内部请求 Windows 重启。新的客体进程通过计划、机器身份和启动时间核对，
读取到原日志的相同摘要；没有在主机发出重启命令。

重启前估计启动时刻为 `02:03:32.3528634Z`，重启后为 `02:05:01.6157164Z`。
测试等待延迟自动启动的服务进入 Running，没有主动调用 StartService；新 parent
随后完成就绪握手和原安装事务，返回 `installer.completed` 且恢复记录已清除。
再运行卸载，产品快照确认包、服务、安装器拥有的机器及物理用户证书、服务目录
和状态文件均已移除。此快照仍先于测试兜底清理。

客体十项清理通过，含本次 RunOnce 续跑标记移除；客体
`4dbba653-2362-4674-ace2-a86b795453e0` 已销毁，输入和主机代理未变，未观察到服务
崩溃事件。脱敏收据为 `artifacts/verification/installer-engine-reboot-m5d.json`。
首轮重启夹具因空注册表属性处理错误在发出重启前失败，客体已销毁，未计入通过。

这些原生结果使用一次性客体签名信任和已记录的卷根 ACL 夹具；生产目录策略保持。
它们覆盖上述实际事务，不包含 WPF 页面操作、alternate-admin、升级、硬断电或完整
E3/E4 发布矩阵。系统正常重启不等同于硬断电。旧载荷的瞬时 SCM 观察没有计入新
握手结果，也没有发布正式版本。
