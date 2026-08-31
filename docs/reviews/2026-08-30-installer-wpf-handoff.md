# ClashSharp Installer / WPF 接盘开工单

> 2026-08-31 状态更新：迁移期 Rust/Slint 双轨已删除，C# / WPF Installer 为唯一实现；下文提到的 Rust 文件和差分向量只保留为历史背景。后续实现不得重新引入 Cargo、Rust 或 Slint。当前生产入口仍保持 fail closed，不能把“唯一源码实现”误报为“已通过发布验收”。

更新时间：2026-08-31（Asia/Shanghai）
当前工作区：`D:\Coding\ClashSharp`
结论：**C# / WPF 已成为唯一 Installer 源码；production runtime、parent/helper 与 machine operations 已接入源码并通过安全定向测试，但默认编译门仍关闭，真实提权 mutation 和签名 VM 矩阵尚未闭环。**

## 1. 接盘时先守住的边界

- 工作树包含大量未提交修改和新增文件；不得 `reset --hard`、`checkout --`、清理未跟踪目录或覆盖用户修改。先用 `git status --short` 建立自己的变更清单。
- 当前活动环境是 Windows 工作区，可以执行 Windows 定向测试；本轮没有启动 WPF Installer、UAC、AppXSVC、SCM、证书或机器目录真实 mutation，不能把 fake/temp-only 测试外推为管理员 VM 证据。
- 目标平台固定为 Windows 11+ 原生 x64。WPF EXE 固定 `win-x64`、self-contained、single-file、`asInvoker`。
- 产品界面固定为参考 Visual Studio Installer 信息层级的**单产品卡片**，不增加伪导航、多页安装向导或多产品网格。
- 稳定产品状态只有 `Available`、`Installed`、`RecoveryRequired`；busy 是执行覆盖态，不是第四个持久状态。
- 默认与正式打包仍必须 fail closed。`MigrationPreviewInstallerRuntime.CanExecute=false` 是预期安全状态；源码中的 production runtime 只能由 formal build + embedded manifest + 显式 mutation flag 三重门启用，不能为演示而放开。
- Rust / Slint 已退役且不得恢复。C# / WPF 是唯一实现；同一签名候选通过完整 Windows E4 矩阵前，`build.ps1` 不得传入 mutation-runtime 开关。

## 2. 资源与并发纪律

- 不终止、不暂停、不重启其他代理或不属于本任务的进程；发现并发构建时先等待其自然结束。
- Windows 上所有 MSBuild 命令保持 `-m:1 -nr:false -p:UseSharedCompilation=false`，测试集串行启动，避免 compiler server 和 testhost 争用。
- 不通过测试启动 Installer/App/mihomo，不请求 UAC，不触碰真实服务、证书、包、代理、TUN 或机器目录；Win32 mutation 测试只能使用随机临时目录或 injected fake。
- 如果回到 Linux 环境，继续用 `eng/check-linux-resource-budget.sh` 包裹 restore/build/test/format；历史 OOM 门禁仍然有效。

## 3. 当前证据账本

| 范围 | 最近真实证据 | 当前源码状态 | 可以声称什么 |
|---|---|---|---|
| Installer Core | Release `548/548`，0 skipped | helper-authoritative coordinator/session、strict manifest、certificate ledger、release-bound inspection contract 与 fault matrix 已跑绿 | 当前 Core checkpoint 绿色；旧覆盖率数字不能自动沿用 |
| Installer Presentation | Release `89/89`，0 skipped | 单卡片三态、精确 allowed-operation、production runtime、packaging activation gate、并发/取消/异常边界已跑绿 | 当前纯 Presentation checkpoint 绿色 |
| Installer Windows | 安全集 `348/348`，Release build 0 warning / 0 error | authenticated broker/host、target-SID cert、machine operations、environment/process 与 parent engine 已实现 | 明确排除了会改真实 CurrentUser 证书的 3 项测试；不能声称管理员 mutation 通过 |
| WPF shell | Windows 11 x64 Release 编译通过，0 warning / 0 error | C# 紫色单卡片、helper pre-WPF 分流、关闭取消与 gated production composition 已落地；默认 UI runtime 与 helper authority 都 fail closed | 没有 UIA、UAC 或生产安装运行证据 |
| Sandbox report gate | Windows PowerShell 5.1 / PowerShell 7 契约测试通过，CI 已接入 | host 只接受精确 schema/scenario/runId、有效时间/环境、全部步骤 passed 与场景专用 checks；当前只有 fixed-package `install-only` 存在通过契约，未实现场景显式 failed/not-executed | 只能声称报告不会把 skipped/未知字段/错配/部分证据当通过；没有启动 Sandbox，也不是 E3/E4 证据 |
| 仓库纯度 | Rust/Cargo/Slint 源文件扫描为空，`git diff --check` 无 whitespace error | 旧审计文字和打包资产目录可保留，但不得包含 Rust 构建入口 | 当前实现语言是 C#；GitHub 历史统计刷新取决于提交后的重新索引 |

## 4. 已落地的主要内容

### 产品与 UI 契约

- WPF 目标固定 Windows 11+ x64、自包含单文件、`asInvoker`、PerMonitorV2。
- 主窗口收敛为一个产品卡片：Available 只显示安装；Installed 显示修复和卸载；RecoveryRequired 只显示与 durable journal 精确一致的继续动作；busy 只显示取消。
- `InstallerProductStatePolicy` 使 durable transaction 优先于 package 观测，并拒绝非法 state / operation 组合。
- Presentation 测试已覆盖全部合法 operation / phase 组合、非法组合、所有 `CanExecute=false` 状态、受限平台仅卸载、runtime lifetime、窗口关闭取消与正式打包不启用 mutation runtime；当前 Release checkpoint 为 89/89。
- `Logo.svg` 已依据原 PNG 的轮廓、阴影和白色标记重建，并以 WPF geometry 契约锁定。

### Core / durable protocol

- 已实现 operation-specific v2 journal、canonical codec / digest、CAS store、helper command/result framing、session guard、ack-loss replay、abort reconciliation、`CommitPackage` 和 certificate ownership primitive。
- command / result 携带 canonical journal bytes 与 hash；helper 后继必须与唯一合法 transition 精确一致。
- 新增只读 `IInstallerTransactionReader`，writer `IInstallerTransactionStore` 继承它；Windows protected stores 暴露 reader 与 writer 两种 capability view。
- protected root 固定 `%ProgramData%\ClashSharp\Installer\v2`，目标用户只有读取恢复状态的权限，不能为了 parent 接线而放宽 ACL。

### Windows helper / machine primitive

- 已写严格八参数 parent-PID bootstrap、固定 helper 路径与 STA `runas` launcher、UAC 1223 分类，以及锁定 exact EXE 的 final-path / Authenticode signer lease。
- named-pipe DACL 拒绝 Network，允许 exact logon SID ReadWrite；为 over-the-shoulder elevation 额外允许 Builtin Administrators ReadWrite。此 ACL 只是连通门，连接后仍必须双向精确绑定 parent / helper PID 和签名镜像。
- persistent broker、client/server、helper host、双向 PID/image 验证、bounded deadline、退出与 uncertain reconciliation 已有 deterministic 测试；WPF production runtime 已组合这些能力，但默认编译门仍关闭。
- PackageManager facade 已支持显式 target SID；`WindowsTargetUserPackageCommitInspector` 按 exact SID + package family + version + health fail closed。
- native target-SID `TrustedPeople` adapter 已按 SID、thumbprint 和完整 DER hash 实现并完成 injected 测试；alternate-admin OTS 真实 VM 证据仍缺失。
- machine plan、target profile resolver、protected roots、锁定 MSIX 的固定七项 archive、staging/current/previous swap、SCM read/mutate、association 原子写入和空根清理原语已组合进 concrete machine operations。profile 缺失卸载只在 exact SID association 存在且服务已独立证明 absent 时清理固定 payload/association，始终只读打开根 lease。

## 5. 当前最高优先级缺口

### 已关闭的实现缺口

- coordinator 已只持有 parent read-only transaction view；Prepared、phase transition、certificate ledger 与 Verified clear 均由 helper authority session 独占，成功空读取不再错误复活内存 fallback。
- persistent authenticated broker、same-EXE helper host、PID/final-path/Authenticode lease、target-SID package inspector和 target-SID certificate adapter 已落地并通过 deterministic 定向测试。
- 机器侧 fixed-plan 原语已覆盖 protected roots、locked archive、payload swap、SCM、association、profile resolver、remove 与 postcondition verifier。
- concrete `WindowsMachineHelperMachineOperations`、helper pre-WPF 启动入口、只读 environment/process inspection、parent engine 和 `ProductionInstallerRuntime` 已完成组合；production UI 与 helper authority 由同一个默认关闭的 formal-build mutation gate 隔离。

### P0：显式 reassociation 与 production activation 证据

普通 install/repair/uninstall 与 durable replay 已完成组合。剩余源码级权限缺口是显式跨 owner reassociation：当前 protected root ACL 和 SCM tuple 都绑定现 owner，不能通过把 association 直接换成目标 SID 来“修复”。在协议完成前所有外来 owner 场景继续 fail closed，普通 parent 请求固定 `AllowReassociation=false`。

- 先以旧 owner SID/token/profile 和旧 ACL lease 验证并停止旧 SCM tuple，不得覆盖 association；
- 再以 durable、可重放的明确阶段迁移 root ACL 与 association，并为新 owner 构造 exact service tuple；
- UI/parent 必须有独立明确确认，只有该路径可设置 `AllowReassociation=true`；
- 每个失败切点必须保留足够的旧/新 owner 证据，能收敛而不是遗失授权。

### P0：production runtime 与真实证据

- production composition 已存在但默认不开启；在 E3/E4 完成前继续由 `MigrationPreviewInstallerRuntime` fail closed，不得修改默认值或打包参数。
- 真实 Windows 11 管理员临时 VM 仍需验证 UAC/OTS、target-SID cert/AppXSVC、ACL、SCM、锁文件、1072 delete-pending、reboot 和 durable cut-point。
- 正式签名、自包含单文件候选还需同一 digest 的 E3/E4 矩阵；没有这些证据不得宣称可发布。

## 6. 当前推进顺序

### 步骤 0：只读确认与安全测试边界

```powershell
git status --short
git diff --check
rg --files -g '*.rs' -g 'Cargo.toml' -g 'Cargo.lock' -g '*.slint'
```

保留工作树中所有既有修改。先确认没有其他构建正在占用输出目录；只启动本任务的单进程 build/test，不终止其他进程。禁止运行会修改真实 CurrentUser certificate store、SCM、AppXSVC 或受保护机器目录的测试。

### 步骤 1：保持当前 checkpoint

每个小步先跑受影响的 deterministic/fake 或随机临时目录测试，再以 `-m:1 -nr:false -p:UseSharedCompilation=false` 构建 Windows 生产项目和测试项目。完整 Windows adapter 集中会触碰真实用户证书的用例必须留给隔离 VM，不能在开发主机直接整批执行。

### 步骤 2：关闭 production activation 前缺口

为显式 reassociation 设计双 owner durable 阶段与 root ACL 转移协议，或在首个发布版本中明确不提供该功能；继续补 UIA、签名打包与 Sandbox 的真实 launch/startup/cleanup-uninstall 证据，并把场景结果绑定到同一签名候选 digest。所有本机组合层测试使用 injected seams 或临时目录，不调用真实 SCM、证书、包或 Program Files。

### 步骤 3：Windows 实机证据顺序

1. Windows 11 x64 非提权 adapter tests；
2. 标准用户同账户 consent 与 alternate-admin OTS；
3. pipe squatting / wrong PID / wrong image / UAC cancel；
4. cert exact target SID、AppXSVC exact target SID；
5. SCM / ACL / association；
6. WPF UIA：键盘、焦点、Narrator、高对比度、100/200/300% DPI、文字缩放、长诊断；
7. signed immutable candidate 的 install / repair / upgrade / uninstall / reboot / tamper / 每个 durable cut-point 矩阵。

任何 Windows 必需场景 skipped 都不能进入候选 promotion。

## 7. SVG / ICO 交接

- `ClashSharp/ClashSharp/Assets/Logo.svg` 已重建为可审查向量路径，并与当前 WPF logo geometry 对齐。
- 原始参考位图在 `ClashSharp/Installer/Logo.png` 与 `LogoInstaller.png`。
- `ClashSharp/Installer/LogoInstaller.ico` 当前只包含 16 / 24 / 32 / 48 / 64 / 128 帧，缺少 20 / 40 / 256 帧，也没有仓库内可复现的确定性生成路径。
- 不要把简单自动描摹或仅放大 PNG 当作完成。需从测绘后的向量母版生成逐尺寸、像素提示过的帧，特别审查 16/20/24/32/40/48，并验证 256；最终在 Windows Explorer、任务栏、UAC 和 100–300% DPI 上检查。
- 这项只能在 `heavy` 资源门 READY 时生成 / 渲染；视觉完善晚于 authority split 和当前源码 checkpoint，但早于签名候选冻结。

## 8. 测试集质量要求

- 测试必须直接调用生产 parser / policy / codec / adapter，不用复制实现的测试 oracle。
- phase、operation、identity、ack-loss、cancel 和 fault cut-point 使用独立可报告 case；不要用一个循环让多个安全边界共享一个模糊失败。
- 禁止固定 sleep；异步与并发测试使用可控 barrier / signal / deadline。
- 禁止隐式 skip、空 discovery 和只编译不执行的假绿；检查 TRX 的 total / executed / notExecuted。
- Core 覆盖率底线仍是 line 90% / branch 80%，但覆盖率不能代替 adversarial matrix。
- Windows-only 行为必须在 Windows 跑；Linux cross-build 的 0 warning / 0 error 不能升级为运行证据。
- 真实 E4 必须记录 candidate digest、OS build、target SID、起始状态、注入 cut-point、进程终止确认和 package / SCM / ACL / association / certificate / journal 终态。

## 9. 不能误报的事项

- 旧 512 / 70 / 103 checkpoint 已恢复；Core/Presentation 已超过旧计数并实际跑绿，Windows 新边界已编译和定向验证，完整 Windows/VM 矩阵仍未执行；
- Windows tests 从未在 Linux 执行；
- WPF shell 没有 Linux runtime 证据，也没有 Windows UIA 证据；
- C# / WPF 是唯一 Installer 源码实现，但当前 fail-closed runtime 不是可发布 production authority；
- authenticated broker/host、machine operations、helper 路由与 WPF production runtime 已组合并通过 deterministic 测试，但默认 gate 关闭且没有 signed VM 证据；
- coordinator authority split 已完成；剩余源码权限缺口是显式双 owner reassociation，剩余发布缺口是 E3/E4 与 gate promotion；
- target-SID native certificate adapter 已实现，alternate-admin OTS 仍未在隔离 VM 取证；
- `MigrationPreviewInstallerRuntime` 不执行安装；
- ICO 未完成；
- 没有签名候选、Windows E3/E4、reboot 或 cut-point 闭环。

## 10. 继续阅读顺序

1. [Installer / WPF 详细审计](2026-08-30-installer-wpf-rewrite-audit.md)
2. [生产就绪执行计划](2026-08-27-production-readiness-execution-plan.md)
3. [Machine-helper IPC 边界](../design/2026-08-30-installer-machine-helper-ipc.md)
4. [Protected-state root 边界](../design/2026-08-30-installer-protected-state-root.md)
5. [项目开发地图](2026-08-27-project-development-map.md)

截至 2026-08-31，authority split、authenticated broker/helper host、target-SID certificate/package、machine operations、profile-independent removal、helper 启动入口、parent engine 与 gated WPF production runtime 均已完成源码与安全定向测试。下一里程碑是决定并实现显式双 owner reassociation 边界，随后在同一签名候选上完成 Windows E3/E4，才能考虑启用 production gate。
