# ClashSharp Installer 详细审查与 C# / WPF 重写路线

> 2026-08-31 状态更新：迁移期 Rust/Slint Installer 和 Rust Sandbox helper 已从仓库退役，C# / WPF Installer 成为唯一实现。本文对旧 Rust 路径的逐项审查仍作为历史威胁模型与迁移证据保留，但不再描述当前发布入口。当前 Windows checkpoint 已完成 helper-authoritative coordinator、authenticated broker/host、target-SID certificate/package、concrete machine operations、helper 启动入口、parent engine 与 production runtime 组合；该运行时仍由默认关闭的 formal-build 编译门阻断，显式跨 owner reassociation 与 signed VM E3/E4 尚未闭环。
>
> 接盘请先阅读 [ClashSharp Installer / WPF 接盘开工单](2026-08-30-installer-wpf-handoff.md)。它区分了最近真实 checkpoint、当前未编译增量、资源门禁和首个 P0 实现顺序。

- 审查日期：2026-08-30
- 审查基线：`main@edfd025a5b2237841f09d80d629ed66ff59b0d38`
- 审查对象：`ClashSharp/Installer`、`ClashSharp/SandboxTest`、安装打包脚本、Installer 测试边界及新 C# Installer 候选
- 目标：将唯一用户可见 Installer 迁移为 C# + WPF、`win-x64` self-contained single-file 绿色程序，同时不降低现有 Rust Installer 的安全不变量
- 证据口径：静态实现为 E1，Linux 可重复契约测试为 E2，目标提交 Windows CI 为 E3，最终签名制品在隔离 Windows VM 的真实安装与故障注入为 E4

## 1. 结论

现有 Rust + Slint Installer 不是应当直接丢弃的原型。它已经具备较强的包身份、payload allowlist、句柄锁定、防降级、提权边界和安装前滚 journal。C# / WPF 重写必须把这些能力迁移到独立 Core/Windows 层，WPF 只负责状态展示和用户意图；不能以更换 UI 技术为由退回“先算 hash、再按路径使用”、全程管理员、任意 PowerShell 参数或无恢复标记的普通安装脚本。

审查基线确认六个 P0；下列是发现时状态，当前推进情况见各节“2026-08-30 增量”：

1. payload 或 CER 缟损时，现有 UI 实际无法进入卸载；
2. 卸载没有 durable journal，并可能在清理完成前删掉 `association.json`，永久丢失继续清理的授权；
3. 证书不在事务中，没有 thumbprint/预先存在/Installer-owned 记录和卸载路径；
4. Service 与 RecoveryWatchdog 的正式 staging 仍是 framework-dependent；
5. 机器文件操作仍有 path-check 到 path-mutate 的 junction/rename TOCTOU；
6. 没有通过真实 Installer 入口的签名 Windows VM 故障注入门禁。

因此迁移采用并行替换：Rust Installer 保持发布 authority；新 C# Core 先冻结并扩展 durable protocol，新 WPF EXE 先以 fail-closed migration preview 落地。只有同一签名候选通过 install/repair/upgrade/uninstall/reboot/tamper/cut-point 的 E4 矩阵，才能切换正式打包入口和删除 Rust/Slint。

## 2. 已有能力与必须保持的不变量

| 维度 | 已有能力 | 主要证据 | C# 重写要求 |
|---|---|---|---|
| 运行环境 | Windows 11 x64 门槛、六语言、主题、Install/Repair/Uninstall | `Installer/src/main.rs:803-1021` | 环境探测与 UI 状态分离；卸载恢复不能被“安装目标 OS”或 package payload 缺失阻断 |
| 权限 | parent 保持非提权；仅固定 helper verb 跨 UAC；helper 不接收任意源/目标路径 | `main.rs:1023-1031,1487-1599,1645-1828` | WPF manifest 保持 `asInvoker`；helper 在 WPF 初始化前解析固定 CLI，进入后重新验证所有事实 |
| 发布信任 | 构建期从最终 MSIX 生成 exact identity/payload/注册包/机器文件清单 | `Installer/build.rs:277-490` | 清单必须嵌入已签名 EXE；sidecar manifest、显示版本和文件名都不是信任来源 |
| 运行期信任 | 拒绝 reparse/多余项；用普通文件句柄锁定对象并以 volume/file-index、长度和 SHA-256 复验 | `trust_anchor.rs:21-217,285-325,858-1038` | 使用 `SafeFileHandle` 和稳定 file identity；验证和所有消费者完成前不得释放 guard |
| 包身份 | 核对 name/version/architecture/resourceId/full-name/family/publisher/publisherId | `package_identity.rs:13-149` | 严格 DTO 在构造业务对象前一次性完成所有字段验证 |
| 版本策略 | 四段 `UInt16` 数值比较；普通 install/repair 拒绝降级 | `package_identity.rs:60-82`、`main.rs:940-974` | 不得改用字符串或 SemVer 比较，不得加入隐式 downgrade override |
| 安装事务 | `Prepared → PackageCommitted → MachineCommitted → Verified` 单向前滚并绑定 SID/version/hash | `installer_transaction.rs:11-169`、`main.rs:1831-2045` | 同阶段重放幂等，禁止跳跃/回退；任何 timeout、cancel、crash 或未知退出保留 marker |
| 并发与静默 | 每用户 operation/startup/recovery lock；全局 machine mutex；App 运行时拒绝变更且不强杀 | `main.rs:269-300,1044-1470,1656-1770` | 保留 check-lock-check；引入 generation 防止旧 refresh 覆盖正在执行的 UI 状态 |
| 进程树 | deadline、有界双流并发排空、Job kill-on-close、stdin 在 Job assignment 后释放 | `process_runner.rs:1-175,325-478` | Windows 版改为 suspended create → assign Job → resume；结果结构化表达是否确认终止 |
| Machine apply | 固定根、本地 MSIX exact extraction、stop/fence/swap、SCM tuple、最小 SDDL、owner/token association | `service_plan.rs:916-1524` | 优先 SCM/File/ACL Win32 API；文件和目录 commit 需要 flush/durability barrier |
| 构建 | 隔离 staging、固定 GeoData/mihomo、签名 Installer + RFC3161、失败清理 | `build.ps1:191-223,264-359,521-826` | locked restore；主 MSIX也要求 timestamp；最终发布路径重新验签和验 hash |

绿色 Installer 的制品边界固定为：一个用户可见、已签名、self-contained、single-file、`asInvoker` 的 WPF EXE，以及与该 EXE 内嵌信任锚精确对应的 sibling `payload`。Installer 不自安装、不写自身卸载注册项。普通 self-contained 多文件 WPF 目录不是默认正式形态，因为 CLR/WPF sidecar 会在应用代码有机会验证前被加载。

## 3. P0 缺陷详解

### P0-01：payload 损坏卸载不可达

现状：

- readiness 在 package 或 CER 缺失时直接进入 Failed：`Installer/src/main.rs:541-545`；
- Slint 卸载控件排除 Failed phase：`Installer/ui/main.slint:449-470`；
- 所有 action 包括卸载仍先经过安装目标 OS 门槛：`Installer/src/main.rs:660-672`。

影响：机器 service、association 或 package 已损坏时，用户最需要卸载，入口反而被 payload 完整性挡住。

关闭条件：卸载只要求“当前 Installer/签名 recovery bundle 对该产品和 owner 有权”，不要求待删除的 MSIX/CER 完整。缺失 package payload 的卸载测试必须走真实 UI → runtime → helper 路径，不能直接调用脚本。

2026-08-30 增量：Rust parent 与 elevated helper 已对 Uninstall 绕过安装目标 OS 检查，Uninstall 不再加载/验证 sibling payload；Failed/Blocked phase 以及 durable uninstall recovery pending 均保留卸载入口。源码契约测试和 Windows target `cargo check/clippy --tests` 已通过，但真实 UI → UAC → helper 的 E4 场景尚未执行，因此该项是“实现已推进、运行证据未闭环”。

### P0-02：卸载中断可永久丢失授权

现状：

- `may_uninstall_machine` 对 Missing/Invalid association 返回 false：`service_plan.rs:278-287`；
- 卸载脚本递归删除包含 `association.json` 的整个 service data root：`service_plan.rs:1784-1802,1857-1860`；
- 卸载没有等价于安装的 durable journal。

中断窗口：若 association 先被递归删除，而其他锁定文件、SCM delete-pending 或 ACL 清理随后失败，下次调用会把 Missing association 判为无权/no-op，残留失去自动收敛路径。

关闭条件：v2 journal 必须绑定 operation 和 owner SID；卸载先 durable `Prepared`，再删除 service/machine payload/package/certificate-owned 内容，authorization tombstone 与 association 最后单独删除。存在同一事务 tombstone 时，Missing association 必须继续前滚，不能 no-op。

2026-08-30 增量：Rust journal 已升级为 schema v2，加入 `operation` 与严格 `generation`，并实现卸载反向阶段 `Prepared → MachineCommitted → PackageCommitted → Verified`。首次卸载仍以 strict association 校验 owner，随后在任何机器删除前把受保护 journal 作为 authorization tombstone 持久化；association 删除或 helper/parent 崩溃后，同一 release + SID 可凭该 tombstone 重放机器清理。机器根、服务数据根与 SCM 均确认消失后才提交 `MachineCommitted`，目标包确认消失后才写 `Verified` 并精确清除 journal。Windows target `cargo check --tests` 与 Clippy `-D warnings` 已通过；PowerShell 执行、ACL、防断电持久性和 cut-point 仍需 E3/E4，所以不把该项标为最终关闭。

C# helper session protocol 的同批增量进一步消除了“收到命令即在内存中假定 phase 已推进”的窗口：每条命令现在必须经过 `Begin → Complete`，只有 terminal result、result journal 与 helper 重新读取的受保护 journal 三者精确一致后，session 才接受后继状态。首次或恢复 bootstrap 必须绑定构造时读取的 exact protected state；仅当命令 journal 是已提交状态的精确幂等前驱时才进入 `VerifyCommittedReplay`，用于回执丢失后的 postcondition 复核。稳定失败只能保持请求前状态，传输/进程中断必须通过 `ReconcileAfterAbort` 在 exact pre/post state 间收敛，不能猜测成功；卸载 `MachineRemovalAuthorized` 也不能仅凭 parent 命令构造。新增固定 `commit-package` verb 后，install/repair 只能从 protected `MachineReserved`、uninstall 只能从 protected `MachineCommitted` 由 helper 独立查询 exact target-user package 结果并提交 `PackageCommitted`；parent 直接携带伪造后继 journal 会被 session 拒绝。`CommitPackage` 已显式覆盖原先 package 外部推进需求，所以两条已完成命令之间只接受 exact protected snapshot；状态被外部推进、丢失、回退或换身份均 fail closed。对应正常、失败、回执丢失、postcondition drift、伪造授权/package phase、transaction/identity/regression 与命令间状态偷跑矩阵已纳入已验证的 499/499 checkpoint。此时尚未接入 authenticated broker、生产 helper 入口与 helper-authoritative protected clear，因此仍只是协议层推进，不是 P0-02 闭环。

随后新增的 Windows protected-state root 已把 Core store 接到具体的 `%ProgramData%\ClashSharp\Installer\v2` 安全边界：本地盘符固定路径、共享产品根的可信 owner/无 DELETE_CHILD/ACL takeover anchor policy、`Installer/v2` 严格 DACL、逐级 `OPEN_REPARSE_POINT` 观测、全链不共享 DELETE 的持久句柄、首次双观测及每次 store 操作前复核。transaction journal 与 certificate ledger 由同一 guard/根/生命周期组合，预置但不匹配的对象只拒绝、不自动修 ACL。完整设计见 [Installer protected state root](../design/2026-08-30-installer-protected-state-root.md)。首个 76 项 Windows checkpoint 已完成 Windows 11 x64 Release 交叉编译；随后补入 store composition、ACL 并发漂移、路径矩阵、machine-helper IPC ACL/PID 及 exact-target-SID package commit inspector 测试，当前 Windows 静态展开计数为 103，最新 checkpoint 因 standard 资源门不足而尚未重新编译。pipe primitive 已写但 authenticated broker、helper 入口和 protected clear 仍未接入，因此 P0-02 仍未闭环。

### P0-03：证书生命周期不在事务内

现状：证书导入发生在 machine prepare 与 Add-AppxPackage 之前，见 `service_plan.rs:73-86`、`main.rs:687-751`。owner 冲突、UAC 取消或 prepare 失败时已经可能留下证书；`main.rs:900-909` 没有记录 thumbprint、是否预先存在、是否由 Installer 添加，也没有卸载删除路径。

关闭条件：`Prepared` 必须早于证书 mutation；journal/持久 ownership ledger 记录 exact thumbprint、store、pre-existing 和 installer-owned。卸载只删除本 Installer 添加、签名策略仍匹配且无其他受管安装引用的证书。

2026-08-30 增量：Rust 发布 authority 的 install/repair 计划已经改为先持久化 machine `Prepared`，再导入 CurrentUser 证书，避免 owner conflict、UAC 取消或 prepare 失败发生在未记账的证书变更之后。C# Core 新增独立 schema v1 certificate ownership ledger，绑定 exact target SID、固定 `CurrentUser/TrustedPeople`、uppercase SHA-1 thumbprint、完整 DER SHA-256、pre-existing/Installer-owned、0/1 managed reference、generation 和随机 ledger identity；strict bounded JSON 拒绝未知/重复/错大小写/整数 enum，文件存储使用受保护根、写穿透、同目录替换、read-back 和 exact-content-hash CAS。`DurableInstallerCertificateMutation` 在 import/delete 前先写账本，pre-existing 证书永不删除，原预存证书消失后 Repair 会先转换为 Installer-owned 再导入；卸载先把引用降为 0，之后只删除 exact 双重身份并在复核后清账本。C# coordinator 的顺序已进一步固定为 install/repair `Prepared → machine reservation → certificate → package → machine → final verify`，uninstall `Prepared → owner-checked removal authorization → machine → package → certificate → final verify`，每个 replay 点均为幂等测试。

2026-08-30 后续增量：`ClashSharp.Installer.Windows` 已实现固定 `CurrentUser/TrustedPeople` 的 `X509Store` adapter；只接受与原 request、当前 Windows SID、内嵌 manifest 和具体 Windows handle lease 同时匹配的调用，证书以 uppercase SHA-1 thumbprint 和完整 DER SHA-256 双重识别，导入只读取仍被锁定的 CER handle，删除遇同 thumbprint/不同 DER 时拒绝继续。SafeHandle、certificate、MSIX metadata/machine hash、PackageManager 和 elevation adapter 已在 Linux 上以 Windows 11 x64 TFM 完成 Release 交叉编译，0 warning/0 error。Windows 专用往返测试已写入 CI，但尚未在当前候选提交的 Windows runner 执行。

同批后续静态增量已补 `WindowsCurrentUserPackageStoreAdapter`：只调用 `FindPackagesForUser(string.Empty, exactFamilyName)`，0 个结果才视为未安装，多个结果、非主应用包种类或任一派生身份不一致均 fail closed；部署使用锁定文件的本地 `file:` URI，并显式关闭 unsigned、developer mode、force shutdown、force downgrade、deferred registration、partial content 和 stage-in-place。`LimitToExistingPackages` 没有被误用于 Windows 11 build 22000 基线：微软文档说明它对直接 package URI 不生效，且较晚才引入；离线依赖由 exact locked `DependencyPackageUris` 保证。调用交给 AppXSVC 后不再把 caller token 传给 WinRT operation，而是等待终态后由 Core 在 durable cut-point 处理取消，避免 UI 取消导致 SafeHandle lease 提前释放。依据见 [AddPackageByUriAsync](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.addpackagebyuriasync)、[AddPackageOptions](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.addpackageoptions)、[FindPackagesForUser](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.findpackagesforuser) 和 [RemovePackageAsync](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.removepackageasync)。这些代码与测试已取得 Linux→Windows 11 x64 Release 交叉编译证据，但没有在 Windows 执行，不能记为 E3。

该项的 C# ownership transaction、helper-only target-SID adapter、parent 只读后置条件与 production composition 已接入源码；但默认正式产物仍不启用 mutation runtime。当前确定性测试不能证明真实 CurrentUser store、alternate-admin OTS、ACL、UAC 或卸载行为，仍须在同一签名候选上完成 pre-existing/owned/collision/cut-point 的 E3/E4 矩阵后才能关闭 P0-03。

### P0-04：干净机器 .NET 依赖未闭合

`Installer/build.ps1:458-516` 明确以 `--self-contained false` 发布 Service/Watchdog。干净 Windows 11 没有 .NET 10 Runtime 时，Installer 自身即使绿色，Service 与恢复链仍无法启动。

关闭条件：Service 和 Watchdog 使用固定 RID self-contained 输出，或 Installer 以 exact signer/version/hash 的可信前置依赖完成可恢复部署；必须在没有 .NET 10 SDK/Runtime 的干净 VM 验证。

2026-08-30 增量：正式打包已改为 `win-x64`、self-contained、single-file、ReadyToRun、非裁剪，并把 native runtime 纳入单文件；Service/Watchdog staging 和最终 MSIX allowlist 均只接受各自一个 EXE，不再接受 `.dll/.deps.json/.runtimeconfig.json` sidecar。两个组件 csproj 的 formal-publish target 会在 publish 后拒绝任何额外文件，Windows CI 新增实际 publish 验证。Linux 上相关项目普通交叉编译成功，但 heavy 门禁未通过，未在本机执行正式 publish；无 .NET Runtime 干净 VM 尚未验证，所以该项是“构建契约已修复，E3/E4 待闭环”。

### P0-05：特权目录仍有 TOCTOU

`service_plan.rs:1241-1273` 先按路径检查/创建/设 ACL；`1444-1455` 再用 `Move-Item` 交换 payload。攻击者或并发参与者可在 check 与 mutate 之间替换可重命名祖先或 junction。

关闭条件：锁定所有可重命名祖先，拒绝 reparse；尽量从已验证目录 handle 相对打开/创建；每次 commit 前后比较 file identity；目录交换和持久标记具备明确 flush 屏障。

### P0-06：真实 Installer VM 门禁缺失

独立 Rust integration test 目前主要是 GeoData manifest：`Installer/tests/geodata_manifest_contract.rs:1-73`。`SandboxTest` 的既有路径仍可直接 `Import-Certificate` + `Add-AppxPackage`，绕开用户实际会运行的 Installer。没有真实 UAC、AppXSVC、SCM、ACL、reboot 和每个 cut-point 的故障注入。

关闭条件：Sandbox/VM 只接受签名 release-artifacts 完整树，运行同一个 Installer EXE；必需场景 skipped 或报告未知字段均失败；结果绑定精确 artifact digest。

## 4. P1 / P2 风险

### 4.1 进程与提权

- `RunAs` helper 跨 parent Job；超时可能只杀掉承载 `Start-Process -Wait` 的 PowerShell：`process_runner.rs:8-10`。
- runner 没有 caller cancellation，mutation 最长同步等待 15 分钟：`main.rs:68-71`、`process_runner.rs:30-75`。
- timeout 的 terminate/reap 是否成功只存在字符串 detail，协调器无法区分“确认结束”和“状态不确定”：`process_runner.rs:97-117,331-351,625-649`。
- `Process.Start` 后再 assign Job 留下短窗口；无 stdin 路径在 assign 失败但 direct child 已退出时可能被当作成功：`process_runner.rs:137-175`。

C# contract 必须提供 `Uncertain`/`RecoveryPending` 结果。取消只有在参与者证明终止或明确提交点时才可显示普通 Cancelled；否则 UI 显示恢复必需，journal 不清除。

### 4.2 Durable commit 与最终验证

- machine payload copy、目录 swap、association replace 缺少显式 `FlushFileBuffers` 等持久化屏障：`service_plan.rs:1336-1501`；
- Rust 最终独立核查主要是 association owner + Service Running：`main.rs:2022-2039`；
- association reader 的 owner/DACL 检查弱于 journal：`main.rs:2508-2542`；
- 固定 `$rollForwardOnly = $true` 使大段 rollback 不可达，但测试仍以字符串存在作为保证：`service_plan.rs:916-924,1553-1654,2165-2207`。

最终 Verified 应核对 exact machine payload、目录/文件 ACL、association/tombstone、完整 SCM tuple，并通过带 transaction nonce 的 IPC readiness handshake；瞬时 Running 不足以证明服务可用。

### 4.3 构建与供应链

- 手工设置 `CLASHSHARP_INSTALLER_PACKAGING_MODE` 可绕过“必须由 build.ps1 进入 release build”的声明：`build.rs:233-243`；
- official 主 MSIX 只要求 trusted，未像依赖和 Installer 一样要求 timestamp：`build.ps1:605-634,771-793`；
- Installer 验签到 release copy 使用普通路径，最终目录未重新验 Authenticode：`build.ps1:781-816`；
- payload/注册包遍历缺少目录数与递归深度预算：`trust_anchor.rs:413-505,591-670`；
- official signer thumbprint/publisher 来自调用环境，必须由受保护 release environment 管理，不能视为普通参数。

### 4.4 UI 与可访问性

- 现有 refresh/language/action 没有统一 generation/cancellation，旧 refresh 可能覆盖 busy/phase：`main.rs:349-363,481-651`；
- Slint 固定 `288×432`，长详情不可滚动；主要交互为自绘 Rectangle/TouchArea，键盘、UIA、高对比度语义不足：`ui/main.slint:34-100,399-595`；
- 六个语言包的自动检测显示文本目前全部相同，见 `main.rs:2743,2795,2847,2899,2951,3003`；
- UI 没有持久、脱敏诊断日志，也没有“取消请求”与“已确认停止”的区别。

WPF 候选必须使用原生 Button/ProgressBar/ScrollViewer、明确 AutomationProperties、PerMonitorV2、高对比度系统资源、可复制的稳定诊断码和 generation-based latest-wins。原始异常、用户路径、token 与命令行不得直接进入 UI。

2026-08-30 后续增量：根据实际功能只有 Install/Repair/Uninstall 三个维护动作、且任一时刻只有一个 ClashSharp 产品实例，WPF shell 已收敛为一个产品卡片，而不是工作负载 tab、多产品列表或向导页。最新静态 UI 又删除了只有一个产品时没有导航价值的伪标签栏和卡片上方重复分组标题；窗口现在只有顶部品牌/只读刷新与唯一产品卡片，产品名是唯一 Level1 heading，卡片状态是 Level2 live heading。卡片上半部固定产品身份、版本和状态，下半部固定可信前提、位置、进度、诊断码和状态相关动作。可信产品态只有三个：`Available` 只显示“安装”，`Installed` 在同一卡片显示主“修复”和次“卸载”，`RecoveryRequired` 只显示待恢复事务的精确“继续安装/修复/卸载”；任何 inspection/mutation 执行中隐藏维护动作并只显示“取消”。未知组合、普通状态携带 recovery operation、恢复态缺少或携带未知 operation，以及 `CanExecute=false` 均 fail closed。视觉层级参考 Visual Studio Installer 的“产品实例卡片 + 主维护动作”信息结构，但不复制 Microsoft 商标、图标或像素资产。参考依据为微软的 [Modify Visual Studio](https://learn.microsoft.com/en-us/visualstudio/install/modify-visual-studio?view=visualstudio)、[Install Visual Studio](https://learn.microsoft.com/en-us/visualstudio/install/install-visual-studio?view=visualstudio) 和 [Repair Visual Studio](https://learn.microsoft.com/en-us/visualstudio/install/repair-visual-studio?view=visualstudio) 文档。

纯 Presentation 状态机已从 WPF 程序集拆成独立 `net10.0` 项目，新增直接引用编译产物的确定性测试，覆盖三个 operation 的单卡片映射、fail-closed readiness/result、single-flight、取消、异常脱敏、snapshot 防外部修改以及“已完成后排队 progress 回调倒灌”的竞态。原先入口安全性只靠读取 `Program.cs` 并比较 parser 与 `new App()` 的字符串位置；现已把 pre-WPF 分流提取为可直接执行的 `InstallerStartupRouter`，行为测试分别证明普通启动只创建 UI、合法 helper 启动只创建 privileged branch、非法 helper grammar 两者都不创建，源码契约仅保留最薄的最终接线检查。WPF executable manifest 原使用了不存在于微软支持表的 compatibility GUID；现已改为 Windows 10/11 官方共用的 `{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}`，并由项目契约测试同时锁定 Windows 11 build 22000、x64、self-contained single-file、`asInvoker` 和 PerMonitorV2。该 GUID 的官方依据见 [Application manifests](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests#supportedos)。真正的最低版本拒绝仍由 `RtlGetVersion` + native architecture probe 和 Core policy 执行，manifest 声明本身不充当授权。

OOM 审查还发现 Presentation 原先会把所有 `Exception` 降级成普通 UI 失败，从而可能在 `OutOfMemoryException`、`StackOverflowException`、`AccessViolationException` 或 `AppDomainUnloadedException` 后继续运行不可信状态。ViewModel 与 async command 现均只脱敏可恢复异常，致命异常在释放 single-flight 状态后继续传播；测试使用人工构造的 `AccessViolationException` sentinel，不实际制造 OOM。

正式 WPF 项目已新增固定逻辑名 `ClashSharp.Installer.ReleaseManifest.json` 的条件式 `EmbeddedResource`，`ClashSharpFormalInstallerBuild=true` 时缺少输入会在 build 前失败。运行时 loader 只接受程序集内恰好一个、大小不超过 64 KiB 且能被生产 strict codec 解析的资源，不读取 sidecar。`PackagingContract.psm1` 新增基于 UTF-16LE SHA-256 的 PublisherId 派生和 final MSIX/payload 生成器；清单现同时绑定包内固定 7 项 machine payload（mihomo、service、GeoData manifest 与四项数据）的 canonical lowercase path、展开长度和 SHA-256。生成器以 64 KiB buffer 流式读取仍打开的 MSIX ZIP，拒绝 traversal、空项、大小写碰撞、额外 service/GeoData 项及单项/总量越界；Core verifier 在同一个调用方持有的主包 stream 尚未释放时独立重算并比对。2026-08-31 清单已升级为 schema 2 并绑定 Installer Authenticode certificate thumbprint；C# one-EXE pipeline 是唯一候选路径，但 production promotion 仍受 signed VM 矩阵阻断。

机器事务顺序已改为在任何 CurrentUser 证书或 MSIX 变更前先持久化 `Prepared`，再由提权边界执行 owner/residue 预留，成功后才提交 `MachineReserved`；Install/Repair 的完整顺序因此是 `Prepared → MachineReserved → PackageCommitted → MachineCommitted → Verified`。卸载不再让普通 `Prepared` 直接授权删除：helper 必须先核验 association owner 并把独立 `MachineRemovalAuthorized` 阶段持久化，之后才允许 Remove，完整顺序为 `Prepared → MachineRemovalAuthorized → MachineCommitted → PackageCommitted → Verified`。这样 association 删除后崩溃仍有明确 tombstone 可重放，而尚未经过 owner 核验的 Prepared 不能伪装成删除授权。普通 Repair 仍要求已安装包，但同一 durable Repair 恢复可在包已丢失时继续修复。

2026-08-30 组合根复审进一步发现一项 authority mismatch：WPF manifest 固定 `asInvoker`，protected root 的 exact target-user ACE 又刻意只有 `ReadAndExecute`，原 coordinator 却由 parent 写入 Prepared/mirror/clear。2026-08-31 已按不放宽 ACL 的方案关闭：parent 只构造首个 canonical Prepared 并只读 reload/exact-compare；helper authority session 在任何副作用前唯一持久化 Prepared，并唯一拥有 journal/ledger transition 与 terminal clear。UAC-before-Prepared、response-loss、clear ack-loss、成功空读取和读取失败 fallback 均已有 fault-matrix 证据。

OTS 证书边界已落实为 native target-SID adapter：helper 仅通过 `CERT_SYSTEM_STORE_USERS` 打开 `<exact target SID>\TrustedPeople`，并继续执行 thumbprint + DER SHA-256 双重身份策略；injected 读取/导入/删除/冲突/ack-loss 测试已跑绿。它不再依赖 alternate-admin helper 自身的 `StoreLocation.CurrentUser`，但 Windows 11 标准用户 profile + alternate-admin OTS 真机证据仍是启用前门槛。依据与实现门禁见 [Installer machine-helper IPC boundary](../design/2026-08-30-installer-machine-helper-ipc.md)。

权限分工重构与 production composition 已完成：`IInstallerTransactionReader` 是 parent 的唯一 capability，Windows protected stores 只把 writer 交给 helper authority factory；coordinator 不再调用 Save/Clear。WPF 只经 `ProductionInstallerRuntime` 调用可信 parent engine，且该分支仍受默认关闭的编译门约束。

helper-only package commit 已实现 exact-target-SID inspector：Windows facade 接收 journal 的 exact `TargetSid`，并复用 one-registration/full identity/health fail-closed policy；现有 parent adapter 仍固定传 `string.Empty`，两条边界由测试分别锁定。alternate-user AppXSVC 真实运行证据仍待隔离 VM，因此不能把 deterministic inspector 测试记为 E4。

提权协议也从“只有哈希字段”推进为可由 helper 独立复算的闭合命令：每个不超过 4 KiB 的 canonical JSON command 同时携带固定 verb、transaction ID、journal SHA-256 与 canonical journal bytes（Base64），helper 可恢复 operation、SID、release identity、phase 和 generation，并拒绝 bytes/hash/CLI bootstrap 不一致。成功 result 不再只回显请求哈希，而是同时携带 helper 已提交的 canonical result journal 与独立 SHA-256；Prepare 只能提交 `MachineReserved`/`MachineRemovalAuthorized`，Apply/Remove 只能提交 `MachineCommitted`，Verify 只能提交或重放 `Verified`，失败 result 只能回显请求阶段。Coordinator 再计算唯一合法后继并 exact-compare，陈旧、跳跃、跨 transaction 或 bytes/hash 不一致的回执均不能推进 parent mirror。journal wire 上限由与 Base64 framing 不一致的 4 KiB 收紧为 1 KiB，并用最大合法 SID 和 `65535.65535.65535.65535` 证明 command/result 仍落在各自 4 KiB 上限内。命令与结果用 4 字节大端长度帧复用同一持久流，pipe session name 只从随机 256-bit transaction ID 派生，因此跨 Prepare/Apply/Verify 阶段保持稳定。新增 session guard 允许合法 recovery phase 作为首次命令、允许同阶段幂等重放，但拒绝跨 transaction、immutable identity 替换和 generation 回退；已是 `Verified` 的恢复只做一次最终复核，不会为重复复核再触发一次 helper 生命周期。

transaction、association、command 与 result codec 已移除反射式 `JsonSerializer` 依赖并改用显式字段 writer/parser；WPF 外壳同时设置 `StartupHookSupport=false`。managed entry point 会在创建 WPF `Application` 前解析 helper grammar；只有 mutation-runtime 编译门启用时才加载唯一内嵌 manifest 并进入 `WindowsInstallerMachineHelper`，默认产物即使收到合法 helper grammar 也稳定失败。真实 helper 集成方法始终参与编译，避免 gated 分支腐化。按“WPF 绿色单文件”最终方向，`runas` launcher 只接受绝对 canonical `ClashSharp.Installer.exe`，复用同一已签名映像的 helper 分支；它使用 STA、`UseShellExecute=true`、固定 `ArgumentList`，并把 Win32 1223 明确映射为 UAC 用户取消。

严格八参数 parent-PID bootstrap、protected pipe DACL、first-instance server 与双向 PID primitive 已组合为 persistent parent broker、client/server 和 helper host；same-EXE final-path/Authenticode lease、bounded deadline、退出观察与 uncertain reconciliation 已有 deterministic 测试。`WindowsMachineHelperMachineOperations` 已组合 SCM/ACL/payload/association 原语，并覆盖 profile 缺失时“服务必须先证明 absent”的固定根卸载。仍未闭环的是显式双 owner reassociation、默认 production gate 的证据化启用，以及真实 signed VM 矩阵。

## 5. 测试集质量审查

### 5.1 现有 Rust Installer 测试

审查基线 CI 展开为 91 个 Rust test；本次候选源码现有 94 个 `#[test]`（新增 payload/OS 卸载可达、卸载反向事务与持久授权顺序），最终展开数仍以 Windows CI 为准。优点：

- journal strict codec、phase matrix、repair escalation 较完整：`installer_transaction.rs:277-530`；
- trust anchor 使用真实临时文件、句柄 sharing 和 reparse 测试：`trust_anchor.rs:1076-1323`；
- runner 覆盖双流排空、截断、stdin EOF、deadline 和 Windows descendant Job：`process_runner.rs:922-1075`；
- package identity、owner policy、pipe vector 适合迁移为 differential vectors。

主要问题：

- `main.rs:3590-4127` 至少 15 处 `include_str!`/源码顺序断言；
- machine apply/uninstall 多为 `script.contains(...)`：`service_plan.rs:2165-2276`，无法发现不可达 rollback、部分递归删除或瞬时 Running；
- Windows PowerShell 测试只验证 parser syntax：`service_plan.rs:2238-2248`；
- 没有 UI Automation、键盘、Narrator、高对比度、DPI、文字缩放测试；
- 没有 cancellation、assign race、termination-unconfirmed、双流读取失败和 exact-limit 边界矩阵；
- 没有 line/branch threshold 或关键状态机 mutation test。

### 5.2 新 C# 测试边界

`ClashSharp.Installer.Tests` 直接引用 `ClashSharp.Installer.Core` 编译产物，不 source-link 生产文件，也不定义测试专用生产分支。首批测试覆盖：

- 四段 `UInt16`、前导零、SID 文本和 lowercase SHA-256；
- strict JSON 的完整属性集、大小写、未知字段、重复字段、注释、尾逗号、整数 enum、空/超限；
- install/repair 的 machine reservation → certificate → package → machine 与 uninstall 的 owner authorization → machine → package → certificate 反向阶段矩阵；
- exact-release resume、cross-release conflict、downgrade、package 缺失卸载；
- mutation fault、cancel、uncertain helper、already-committed replay、Verified 再核查后 exact clear；
- compare-and-swap file journal、read-back 和 verified-only clear；
- operation × mutation/reverify/final-verify cut-point matrix，以及每个合法 durable phase 的最小恢复重放；
- journal 路径伪装成目录、畸形/超限持久文件、跨 transaction identity 替换和 exact id+hash clear；
- certificate ownership 的 pre-existing/owned/消失后接管/0-reference 单向 generation，strict codec、CAS file store、exact thumbprint + DER hash 冲突；
- import/delete 前写账本、导入或删除产生副作用后崩溃、重放不重复 mutation、无 ownership evidence 的卸载绝不碰证书；
- Windows client/server、native build 22000 边界、OS/process x64 与 ARM64/x86/unknown 拒绝；
- embedded release manifest 的 exact role/path/order/identity、外层与包内 machine 文件逐项/总量预算、unknown/duplicate/case-changed JSON、locked file exact-set 与 lease disposal；
- single-flight，不依赖固定 sleep 或墙钟延迟；需要保持未完成状态的 fake 只用可取消的无限 Task 与 `TaskCompletionSource` 驱动。
- canonical journal bytes/hash 自洽、helper command/result 双 journal exact JSON、1 KiB journal → 4 KiB IPC 尺寸闭合、helper-authoritative 后继、4 KiB 长度帧、多命令单流、bootstrap/monotonic session、跨 transaction/identity/regression 拒绝；session command 在 result + exact protected-store reload 前不推进，ack-loss 只允许 exact committed predecessor replay，stable failure/abort 不合成后继状态。
- 卸载 UAC 在 owner-check/removal-authorization 阶段取消时仍停留 `Prepared`，机器、MSIX 和证书删除均不可达。
- install/uninstall 的 Prepare、machine 和 final 三个边界若返回陈旧 journal，parent 均保持上一已证阶段并拒绝 clear。

2026-08-31 当前 Core checkpoint 已在 Release、单 MSBuild 节点下实际完成 548/548，0 skipped；它覆盖 helper-authoritative coordinator/session、`CommitPackage`、每个 committed boundary 的 ack-loss、replay postcondition drift、命令间 protected state 偷跑/丢失矩阵，以及 production inspection contract。此前 499 项 checkpoint 的 Cobertura 为 line 94.90%（3108/3275）、branch 85.67%（1495/1745）；源码增长后不能把旧覆盖率百分比直接继承到当前 checkpoint，下一次 promotion 前必须重新采集并满足 line 90% / branch 80% 门槛。

MSIX verifier 已下沉为跨平台 Core 边界：测试覆盖主包/应用身份、PackageIntegrity、依赖声明与框架身份、必需 ZIP 条目缺失/改名/重复、非法 UTF-8、DTD、重复 XML 元素、non-seek stream 和 cancellation，并逐一覆盖 7 项 machine file 缺失与 hash 篡改、额外 service/GeoData 项和大小写碰撞。release-manifest 测试锁定固定 7 项集合、ordinal 顺序、Windows 歧义路径、GeoData/executable/combined 边界，以及 schema 2 的 Authenticode thumbprint。上述用例已包含在当前 548/548 Core checkpoint 中。

当前实际 checkpoint 为 `ClashSharp.Installer.Tests` 548/548、`ClashSharp.Installer.Presentation.Tests` 89/89，以及明确排除 `WindowsCurrentUserCertificateStoreAdapterTests` 的 Windows 安全集 348/348。Windows 与 WPF 生产项目 Release 编译为 0 warning/0 error；Windows 安全集覆盖 broker/host/operation executor、target-SID certificate seam、environment/process、parent engine、deployment/profile/root、profile-independent removal、locked archive/payload swap、SCM verifier/mutation、association 和 empty-root cleanup。测试使用 injected native seam 或随机临时目录，没有调用真实 SCM、AppXSVC、Program Files 或证书 mutation。会触碰开发主机 CurrentUser TrustedPeople 的测试仍只允许在隔离环境执行。

WPF 单卡片项目已完成 Windows 11 x64 Release 编译，0 warning/0 error；这只验证项目/XAML 编译边界，不代表执行过 WPF runtime 或 UIA。Rust/Cargo/Slint 源码和构建入口已删除，因此不再存在 Cargo checkpoint。现有 deterministic 与 temp-only 证据不能外推到 UAC、AppXSVC、真实 certificate/SCM/ACL mutation 或签名 VM。

后续测试分层固定为：

1. Linux `Installer.Core.Tests`：pure policy、codec、phase、manifest、fault-state matrix；
2. Linux `Installer.Presentation.Tests`：直接引用纯 Presentation 产物，验证少状态单卡片、generation/single-flight/cancel/progress/result，不 source-link WPF；
3. Windows 非提权 `Installer.Windows.Tests`：PackageManager adapter contract、file identity/reparse/rename、CurrentUser certificate、suspended Job tree；
4. Windows WPF UI Automation：键盘、焦点、Narrator、高对比度、DPI/文字缩放和长文本；
5. Windows 管理员临时 VM：真实 cert/Appx/SCM/UAC/reboot；
6. 每个持久化 cut-point 强杀 parent/helper/package child 或断电，再用同一 release Repair/Uninstall 证明收敛；
7. 已迁移的 canonical vectors 由 C# contract tests 保留；不得为差分测试重新引入 Rust 构建入口；
8. 任何必需场景 `skipped` 视为失败。

## 6. C# / WPF 目标边界与当前落地

| 项目 | 目标责任 | 当前状态 |
|---|---|---|
| `ClashSharp.Installer.Core` | `net10.0` immutable request/journal、strict codec、operation-specific state machine、certificate ownership、manifest/budget、sanitized result、平台端口 | helper-authoritative coordinator/session、helper-only package commit、certificate ledger、schema-2 locked release 与 fault matrix 已完成；Release 548/548 |
| `ClashSharp.Installer.Presentation` | `net10.0` 单卡片状态机、命令、readiness/result 边界 | strict startup router、精确 allowed-operation、三态恢复优先、single-flight/cancel/fatal exception 边界已完成；Release 89/89 |
| `ClashSharp.Installer.Presentation.Tests` | 直接引用 Presentation 的状态/并发/异常、startup routing 与 executable contract 测试 | 89/89、0 skipped；覆盖 production runtime、受限平台仅卸载、关闭取消、runtime lifetime 与 packaging activation gate |
| `ClashSharp.Installer` | `net10.0-windows10.0.22000.0` WPF 单产品卡片、UIA、高 DPI、Windows platform probe、内嵌清单 loader | C# 紫色单卡片 shell、helper 路由与 gated production composition 已落地并完成 Release 编译；默认产物同时禁用 UI mutation runtime 与 helper authority |
| `ClashSharp.Installer.Tests` | 直接引用 Core 的 pure contract 与 coordinator tests | Release 548/548、0 skipped；旧 499 项覆盖率需在 promotion 前重采 |
| `ClashSharp.Installer.Windows` | SafeHandle release/payload lease、target-SID cert/package、protected state、authenticated broker/host、machine mutation | broker/host、EXE trust lease、target-SID adapter、machine-operations composition、environment/process inspection 与 parent engine 已实现；跨 owner reassociation 仍 fail closed |
| `ClashSharp.Installer.Windows.Tests` | Windows adapter/integration 与 PowerShell→C# build-contract 互操作 | 安全集 348/348、Release 0 warning/0 error；会修改 CurrentUser certificate store 的 3 项测试明确排除，只允许在隔离 VM 执行 |
| VM harness | 签名 candidate 的真实 fault/reboot matrix | 未开始 |

当前 C# journal 已把 operation 纳入 immutable identity，并显式区分 `Prepared` 与 `MachineRemovalAuthorized`，只有后者可进入 Remove。coordinator 只持有 read-only transaction view，helper authority session 独占 Prepared、phase CAS、certificate ledger 与 verified clear。authenticated broker/host、EXE trust lease、target-SID certificate/package inspector、machine operations、helper 启动入口、parent engine 与 production runtime 均已完成定向测试。P0 当前集中在显式双 owner reassociation、Windows ACL/SCM/UAC/OTS/reboot/cut-point E3/E4，以及通过证据后才允许启用默认关闭的 production gate。

WPF 项目设置固定 `win-x64`、`SelfContained=true`、`PublishSingleFile=true`、`PublishTrimmed=false`、`PublishReadyToRun=true`、`asInvoker`、PerMonitorV2 与 formal-build embedded-manifest gate。当前 `MigrationPreviewInstallerRuntime` 返回 `CanExecute=false`，即使间接调用 Execute 也返回 Blocked；这是迁移期的安全状态，不是可发布 Installer 完成证据。

## 7. 迁移顺序

| 阶段 | 内容 | 退出证据 |
|---|---|---|
| M0 | strict v2 journal、package version、operation order、uncertain outcome、迁移后的 canonical vectors | Core tests；未知/重复字段与完整 phase matrix 全过 |
| M1 | `LockedPayloadVerifier`、manifest generator、迭代有界遍历、祖先/文件 handle 与 file ID | Windows reparse/rename/extra-file/limit/TOCTOU adversarial tests |
| M1 | `ProcessSupervisor` + `SafeJobHandle` | suspended create/assign/resume；有界双流；绝对 deadline；结构化 termination state |
| M2 | CurrentUser certificate/package transaction | pre-existing/owned ledger；payload 缺失 uninstall；Add/RemovePackageAsync fault matrix |
| M2 | authenticated `ElevationBroker` + `WindowsMachineMutator` | 固定 verb + SID；RunAs uncertain recovery；SCM/ACL/payload/association durable apply/remove |
| M3 | WPF production composition、`.resx` 六语言、诊断与 UIA | keyboard/Narrator/high contrast/100–300% DPI/text scale；关闭/取消安全点 |
| M4 | 唯一 C# packaging pipeline | locked restore、self-contained single file、sign/timestamp/final-path reverify、SBOM/provenance |
| M5 | signed artifact VM matrix | 同一 digest 的 clean install/repair/upgrade/reject downgrade/uninstall/reboot/tamper/cut-point 全过 |
| M6 | Rust/Slint 源码已提前退役；只启用 C# production authority | E4 完整，回滚手册和旧 pending journal recovery 已验证 |

## 8. Windows E4 验收矩阵

每个场景必须记录：candidate digest、OS build、target SID、起始 package/machine/certificate/journal 状态、注入 cut-point、进程终止确认、最终 package/SCM/ACL/association/certificate/app data 状态和稳定诊断码。

最低矩阵：

- clean install（无 .NET 10 Runtime）；
- same-version repair、upgrade、downgrade reject；
- payload/CER/dependency/manifest extra-file/reparse/tamper；
- package 缺失或损坏时 uninstall；
- pre-existing trusted certificate 与 Installer-owned certificate；
- UAC cancel、parent crash、helper crash、helper termination-unconfirmed；
- package commit、machine copy、SCM update、association/tombstone、certificate 和 journal 每个 cut-point；
- service 1072 delete-pending、锁定文件、reboot continuation；
- target App running、不同 SID、owner reassociation、Missing/Invalid association + residue；
- 100/200/300% DPI、键盘、Narrator、高对比度与长诊断文本；
- install 后 App 启动、system proxy、TUN、退出/重启恢复和无残留卸载。

## 9. Linux 本地验证与资源纪律

Linux 只能证明跨平台 Core、严格 codec、pure state machine、XML/SVG 和项目静态契约，不能证明 WPF runtime、UAC、MSIX、SCM、ACL、Authenticode 或 reboot。

2026-08-30 本地系统发生全局 OOM；内核记录显示 `cc1` 触发 OOM，victim 为 `WeChatAppEx`，但无法从记录归属到某个开发会话。后续所有 .NET 操作均由 standard 资源门与 host-wide lock 包裹：一次 Presentation 启动前因 MemAvailable 11776 MiB + SwapFree 508 MiB = 12284 MiB、比 12288 MiB 门槛少 4 MiB 而正确拒绝，未启动 dotnet；之后余量恢复时完成 locked restore、Presentation 39/39、Core 499/499、覆盖率采集、Windows 生产/76 项测试 checkpoint 及 WPF 的 Windows 11 x64 Release 交叉编译。Core 覆盖率门禁记录为 MemAvailable 12972 MiB、SwapFree 1 MiB、综合 12973 MiB，load 1.05、memory PSI full avg10 0.00。此后所有 protected-root/bootstrap/IPC 增量均被 standard 门禁阻止启动；交接前最后一次为 MemAvailable 6464 MiB + SwapFree 12 MiB = 6476 MiB，低于 12288 MiB。该检查仍显示无并发 worker、load 6.93、PSI full avg10 0.00、无最近 15 分钟可读 OOM。全程没有降低阈值、改用 light 或绕过门禁，也没有新增 OOM 或遗留编译进程。为避免长程任务再次被资源竞争破坏，新增 `eng/check-linux-resource-budget.sh`：

- `light`：小型静态检查/单项目验证；
- `standard`：普通 restore/build/test；
- `heavy`：完整 solution、WPF publish 或多尺寸高分辨率渲染；
- 检查 MemAvailable、SwapFree、memory PSI、按 CPU 数折算的 load、`/tmp`/workspace 空间、并发 build/test/render worker 和最近 OOM 冷却期；
- 任一失败即不启动任务，不以“应该够用”覆盖门禁；
- `-- command ...` 模式用 host-wide `flock` 覆盖“检查 → 命令完成”的整个窗口，避免多个 Agent 都在检查通过后同时启动；
- 本地大型验证串行执行，不与其他 Agent、Cargo、dotnet、ImageMagick 并发。

示例：

```bash
eng/check-linux-resource-budget.sh standard -- \
  /tmp/clashsharp-dotnet-10.0.201/dotnet test \
  ClashSharp/ClashSharp.Installer.Tests/ClashSharp.Installer.Tests.csproj \
  -c Release --no-restore
```

资源门禁是启动前条件，不替代测试本身，也不能作为 Windows E3/E4 证据。
