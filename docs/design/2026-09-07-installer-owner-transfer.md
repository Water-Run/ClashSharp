# Installer 跨账户换绑边界

## M4h 已实现的边界

普通 parent 仍固定 `AllowReassociation=false`。审查发现底层 ownership policy 和 association store 曾单独使用该布尔值放行未知或外来归属：前者为不明确的修复返回新 token，后者允许覆盖旧 association。这不是完整换绑证明，可能丢失恢复旧服务所需的凭据。

现在普通 provisioning 只接受两种状态：

1. 已存在目标 SID 的严格 association：继续使用原 token；
2. association、服务和机器载荷残留均不存在：创建新 token。

其他状态均在服务停止或 association 写入前拒绝，不再因请求携带换绑意图而放行。底层 association store 独立拒绝覆盖其他 SID、同 SID 不同 token、损坏 JSON 或不安全文件对象；只能创建缺失文件，或观察已经完全相同的 association。原有文件替换失败后的后置条件核对不变。

这项变更收紧的是普通入口，不代表已经实现了用户确认、双 owner 迁移或恢复界面。请求和 v2 日志仍保留原字段以维持协议读取兼容性，该字段只记录意图。

## M4i 已实现的私有状态协议

Core 的 `Ownership` 模块现在定义独立 schema 1、两份 participant、两份可选证书账本、普通 v2 Prepared continuation、规范 JSON codec、摘要 snapshot 和保存/删除策略。它们均不执行文件、账户、服务或权限修改，尚未接入 production composition。

八个阶段只允许相邻推进或原阶段精确重放：

| 阶段 | 实际执行器在推进前必须验证 |
|---|---|
| Prepared | 旧/新账户证据、候选及后续普通事务已经保存 |
| StartupBlocked | 普通 Prepared 日志已在旧 ACL 下建立，App 持续被阻断 |
| PreviousServiceRemoved | 用旧 owner/token/profile 验证的原服务已经删除 |
| MachineAccessTransferred | 机器载荷与服务数据目录均为新账户的准确 ACL |
| AssociationTransferred | association 完全等于预先记录的新 owner/token |
| CertificateStateTransferred | 原证书账本已保留，目标账户账本已就绪 |
| InstallerAccessTransferred | 普通 Installer 状态目录权限已转移 |
| Verified | 全部后置条件与仍然存在的 Prepared continuation 已复核 |

创建只接受 Prepared；删除只接受 Verified、准确的 continuation transaction ID 和准确摘要。保存必须提供当前摘要，并保持候选、事务、两份凭据、profile 和证书账本的全部身份字段不变。该纯策略不能替代机器独占租约、实际存储读写及原生后置条件验证。

新、旧 SID 与 profile 必须不同，凭据必须不同；continuation 只能是无 takeover 标记的普通 Install/Repair。证书账本分别绑定各自 SID，且保留有效引用，不能用已释放账本冒充活动归属。profile 语法拒绝 UNC、设备/相对路径、替代数据流、点段、DOS 设备名、结尾点/空格及无法无损表示为 UTF-8 的字符；Windows 适配器仍须独立验证实际 profile 映射和目录。

私有 JSON 上限 16 KiB、深度上限 4，使用 [System.Text.Json 源生成元数据](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)并对重新编码的字节做完整比较。重复/未知属性、大小写变化、属性重排、缺失 nullable 属性、数字 enum、替代转义、BOM、注释和多余空白均被拒绝。私有协议中嵌套 continuation 的 JSON 表示独立于公开 v2 格式；写公开文件仍必须调用原有 `InstallerTransactionCodec`，其输出不包含凭据或 profile。

公开诊断字符串只包含阶段和 generation；codec 不保留可能包含私有值的解析异常，临时序列化缓冲在摘要或比较后清零。这不意味着托管字符串可擦除，也不意味着仅靠模型即可获得 Windows 修改权限。

161 项协议用例覆盖完整阶段组合、陈旧摘要、身份替换、两份证书账本、最大 Unicode profile、严格编码和诊断脱敏。完整 Installer Core 721 项通过、0 跳过；同次覆盖率为行 93.29%、分支 85.98%，超过既有 90%/80% 门槛。收据为 `artifacts/verification/coverage-1.0.0-m4i/1.0.0-m4i-core.trx`，覆盖率报告位于该目录的 `0da142d4-d948-4289-bc28-75765d6fe861/coverage.cobertura.xml`。全解决方案 Release x64 构建 0 警告、0 错误。

## M4j 已实现的私有持久存储

Core 的 `InstallerOwnerTransferStore` 将摘要比较、相邻阶段和精确重放规则应用于一个窄的字节存储端口。写入或删除即使返回 I/O 错误、取消或确认丢失，也会在调用方仍持有机器权限及目录租约时重新读取实际状态；只有目标状态已被观察到才报告成功。取消且状态未变时返回取消，状态损坏或不可确认时返回稳定的 uncertain 诊断，不进行第二次修改。读写缓冲在使用后清零，同一 store 拒绝并发调用；这不能替代进程间机器独占锁。

Windows 实现使用固定的 `ProgramData\ClashSharp\InstallerAuthority\v1\owner-transfer-v1.json`，与用户可读的 `Installer\v2` 分开。已有 `ClashSharp` 根目录必须存在且通过祖先检查；私有后代只接受 Administrators owner、受保护 DACL 和 SYSTEM/Administrators 两条准确 FullControl ACE。创建使用显式安全描述符，已有目录不被重新赋予“正确”ACL；验证失败即拒绝。每次操作重新检查已固定的目录句柄，租约在 authority 退出前保持。

文件句柄必须是普通文件、只有一个硬链接，并具有准确的两条私有 ACE。读取限制为 1–16384 字节；只有在已验证根目录下确认固定叶子不存在才返回空状态。写入通过[在创建时指定 ACL 的 FileInfo.Create](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemaclextensions.create?view=net-10.0)建立独占随机临时文件，验证句柄后写入并 flush，再使用同目录 [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw) 替换固定叶子。清理只涉及当前调用创建的临时路径；后续调用不信任或自动删除旧临时文件。

新增 22 项 Core 故障用例、21 项 Windows fake 边界用例。完整 Installer Core 743 项、Windows 开发机安全子集 436 项通过，均 0 跳过；行覆盖率 93.39%、分支 86.17%。完整 Release x64 构建 18 个项目，0 警告、0 错误，format 检查 1284 个源文件、0 处变更、无工作区警告。

远端管理员进程使用生产 Core/Windows 存储、生产机器互斥锁和隔离 ProgramData 路径完成 74 项原生断言：独立进程重启恢复 Prepared、七次相邻推进、Verified 清除、准确 ACL、目录重命名被句柄阻止、宽松文件/目录 ACL、硬链接、符号链接和超大文件拒绝，以及异常对象字节/权限保留。没有修改服务、系统代理、包或证书。收据为 `artifacts/verification/private-persistence-validation-m4j.json`，包含实际被测程序集摘要；这是存储级验证，不包含实际服务迁移、掉电测试或桌面验收。

当前 backend 只供显式迁移 authority 使用，首次读取可以创建缺失的两个私有后代；普通 helper 通过下面的 M4k 只读端口检查未完成迁移，不能调用该创建路径。写入 backend 尚未接入 production composition，也没有赋予普通操作换绑权限。

## M4k 普通 helper 的只读准入

认证 helper 现在按“机器独占锁 → 私有迁移检查 → App 生命周期锁 → 普通事务资源”建立 authority。只读检查使用独立的 presence 端口，不持有写入方法、不读取或解析记录内容；缺失产品目录或私有后代只表示该固定链尚未建立，不会触发创建。缺失 ProgramData 祖先、无法打开的对象或不准确的权限仍然拒绝。

完整链存在时，通过原生文件句柄验证对象种类、单硬链接及私有 ACL，然后只判断固定叶子是否存在。所有阶段，包括 Verified，以及空文件、损坏 JSON 和超大文件，都返回 `installer.owner_transfer.pending`；普通 helper 无权自行清除。仅确认叶子不存在时允许后续普通流程继续。

检查拥有自己的目录句柄并在返回或异常时释放；机器独占锁继续覆盖后续 authority 生命周期。取消会等待已拥有的检查任务结束后再释放机器锁，且不会进入 App 锁或事务资源创建。新增 14 项 Windows 用例覆盖缺失链、对象拒绝、句柄释放、authority 顺序和取消；完整 Windows 安全子集 450 项通过，0 跳过。Release x64 全解决方案构建 0 警告、0 错误，format 1285 个源文件、0 处变更、无工作区警告。

远端生产只读检查在隔离 ProgramData 上通过 31 项原生断言，确认八个阶段及三种无效长度均阻断、原字节保留、宽松 ACL 被拒绝、缺失产品目录不被创建。收据为 `artifacts/verification/owner-transfer-admission-validation-m4k.json`。该结果不证明专用迁移执行器、完整 helper IPC 或桌面安装已经完成。

## 持久迁移的接入要求

换绑必须是独立用例。后续接入不能再次让普通写入方法凭布尔值覆盖旧证据：

- 由经过认证的 helper 获取机器独占租约，检查并持有旧、新账户的 App 生命周期屏障。缺失、重定向或不明确的旧 profile 不能当作旧 App 已停止。
- 在修改前保存旧/新 SID、旧/新凭据、可信 profile 身份、同一候选摘要和普通安装后续事务的身份。包含新凭据的记录仅允许 SYSTEM/Administrators 访问，不能写入 parent 可读 v2 日志、日志文本或 IPC 结果。
- 先持久阻断 App 启动，再用旧 SID/token/profile 构造旧 SCM tuple，验证、停止并删除旧服务。存在陌生服务、损坏 association 或未知 ACL 时拒绝，不能自动修复成当前账户。
- 明确区分服务已删除、机器目录 ACL 已转移、association 已转移、证书所有权状态已保存、Installer 状态目录 ACL 已转移以及最终验证。每一阶段的重放只接受预期的旧状态或已完成的新状态。
- 普通 v2 Prepared 日志必须持续存在到新账户的安装事务接管，避免迁移与继续安装之间出现 App 可以启动的空隙。目标账户没有包时应接续 Install；已有包时接续 Repair，不能绕过 Repair 要求已有包的规则。
- 现有 certificate ownership store 只保存一个目标 SID 的账本。旧账本不能改写 TargetSid 或在有引用时删除；需要保留原证书归属证据，并定义旧账户包的保留/移除及后续独立卸载路径。目录 ACL 转移本身不能解决这项多账户状态问题。
- 确认界面应明确说明旧账户的服务接管将停止，以及旧包、配置和证书如何处理。只有这条独立确认路径可以申请迁移权限；普通 Repair、静默参数和恢复一个不匹配的候选不能隐式发起换绑。

这些要求尚未全部实现。下一步连接专用阶段执行器、证书账本保存和确认界面。当前生产 mutation 门继续关闭，跨账户请求仍拒绝；协议、存储及普通入口检查的测试不是换绑功能完成的证明。

## 验证

M4h 增加按观察状态、服务残留和意图标记展开的拒绝用例，并保留同 owner 的凭据复用验证。Windows 适配器测试使用 fake SCM/file ports 或临时目录，核对旧字节、服务和载荷保持，以及停止/写入调用没有发生。

Release x64 完整构建 0 警告、0 错误；Installer Core 560 项、Windows 开发机安全子集 415 项通过，均 0 跳过。当前用户证书存储的 3 项修改测试仅在隔离 CI 执行。证据为 `1.0.0-m4h-core.trx`、`1.0.0-m4h-windows-safe.trx` 和 `artifacts/verification` 中的同节点日志。
