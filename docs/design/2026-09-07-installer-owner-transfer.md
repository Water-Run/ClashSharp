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

## M4l 持久阶段编排

Core 的 `InstallerOwnerTransferCoordinator` 只接收同一专用 authority 已检查的私有 snapshot，先重新读取并逐字段核对实际记录，再调用阶段执行端口。Prepared 必须已经保存；记录缺失、进度变化或身份变化均在系统修改前拒绝，不能由恢复入口重新生成凭据或候选。

六个修改边界按顺序调用 `ApplyAndVerifyAsync`，成功完成一个边界后才保存下一阶段和 generation。执行端口必须独立验证整个步骤的后置条件，并允许准确的已完成状态重放。取消和异常不会触发回滚、删除证据或执行下一步；已经执行但未确认的步骤由下次恢复重新观察。编排器拒绝并发进入，等待已拥有的异步任务结束后才释放本地执行状态。

最后单独调用只读 `VerifyCompletedAsync`，复核服务、权限、association、证书账本及准确的普通 Prepared 启动屏障，然后保存 Verified 并按摘要删除私有记录。恢复已是 Verified 的记录同样再次验证；最终保存/删除确认不明确时仍保留可恢复状态。返回值仅包含原普通 v2 snapshot，后续安装仍须在相同 authority/生命周期保护下接管，不能提前清除普通日志。

新增 31 项用例，使用生产 Core 私有 store、真实临时文件中的普通 v2 store 和模拟系统修改端口，覆盖八个初始阶段、六个步骤前/后中断、取消期间排空、陈旧身份、最终验证失败、普通屏障丢失和不明确的提交确认。完整 Core 774 项通过、0 跳过，行覆盖率 93.44%、分支 86.23%；全解决方案 Release x64 构建 0 警告、0 错误，format 检查 1288 个源文件、0 处变更、无工作区警告。覆盖率与 TRX 位于 `artifacts/verification/coverage-1.0.0-m4l`。

这项实现是持久编排；阶段端口的 Windows 原生实现和专用认证入口仍待接入。模拟的服务/ACL/证书操作不计作实际换绑验收，也不改变普通 helper 的拒绝边界或生产 mutation 门。

## M4m 旧服务步骤与目录句柄修复

Windows 的旧服务步骤只接受 StartupBlocked。它重新核对同一候选、准确的普通 Prepared snapshot、旧 SID 对应的实际 profile、机器目录权限与旧 association，再调用已有 SCM tuple 验证及停止/删除实现。后置检查要求服务不存在、旧 association 仍准确存在；服务已经不存在时可重放。目录和 association 租约持有到异步步骤结束，取消不会提前释放。窄端口只提供该步骤需要的六项操作，普通 machine backend 复用这些实现。

首次远端验证在写入初始 association 时发现一个影响普通安装的真实错误：外层目录保护请求 DELETE 且不共享删除，内层文件写入保护又拒绝共享该 DELETE 访问，因此 CreateFile 返回 ERROR_SHARING_VIOLATION（32）。另一项原生对照确认，仅请求 READ_CONTROL 和 FILE_READ_ATTRIBUTES 的只读句柄没有阻止目录改名。

共用目录 lease 现在统一请求 READ_CONTROL、FILE_LIST_DIRECTORY 和 FILE_READ_ATTRIBUTES，共享读/写但不共享删除。读取目录内容的访问参与共享冲突检查，能够固定名称，同时不再携带会阻塞嵌套保护的 DELETE 权限。移除原来两个模式的布尔参数，读写两种 root guard 都使用同一保护语义；实际删除操作仍使用独立的删除句柄。[CreateFileW 的访问与共享规则](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)说明了共享限制及删除与改名的关系；具体旧、新句柄行为另有真实 Windows 对照收据。

新增 21 项服务步骤边界用例，以及 3 项实际临时目录回归：多个观察/修改保护共存并持续阻止改名、已有删除权限阻止新观察 lease，以及持有保护期间完整创建/替换/读取/删除 association。临时目录测试不修改系统 ACL、服务或证书。完整 Windows 安全子集 474 项通过、0 跳过；18 项目 Release x64 构建 0 警告、0 错误，format 加载 1291 个文件、0 处变更、无工作区警告。

远端使用生产 Windows backend、真实 SCM 和隔离文件根目录通过 12 项服务断言，覆盖准确旧服务删除、已删除重放、不同凭据的服务拒绝及原样保留、association/普通日志保留。测试只创建停止状态的自有服务，结束后确认固定服务不存在。候选为测试 manifest/lease，双 App 屏障及候选签名未在该探针验证，所以这不是完整迁移或签名安装验收。

目录句柄变化后，使用同一程序集在新隔离目录重跑私有存储与普通准入的四个独立进程，共 105 项断言通过，包括阶段恢复、ACL、硬链接、符号链接、大小限制和全阶段阻断。收据为 artifacts/verification 下的 service-removal-validation-m4m.json、private-state-regression-m4m.json 和 handle-sharing-validation-m4m-initial.json；实际 Windows 程序集 SHA-256 为 deb128c8dd39fafeb04b748935d71b61e10434e1867a11cdd4d1db3b310be395。

该服务步骤尚未接入完整阶段执行器及专用认证入口。机器 ACL 迁移、association 切换、证书账本保留、双账户清理和确认界面继续实现，生产 mutation 门保持原有关闭状态。

## M4p 机器共享目录权限迁移

`WindowsOwnerTransferMachineAccess` 只接受 PreviousServiceRemoved。只读状态端口核对两份已记录 profile、固定根布局及旧服务缺席；持有的候选在修改前后复核。它不保存阶段、不修改 association、不创建缺失目录，也不申请全局机器权限或双 App 屏障；这些资源仍由后续专用执行器持续拥有。

`WindowsOwnerTransferAccessTree` 先固定卷根和受信任祖先，再验证 Program Files/ClashSharp 与 ProgramData/ClashSharp 下的固定布局。Service 仅允许 current、staging、previous 三个载荷目录，服务数据仅允许 association.json 和规范命名的受保护服务私有目录。整个遍历最多 256 个对象、产品根下深度 12；普通文件必须没有重解析点且只有一个硬链接，拒绝设备名、替代数据流、歧义路径、未知根成员和额外权限。全部验证完成后才写 DACL。

共享根只接受 Administrators owner、受保护 DACL、SYSTEM/Administrators FullControl 及准确旧/新 SID 的 ReadAndExecute/Synchronize 三条规则。载荷后代与 association 必须是对应的继承规则。Installer 保持旧 SID 三条规则；InstallerAuthority 保持 Administrators owner 的私有两条规则；服务私有目录保持 LocalSystem owner 的私有两条规则。私有边界不递归枚举或修改。

Installer 边界仅额外固定已知的 v2 目录和普通日志文件，按旧权限只读检查准确 Prepared 字节。不能在这一阶段复用普通旧账户读取器：公共 ClashSharp 根已经可能换成新权限，而原读取器要求整个旧账户目录链始终一致。专用的只读日志句柄跨越这次权限变化，拒绝共享写入和删除；普通读取器的要求没有被放宽。

原生适配器只申请读权限及必要的 WRITE_DAC，不申请删除、写文件内容或修改 owner 权限。通过 [SetSecurityInfo](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-setsecurityinfo) 修改已固定对象的 DACL。由于 Windows 会传播可继承规则，实现预先验证整个传播范围，按父项先于子项推进，并逐项复核；若系统已完成子项继承则不再写入。取消或 I/O 失败保留已完成的 ACL 和原持久阶段，以便准确重放。

新增 62 项模拟边界测试覆盖阶段与候选拒绝、双方 profile、服务重新出现、恢复日志变更、整树预检、私有边界、限制、取消/故障恢复、诊断脱敏及等待中的候选复核资源生命周期。Windows 安全子集 536 项通过，完整解决方案构建零警告、零错误。

真实隔离 Windows 的种子、LocalSystem 私有目录、部分执行和新进程恢复四个进程通过 65 项断言。第一次权限执行在两处共享根修改后中断，恢复进程完成另两处；Windows 自动转移全部已检查的继承后代，再次执行零写入。association、普通 Prepared 及私有文件字节均保留；Installer/v2、InstallerAuthority、服务私有边界的原始安全描述符摘要均未变化。句柄阻止目录、association 和日志改名，也阻止日志/association 写入，所有进程结束时句柄计数为零。实际被测 Windows 程序集 SHA-256 为 `714e113a7869d9e2cef1dda1a75ad15479474c3a894f2de0f467028065881ca4`，来自直接 x64 项目构建，收据保存了对应 Core 摘要。

额外尝试的 MAXIMUM_ALLOWED、同卷移动及独占子句柄夹具均未形成期望的旧继承子项状态，失败记录原样保留，不计入上述通过项。前两种得到受保护的旧 ACL，后一种仍自动更新继承 ACL；生产实现不使用这些注入方法。逐子项显式补写的恢复分支目前只有模拟测试，不能将这些夹具当作该分支的原生证明。没有改动本机代理，也没有在本阶段启动真实服务、内核或修改证书。

完整阶段执行器、后续 association/证书/Installer 状态迁移、双账户清理与确认界面仍需继续。正式执行门维持关闭。

## M4q 关联文件原子迁移与恢复

`WindowsOwnerTransferAssociation` 只接受 MachineAccessTransferred，复核同一候选、双方 profile、固定布局、服务缺席和准确普通 Prepared。机器树提供独立的只读租约：共享 ACL 必须全部属于新账户，Installer/v2 仍保持旧账户权限，其他受保护边界保持不变。租约持续固定父目录、载荷和日志；association 叶子由专用文件端口短暂打开，完成观察后释放，允许同目录原子替换。此端口不改变普通 association store 的创建或精确重放限制。

文件端口仅接受记录中的旧 association 或准确的新 association。每次读取都要求普通文件、单硬链接、Administrators owner 和准确的新账户继承 ACL，大小不超过 4 KiB。事务临时文件名由已经持久保存的随机 256 位 continuation ID 导出；只有这个准确名称可以在只读树租约持有期间出现或消失，其他名称、大小写变体或目录对象均拒绝。

恢复时，已知临时文件还必须符合相同文件约束，且字节是本次规范新 association 的准确前缀。空文件、部分写入及完整文件可以据此移除并重建；未知字节、权限或链接原样保留。新临时文件使用独占 CreateNew，写入后调用 [FileStream.Flush(true)](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0)，再次检查旧状态及完整临时文件，再以 [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw) 的 REPLACE_EXISTING 和 WRITE_THROUGH 完成同目录替换。准确新状态且没有临时文件时不执行替换。

替换可能返回错误或迟到的取消，因此步骤在仍持有父目录和 Prepared 句柄时，以不接受取消的观察复核实际文件。只有新 association 已准确提交且临时文件不存在才接受结果；状态无法证明时返回稳定 uncertain 诊断，保留持久阶段。候选、服务缺席、树权限及日志在退出前再次复核。全局机器独占、两份 App 屏障、私有日志和候选的持续持有仍是调用方责任。

新增 43 项测试覆盖完整前置条件、旧/新精确重放、替换确认丢失、错误后的旧或异常状态、日志/权限变化、取消后等待观察收尾及诊断脱敏。完整 Windows 安全子集 579 项通过，0 跳过；18 项目 Release x64 构建零警告、零错误；format 检查 1312 文件，零处变更，无工作区警告。

远端两个独立进程通过 16 个场景、332 项原生断言，包括准确替换、空白/部分/完整临时文件恢复、已提交状态清理、只读句柄下无替换重放，以及陌生归属、无关临时文件、异常权限、硬链接、符号链接、缺失文件和提前取消的拒绝。未涉及替换的文件字节及文件/目录安全描述符摘要均保持，普通日志可重新独占打开，所有树句柄计数和遗留探针进程均为零。

实际被测 Windows 程序集 SHA-256 为 `9102dd2bd8916981729998f4ab5957ba95cccf698691686b0ba7ab9068979c73`，来自直接 x64 项目构建；收据 `artifacts/verification/association-transfer-validation-m4q.json` 同时记录 Core 摘要。临时文件由种子进程构造后交给新进程恢复，该证据不等同于实际掉电测试。探针使用生产文件端口、机器树和全局锁，以及隔离目录、合成身份与载荷；没有验证完整 helper、候选签名或双 App 屏障，也没有启动服务或改动证书和代理。

关联迁移阶段已经实现，证书账本保留、Installer 状态 ACL 迁移和专用认证确认入口继续接入。正式执行门保持关闭，不能将本阶段验证作为完整换绑或正式安装验收。

## M4r 双账户证书账本保留

Core 的 `InstallerOwnerTransferCertificateState` 将普通活动账本、旧账户私有归档和目标账户私有归档作为三个准确状态。只接受 AssociationTransferred，并且只允许三项有序操作：先复制旧活动账本到私有归档，再原子启用目标账本或清除已归档的旧活动副本，最后移除已观察到准确活动副本的目标归档。目标没有旧账本时，活动位置最终为空，由后续普通安装创建其自身证书记录。

每一步都保留 LedgerId、TargetSid、证书身份、预先存在/安装器所有权、引用数和 generation，不能通过更改 SID 或释放仍在使用的引用完成换绑。活动位置提前消失、旧归档尚未存在却已启用目标、目标归档先于活动副本丢失，以及任何不属于本次记录的账本均拒绝。完成后移除目标的重复归档，避免后续普通修复更新活动账本时留下陈旧的第二份记录。旧账户归档仍保留有效引用。

Windows 的 `WindowsOwnerTransferCertificates` 复核同一候选、双方 profile、新 association、服务缺席和准确 Prepared，并检查已有目标账本与候选证书一致。只读机器树额外固定已有 InstallerAuthority/v1；共享根要求全部新 ACL，Installer/v2 及活动账本仍使用旧账户 ACL，association 和日志始终由句柄阻止写入及替换。该步骤不创建缺失的私有根、不改目录 ACL，也不导入或删除任一账户的实际证书。

私有归档位于 `InstallerAuthority/v1/certificate-owner-<SID的SHA256>.json`，每个账户只有一个固定位置。专用原生端口检查三个文件槽的准确现状及 Core 计算出的下一步，仅新建缺失的旧归档；活动文件通过同目录磁盘 flush 和原子替换更新。私有文件在创建时设置准确的 Administrators owner、受保护 SYSTEM/Administrators 两条规则；活动文件要求准确旧账户继承规则。所有文件均为单硬链接普通文件，限制 4 KiB，解析后还需重新编码逐字节匹配。公开异常不保留私有解析细节。

替换/删除的返回值不能单独推进状态。错误或取消后，在目录、association 和 Prepared 保护仍持有时以不可取消的读取检查整个三槽后置条件；不准确时停止，后续操作不会执行。只有当前调用创建且仍满足准确 ACL、链接及预期字节前缀的随机临时文件可以清理。已存在的未知临时文件保留；此实现不将未知文件当作恢复状态，也不承诺清除真实进程强杀留下的全部临时副本。

新增 Core 17 项和 Windows 40 项测试，覆盖全部可达/不可达槽组合、可选账本、复制后的独立恢复、引用与字节保留、三步确认丢失、异常后停止、最终边界变化和取消排空。完整 Core 791 项与 Windows 安全子集 619 项通过、零跳过；Core 行覆盖率 93.46%、分支 86.37%。18 项目 Release x64 构建零警告、零错误，format 检查 1319 文件零处变更，无工作区警告。

真实远端两进程在 21 个场景中通过 657 项断言，生产文件端口实际完成 24 次账本操作。种子进程在归档或激活后结束，新进程按实际状态继续；覆盖四种可选账本组合、已完成重放、陌生状态、归档缺失、硬链接、符号链接、异常文件/根权限、非规范 JSON、未知临时文件保留及取消。无关字节和文件/目录权限摘要保持，普通 Prepared 可独占重新打开，最终树句柄和遗留探针进程为零。

首次探针错误地只接受文件占用时返回 ERROR_SHARING_VIOLATION（32）；本机型上的实际 MoveFileExW 返回 ERROR_ACCESS_DENIED（5）。保留初次失败和单项诊断收据后，探针接受这两种拒绝结果，并增加“原证据保持后释放自身读句柄，同一组文件立即成功”的对照；在全新隔离目录完整重跑通过，未调整生产代码或文件权限。前两次失败不计入 657 项通过断言。

实际被测 Windows/Core 程序集 SHA-256 分别为 `0af4496ef79788e87a076b08a6afeed8115c07f7783f4b3506074e7fb720d0b0` 与 `c4f96df4b0732bc1d5c05652826cabb97176f12a41cf0076faafcebb4d8cd365`，来自完整解决方案构建。收据为 `artifacts/verification/certificate-state-transfer-validation-m4r.json`。探针使用合成身份、证书记录和载荷，没有修改证书存储、启动服务或改动代理；该结果不包含完整认证 helper、双 App 屏障、签名候选、实际掉电或旧账户独立卸载验收。

账本保留步骤已经实现；旧账户包的独立清理路径、Installer 状态权限迁移及专用认证确认入口继续接入。正式执行门仍关闭。

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
