# ClashSharp 生产就绪执行计划

> 2026-08-31 更新：Rust/Slint Installer 与 Rust Sandbox helper 已退役并从受控树移除；自包含 C# / WPF Installer 是唯一实现。本文中仍出现的 Rust 路径、Cargo 门禁和跨语言向量仅记录迁移前证据，不再是当前执行入口。
>
> 当前 Installer / WPF 接盘入口：[ClashSharp Installer / WPF 接盘开工单](2026-08-30-installer-wpf-handoff.md)。2026-08-31 已恢复并超过旧 checkpoint，随后完成 Core helper-authoritative protected-state P0；Windows 生产 broker/进程组合仍继续推进。

- 更新日期：2026-08-30
- 审计起始基线：`main@fa72287`（仅标识计划建立时已存在的提交，不是本文自身提交）
- 当前候选：本文所在候选批次；其最终提交 SHA 由 Git 历史和 CI run 记录，不在本文自引用
- 目标：先形成可签名、可安装、可恢复、可验证的 Windows 生产闭环，再处理不阻断发布的完美化债务
- 项目地图：[`2026-08-27-project-development-map.md`](./2026-08-27-project-development-map.md)
- 详细审计来源：[`2026-08-10-development-audit-and-optimization-roadmap.md`](./2026-08-10-development-audit-and-optimization-roadmap.md)
- Installer 重写审查：[`2026-08-30-installer-wpf-rewrite-audit.md`](./2026-08-30-installer-wpf-rewrite-audit.md)
- 状态定义：`完成` = 已有自动化或人工证据；`待 Windows 验证` = 实现完成但未取得 Windows CI/真机证据；`阻断` = 不满足 RC 条件

## 1. 当前结论

项目的主体架构、Installer 权限边界、包身份、签名 payload 和 TOCTOU 防护已达到较高成熟度，但尚不能称为生产就绪。当前真正阻断发布的是发布闭环，而不是普通页面功能。

截至本计划建立时：

- P0-01～P0-05 已完成：Installer 是唯一部署 authority；禁止隐式降级；MSIX Identity 单一来源；签名 payload 精确 allowlist；校验到消费期间保持 immutable handle。
- MVVM composition root、page factory 和类型安全导航的核心收口已完成。
- Connections 与 core log 的 WebSocket 实时链路已经存在，早期“尚未实现”的记录不再有效。
- 审计起始基线 `main@fa72287` 的 Windows CI（2026-08-24，[run 32705511983](https://github.com/Water-Run/ClashSharp/actions/runs/32705511983)）中，.NET build/format/test、Installer fmt/clippy/test、Sandbox fmt/clippy/test 均已通过；唯一失败是 Installer 依赖审计中的 `webbrowser 1.2.1` 安全公告。
- 本文所在候选批次已把 Installer 锁文件更新到 `webbrowser 1.2.2` 和 `event-listener 5.4.2`，并删除 `quick-xml 0.39.4` 已升级后失效的两个 RustSec ignore；固定版 `cargo-audit 0.22.2` 无 vulnerability ignore 通过本地扫描，恢复主分支绿色仍需候选提交的 CI 证明。
- 仓库尚无正式 GitHub Release，也没有 release workflow、受保护 branch/tag/environment、签名门禁或真实 Installer VM 门禁；本候选已为独立 Installer Core 加入 line 90% / branch 80% 阈值，仓库其余关键域仍未统一覆盖率门禁。
- 干净 Windows 机器的 .NET 10 Runtime 部署策略尚未闭合；正式构建所需四项 GeoData 也缺少固定来源、版本、许可和预期 digest。

因此当前阶段定义为：**功能候选基本成形，发布候选尚未成立。**

2026-08-30 增量：`main@edfd025a5b2237841f09d80d629ed66ff59b0d38` 的 Windows CI run `33069360366` 已全绿，`.NET build, format, and test`、`Rust Installer`、`Rust SandboxTest` 已设为 `main` required checks。新的 C# Installer 迁移已落下 Core、WPF fail-closed shell 和直接引用真实 Core 程序集的测试；完整证据见上方 Installer 重写审查。该增量不改变“尚无正式 Release / E4”的结论。

## 2. 已推进的当前批次

| ID | 状态 | 内容 | 验收证据 |
|---|---|---|---|
| WB-000 | 待 CI 验证 | Installer `webbrowser 1.2.1 → 1.2.2`，关闭 `RUSTSEC-2026-0257`；`event-listener 5.4.1 → 5.4.2`；移除失效 RustSec ignore | locked metadata、无 vulnerability ignore 的固定版 `cargo-audit` 本地通过、Windows target clippy；提交后 CI 必须绿色 |
| WB-001 | 待 Windows 验证 | 开启后的托盘三态色彩指示：未知/未启用/待命灰色、已验证系统代理绿色、已验证 TUN C# 紫色 `#512BD4` | effective-state 解析测试、SVG/ICO 资产契约、Windows 托盘人工 smoke |
| WB-002 | 待 Windows 验证 | 主界面 Logo 改用 SVG；托盘采用 SVG 源和 8 档多分辨率 ICO 运行时产物 | XML 校验、ICO header/frame 校验、MSIX 精确资产清单 |
| WB-003 | 待 CI 验证 | 设置显示名称改为“启用托盘色彩状态指示”，旧存储键与布尔偏好保持兼容 | 六语言资源与资源完整性测试 |
| WB-004 | 待 Windows 验证 | Installer 统一 deadline、有界双流 capture、非提权进程树 Job 终止与 UAC 取消分类 | Linux 8 项独立 runner 测试（含 teardown grace/worker detach 上界和无无界 wait/join 静态契约）、Windows target clippy、Windows 非提权孙进程测试待 CI；`RunAs` helper 跨 Job 边界仍需 durable cut-point/VM 证据 |
| WB-005 | 待 CI 验证 | 四个 integration Probe 显式 test-only、不可 publish/pack；SandboxTest 不可发布 | 项目属性、`RepositoryTopologyTests` 架构门禁与 Cargo `publish=false` |
| WB-006 | 已完成 | 项目地图、执行计划、历史 roadmap、stabilization ledger 与 README 状态入口对齐 | 内部链接、基线、状态与 Gate 依赖一致性审查 |
| WB-007 | 进行中 | C# Installer v2 protocol：MachineReserved 前置、卸载 MachineRemovalAuthorized tombstone、operation-specific durable phase、canonical journal digest、helper-authoritative result journal、result + exact protected-store reload 后才推进的 two-phase session、helper-only package commit、CAS store、certificate ownership、Windows 11 x64 policy、embedded payload/package identity/locked lease、Uncertain outcome | 2026-08-31 Core 532/532、0 skipped；coordinator 只依赖 `IInstallerTransactionReader`，成功空读取不再复活 fallback，读取失败只回退到已验证 helper response；authority session 唯一提交 Prepared/后续 transition/clear，并覆盖 UAC-before-Prepared、commit/clear ack-loss 与 replay。提权路径已收敛为同一签名单文件 WPF EXE 的 helper 分支；persistent broker、machine mutation/SCM authority 尚未接入 |
| WB-008 | 进行中 | C# / WPF self-contained single-file、asInvoker、参考 VS Installer 产品实例层级的单卡片可访问 shell | 纯 `net10.0` Presentation 固定 Available/Installed/RecoveryRequired 三态，busy 为执行覆盖态；可用仅安装、已安装修复/卸载、恢复仅精确继续动作、busy 仅取消，startup router 在 WPF 创建前分流；最近 39/39 实际全绿，当前静态 70 项已把 package + durable journal 决策展开到全部 15 个合法 phase、非法组合与 blocked state，待资源门重验；WPF Windows 11 x64 Release 交叉编译通过，Windows UIA/publish/签名/VM 待验 |
| WB-009 | 待 Windows 验证 | 基于 1024/184 PNG 测绘重建 canonical SVG，并由 WPF 使用同一几何 | 1024/256/64/32 预览、设计测绘文档、hexagon/shadow/white mark 三层 SVG↔WPF exact geometry 契约；Explorer/DPI/ICO hinting 待验 |
| WB-010 | 已完成 | Linux restore/build/test/render 前资源门禁、OOM cooldown 与跨 Agent 原子执行锁 | `eng/check-linux-resource-budget.sh ... -- command` 以 host-wide `flock` 覆盖检查到命令结束，并阻止高 load/PSI/并发 worker 时启动 |
| WB-011 | 进行中 | Windows SafeFileHandle payload lease、MSIX metadata identity、CurrentUser/TrustedPeople certificate、current/target-user PackageManager、protected ProgramData state root 与 NativeAOT elevation 边界 | 已写 fixed root/store、strict bootstrap、split-token exact-logon + 标准用户 OTS Administrators(ReadWrite) protected pipe DACL、first-instance/单实例 server、双向 pipe PID primitive、exact-target-SID package commit inspector，以及不会创建缺失目录且不暴露 writer contract 的 parent reader；Windows 项目 Release 0 warning/0 error，新 reader/adapter 定向测试通过。固定 helper 名称/drive-qualified 路径/STA ArgumentList/UAC 1223 已有，签名 helper、persistent authenticated broker、target-SID certificate authority 尚未接入，目标提交 Windows E3 待验证 |

托盘状态契约固定如下：

| 条件 | 视觉状态 | 主色 |
|---|---|---|
| 色彩指示关闭 | 固定品牌绿（不表达状态） | `#08723B` |
| 色彩指示开启，有效运行态未知，或 Disabled/Standby | 中性灰 | `#626262` |
| 色彩指示开启，Rule/Full takeover 且 system proxy 已验证生效 | 系统代理绿 | `#08723B` |
| 色彩指示开启，Rule/Full takeover 且 TUN 已验证生效 | C# 紫 | `#512BD4` |

状态优先级固定为 `已验证 TUN > 已验证系统代理 > 未知/未启用/待命`；偏好 checkbox 不作为有效状态证据。运行时不再从 1024 PNG 逐像素转灰，也不再临时创建 HICON；托盘直接加载已打包的多分辨率 ICO。

## 3. 核心生产阻断队列

### P0-A：恢复并保持主分支绿色

- [x] 更新存在安全公告的 Installer transitive dependency。
- [x] 使用固定版 `cargo-audit 0.22.2` 和 2026-08-27 拉取的 1226 条公告完成无 vulnerability ignore 的本地扫描；保留 4 个上游 unmaintained warning 随 Slint 链升级治理。
- [x] 在 `main@edfd025a5b2237841f09d80d629ed66ff59b0d38` 的 Windows CI run `33069360366` 运行 locked fmt/clippy/test/audit 与完整 .NET build/test，全部绿色。
- [x] 把 `.NET build, format, and test`、`Rust Installer`、`Rust SandboxTest` 设为 `main` required checks；不得用宽泛 ignore 掩盖可升级漏洞。

退出条件：本文所在候选提交的全部 CI job 绿色，且 lockfile 可重现。

### P0-B：统一 Installer 子进程执行器

- [x] 把 `read_windows_build`、系统主题/语言探测、PowerShell capture、stdin PowerShell 和 elevated worker 的直接调用收口到统一 runner。
- [x] 按注册表探测、普通查询、UAC/机器事务定义 15 秒、45 秒和 15 分钟 deadline；runner limits 可在测试注入。
- [x] 并发流式读取 stdout/stderr，各自最多保留 256 KiB；超出后继续排空并记录 `truncated`。
- [x] 非提权路径超时后请求 `TerminateJobObject`/直接 child kill，先关闭 kill-on-close Job，再用共享 2 秒 teardown grace 轮询回收 child 和接收 I/O worker；超时 worker 直接 detach 并返回稳定诊断码，Windows 测试验证同一 Job 内的孙进程退出。
- [x] 以稳定诊断码区分 UAC 取消、deadline、spawn/wait/pipe 失败、输出超限和非零退出。
- [ ] `Start-Process -Verb RunAs` 创建的 elevated helper 会跨越调用方 Job 边界；以 durable transaction cut-point 和隔离 Windows VM 故障注入证明超时、取消或调用方终止后可收敛且不遗留失控 helper。
- [ ] 故障注入证明 timeout 后 journal 仍可由同一 Installer Repair 收敛。

退出条件：挂起或无限输出的非提权 child 不会卡死 UI、泄漏同一 Job 内进程或无界占用内存；`RunAs` elevated helper 的跨 Job 生命周期由 durable cut-point 和 VM 故障注入证明可恢复、可收敛，不能用调用方 Job 测试替代。

### P0-C：核心状态事务闭环

- [ ] 用 packaged real app 双实例 smoke 关闭 P1-01，证明 secondary 不修改第一实例的 core/代理状态。
- [ ] 关闭 P1-06：reset/clear 先 quiesce 所有生产者，再原子应用或明确记录 restart-required，不抹掉真实 baseline。
- [ ] 关闭 P1-07：data-package import 经过单一 settings transaction，统一 cache invalidation、core/代理 replay 和完整 rollback。
- [ ] 关闭 P1-08：TUN 与 mixed port 使用 desired/applied generation；UI、托盘和诊断只宣称 verified effective state。

退出条件：reset、clear、import、TUN、port 和双实例都不会造成持久设置、运行态、代理或 UI 宣称彼此分裂。

### P0-D：干净机器运行依赖与可信发布输入

- [ ] 为 App、Service、Watchdog 固定 .NET 10 Runtime 策略：全部 self-contained，或由 Installer 以固定可信前置依赖进行可恢复部署。
- [x] Service 与 RecoveryWatchdog 的正式 staging 改为 `win-x64` self-contained single-file；组件项目和 Windows CI 均拒绝 sidecar，最终 MSIX allowlist 同步收紧。App 策略与干净 VM 证据仍属于上一项未闭环范围。
- [ ] 在未安装 .NET 10 SDK/Runtime 的干净 Windows 11 x64 VM 证明安装、启动、Repair 和卸载。
- [ ] 固定四项 GeoData 的 canonical source、版本、许可、长度和 SHA-256；受控构建不能接受 operator 任意选择字节后自签 manifest。
- [ ] 从干净 runner 取得所有 immutable 输入，保持离线 locked build 与精确 allowlist。

退出条件：干净 checkout 和干净支持版 Windows 不依赖开发机遗留 runtime/GeoData，也能构建并运行候选。

### P0-E：durable install/uninstall 与证书生命周期

- [x] 安装 journal 已有 Prepared、PackageCommitted、MachineCommitted、Verified 和受保护 ProgramData 原子写入。
- [x] C# v2 Core 候选把 operation 纳入 immutable journal，并以反向阶段顺序覆盖 payload 缺失卸载的 pure coordinator contract；strict codec 明确拒绝未知、重复和非 canonical 字段。
- [x] C# Core 已实现 exact SID/thumbprint/DER SHA-256/store/pre-existing/Installer-owned/reference/generation 的 certificate ownership ledger、strict codec、CAS file store 和 import/delete write-ahead replay；Rust install/repair 也已把 machine `Prepared` 提前到证书 mutation 之前。
- [x] C# embedded release manifest 与正式生成器已绑定主 MSIX 内固定 7 项 machine payload 的 canonical path、展开长度和 SHA-256；Core 在主包 lease 仍有效时流式复核并拒绝缺失、额外项、大小写碰撞和 byte mismatch。
- [ ] 为卸载、association、machine payload 和 certificate 定义对称、持久、可重入阶段。
- [x] C# Windows 层已实现固定 `CurrentUser/TrustedPeople` 的 `X509Store` adapter，只接受 exact request + SID + Windows handle lease，并以 thumbprint + DER SHA-256 双重身份导入/删除；Linux 只取得交叉编译证据。
- [ ] C# Core 的 authority split 已完成：`asInvoker` parent 不直接写/clear protected root，首个 Prepared、后续 journal transition 与 clear 由 helper authority session 唯一提交，parent 只读 reload 并 exact-compare。仍需把它接入 authenticated Windows broker，并实现 `<exact target SID>\TrustedPeople` 的 `CERT_SYSTEM_STORE_USERS` native adapter；在 Win11 标准用户 + alternate-admin OTS 场景证明不会落入管理员 CurrentUser。若该路径不能取证，只允许 helper ledger cut-point 包围 parent-side mutation 的显式回退。
- [ ] 用已改为显式 SID 参数的 Windows PackageManager facade 实现 helper-only target-user package inspector；`CommitPackage` 必须传 journal exact SID + family，不能用 OTS 管理员的 current-user 空 SID，并在 alternate-user Windows VM 证明权限和身份矩阵。
- [ ] 把 adapter 接入 WPF production composition，并为当前 Rust authority 增加兼容 ledger/remove；目标提交 Windows 测试和真实卸载必须证明只删除本 Installer 拥有、且无其他依赖者的 exact 证书。
- [ ] payload 缺损时仍可进入安全卸载；Repair 的完整性要求不得阻塞卸载恢复。
- [ ] action/refresh/language change 使用单一 generation 状态机；RAII 保证 worker panic/spawn failure 后 busy 状态复位。
- [ ] 逐个识别真实 compensation 与不可达分支；保留有效补偿，只删除有证据证明不可达的虚假保证。

退出条件：在每个事务 cut point 强制终止后，Repair 或 Uninstall 都能确定性收敛到一个完整终态。

### P0-F：构建并签名不可变候选

- [ ] 新增独立 candidate workflow，固定 SDK/toolchain、locked restore、Windows SDK、精确 staging、SBOM 和 build provenance。
- [ ] 在受控签名环境构建候选；Installer、主 MSIX 和 framework dependency 分别验证 signer 与 RFC 3161 timestamp。
- [x] 本地 `build.ps1` 已实现 staging → `release-artifacts` 的路径、长度和 SHA-256 tree compare。
- [ ] CI 以不可变 artifact ID/digest 在 build、VM E2E 和 promotion 之间传递同一个候选，不允许手工替换 payload。

退出条件：未签名内容可重现；带时间戳签名的候选可验证 signer、subject digest、timestamp、SBOM、provenance 和整树 digest。此阶段不公开 promotion。

### P0-G：真实 Installer Windows E2E

- [ ] Sandbox 只接受 `target/release-artifacts` 完整树并运行签名 `ClashSharp-Installer.exe`，不得再直接 `Import-Certificate` + `Add-AppxPackage` 旁路产品入口。
- [ ] host 与 CI 调用同一个版本化 strict JSON schema validator；必需场景出现 skipped、未知字段或场景错配时报告失败。
- [ ] Sandbox 覆盖无需重启 smoke；VM snapshot 矩阵覆盖首次安装、Repair、升级、拒绝降级、卸载、UAC 取消、应用运行中、不同 SID、pending-delete、重启和 payload 篡改。
- [ ] 覆盖 install/uninstall 每个 durable journal cut point。
- [ ] 验证 App 启动、system proxy、TUN、service、退出/重启恢复、卸载残留和证书归属。

退出条件：绿色报告绑定 P0-F 的精确 artifact digest，并证明真实 Installer、AppXSVC、UAC、SCM、journal、代理/TUN 与卸载链路，而不是测试旁路。

### P0-H：受保护 promotion 与正式发布

- [ ] `main`、release tag 和 production environment 使用 required checks/required reviewers 与最小权限。
- [ ] P0-G 完整矩阵成功后才 promotion 同一 artifact digest；生成校验和、attestation、版本说明、已知问题和 roll-forward 操作手册。
- [ ] 启用依赖/安全报告渠道和定期 audit；发布前完成 SECURITY、支持策略和变更记录。

退出条件：受保护 tag 只能 promotion 已通过 P0-G 的不可变候选，发布记录和 provenance 可验证且无手工替换窗口。

## 4. RC 前高优先级加固

以下工作可以在 P0-D/P0-E 并行准备，但必须在 P0-F 候选冻结前完成，避免签名 VM 矩阵反复重跑：

1. 安全边界

   - 为 system DLL P/Invoke 限定 `System32`，关闭 CA5392 后升为 error。
   - 统一日志入口的长度、控制字符、URL/token/path 脱敏和稳定诊断码。
   - 将动态 SQL 标识符缩为 closed enum/allowlist。
   - 为 service identity/ACL 建立真机安全测试。

2. 发布隔离

   - [x] 四个 Probe 已明确设置 `IsTestProject=true`、`IsPublishable=false`、`IsPackable=false`，并增加架构门禁（2026-08-27）。
   - [x] MSIX 保持 allowlist + denylist 双门禁，现有 Rust 契约测试证明 Probe、Sandbox、Installer、PDB 和第二 updater 不可进入 payload。

3. 可靠性与资源所有权

   - 修复 Profile gate 的 `SemaphoreSlim` 生命周期。
   - 明确取消后仍运行任务的 owner；补齐页面 CTS cancel + dispose 对称性。
   - 把 Connections latest-wins refresh 与 serialized mutation 分离，并增加反复导航/取消测试。

4. 质量门禁

   - [x] Installer Core transaction、identity、certificate ownership、uninstall 与关键状态机已设置 line 90% / branch 80% CI 阈值；499-test 当前候选实测 line 94.90%（3108/3275）/ branch 85.67%（1495/1745）。
   - [ ] 将同类阈值扩展到 Windows adapter、日志边界和真实 integration 层。
   - 增加键盘、Narrator、高对比度、200% 文本缩放和托盘高 DPI smoke。
   - 补齐 `SECURITY.md`、发布/支持策略、变更记录和运维 runbook。

## 5. 暂不阻断核心生产就绪的工作

这些工作继续保留，但不再与安全发布闭环同级：

- 为每个 private 字段、局部函数和测试声明补文档。
- 全量从 `{Binding}` 迁移到 `x:Bind`。
- 一次性拆完 `SettingsViewModel`、`ProfileCatalogService`、Installer `main.rs` 和 `service_plan.rs`。
- 纯粹为了“现代 C#”而替换清晰的循环或引入 LINQ。
- 不影响安全、可访问性或关键生命周期的视觉完美化。

公共 API、安全边界、事务、并发、取消和资源所有权文档仍是 RC 前要求；其余全量文档清零进入 GA 后持续治理。这样不会降低生产质量，只是把发布判定从“声明数量”改为“风险和直接证据”。

## 6. 推荐推进顺序

1. 提交 WB-000～WB-006，取得本文所在候选提交的 Windows CI 全绿和托盘人工 smoke。
2. 关闭 P0-B journal recovery，并完成 P0-C 的 P1-01/P1-06/P1-07/P1-08 核心状态事务。
3. 固定 P0-D runtime/GeoData 输入，完成 P0-E 对称 durable lifecycle 与证书归属。
4. 完成 RC 安全、可靠性、覆盖率、可访问性和运维文档加固。
5. 由 P0-F 构建并签名不可变候选，但不公开 promotion。
6. 用 P0-G 对该精确候选执行真实 Installer VM 矩阵；失败则回到对应实现层并重新生成候选。
7. P0-H 只 promotion 已通过矩阵的同一 artifact digest，执行升级/卸载 soak 后发布首个正式 Release。

任何阶段若发现 P0 失败，应修复当前层而不是继续叠加 UI 功能。

## 7. 核心生产就绪 Definition of Done

只有以下项目全部具有可追溯证据，才可标记“Core Production Ready”：

- [ ] `main` 的 .NET/Rust build、format、test、audit 和选定 analyzer 全绿。
- [ ] 没有未处置的 critical/high 依赖漏洞。
- [ ] 干净 Windows 11 x64 在未预装 .NET 10 时仍能通过受支持的 runtime 部署策略运行 App、Service 和 Watchdog。
- [ ] GeoData 的来源、版本、许可、长度和 digest 可追溯，干净 runner 能确定性取得同一输入。
- [ ] 所有 Installer child process 都有 deadline 和有界输出；非提权进程树有 Job 终止保证，跨 `RunAs` 边界的 elevated helper 有 durable cut-point/VM 故障注入证明可恢复且无失控残留。
- [ ] install、Repair、upgrade、reject-downgrade、uninstall 和证书生命周期均可中断恢复。
- [ ] 最终签名 Installer 的 Windows VM 矩阵无必需 skipped。
- [ ] 主体无法安装/配置/删除 service；最终 MSIX 只有精确允许的产品资产。
- [ ] system proxy、TUN、退出恢复、重启恢复和卸载残留都经过真机验证。
- [ ] 托盘三态、高 DPI、Explorer 重启恢复和设置迁移经过 Windows smoke。
- [ ] 日志不会持久化未截断、未清理或可识别的 token/URL/path 敏感信息。
- [ ] 签名、时间戳、SBOM、provenance、artifact hash 和 release notes 均可验证。
- [ ] `main`、release tag 和 production environment 受保护；required checks 绑定候选 SHA/artifact digest。
- [ ] 有覆盖率阈值、崩溃/恢复 runbook、安全报告渠道和支持边界。
- [ ] 发布候选经过至少一轮升级与卸载 soak，且没有 P0/P1 未决缺陷。

## 8. 当前验证边界

本文所在候选批次在 Linux 工作区已完成 SVG XML、ICO 多尺寸结构、Rust fmt、locked Cargo metadata、无 vulnerability ignore 的固定版 `cargo-audit`、独立 process-runner 测试、Windows target clippy、源码 whitespace 和静态契约；并用官方 .NET SDK `10.0.201` 完成 locked restore、format 与 Application Release build。Linux 仍不能替代 Windows XAML compiler 驱动的完整 WinUI build/test，Windows Job 孙进程、AppXSVC、UAC 和 SCM 验证也只能在 Windows 运行，因此候选提交的 Windows CI 仍是不可替代的 E3 验收证据。
