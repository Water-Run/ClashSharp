# ClashSharp Installer / WPF 接盘开工单

更新时间：2026-08-30（Asia/Shanghai）
工作区：`/home/waterrun/Project/ClashSharp`
分支 / HEAD：`main` / `edfd025a5b22`
结论：**实现尚未闭环，也不是可发布 Installer；接盘第一件事是恢复当前源码的可编译、可测试证据。**

## 1. 接盘时先守住的边界

- 工作树包含大量未提交修改和新增文件；不得 `reset --hard`、`checkout --`、清理未跟踪目录或覆盖用户修改。先用 `git status --short` 建立自己的变更清单。
- 当前环境是 Linux。它可以验证 Core、Presentation、静态资源和 Windows 目标交叉编译；不能声称验证过 WPF runtime、UIA、UAC、AppXSVC、SCM、ACL、证书写入、Authenticode、重启恢复或真实安装/卸载。
- 目标平台固定为 Windows 11+ 原生 x64。WPF EXE 固定 `win-x64`、self-contained、single-file、`asInvoker`。
- 产品界面固定为参考 Visual Studio Installer 信息层级的**单产品卡片**，不增加伪导航、多页安装向导或多产品网格。
- 稳定产品状态只有 `Available`、`Installed`、`RecoveryRequired`；busy 是执行覆盖态，不是第四个持久状态。
- 安装器在迁移完成前必须 fail closed。当前 `MigrationPreviewInstallerRuntime.CanExecute=false` 是预期安全状态，不能为演示而放开。
- Rust / Slint Installer 仍是发布 authority；C# / WPF 只有在同一签名候选通过完整 Windows E4 矩阵后才能切换。

## 2. 资源纪律：任何构建之前必须执行

本机本轮发生过全局 OOM。所有 restore、build、test、format 和渲染必须由资源门持锁包裹，从检查开始一直锁到命令退出；不得降低阈值、改用更轻 profile 绕过，也不得和 Cargo、dotnet、编译器或渲染器并发。

固定 SDK：`/tmp/clashsharp-dotnet-10.0.201/dotnet`

```bash
./eng/check-linux-resource-budget.sh standard -- <单个 restore/build/test/format 命令>
```

高分辨率渲染、ICO 多尺寸生成、完整 solution / publish 使用 `heavy`。所有 MSBuild 命令保持 `-m:1 -nr:false -p:UseSharedCompilation=false`，测试集串行启动。

交接前最后一次只读门禁结果：

- `MemAvailable`: 6464 MiB；
- `SwapFree`: 12 MiB；
- 综合余量：6476 MiB，低于 standard 的 12288 MiB；
- load 6.93 / 16 CPU、memory PSI full avg10 0.00；
- 没有并发 build/test/compiler/renderer；最近 15 分钟没有可读 OOM；
- 结论：`RESOURCE GATE: BLOCKED`，因此本轮没有启动 dotnet、Cargo 或渲染器。

门禁不通过时停止，不把静态检查当作编译或测试证据。

## 3. 当前证据账本

| 范围 | 最近真实证据 | 当前源码状态 | 可以声称什么 |
|---|---|---|---|
| Installer Core | Release `499/499`，0 skipped；line 94.90%（3108/3275），branch 85.67%（1495/1745） | 静态展开 512 项；strict bootstrap / pipe-name 等增量后未重新编译 | 仅旧 499 checkpoint 绿色；不能声称当前 512 绿色或沿用旧覆盖率 |
| Installer Presentation | Release `39/39`，0 skipped | 静态展开 70 项；三态全矩阵增量后未重新编译 | 仅旧 39 checkpoint 绿色 |
| Installer Windows | 前 76 项所在版本在 Linux 完成 Windows 11 x64 Release 交叉编译，0 warning / 0 error | 静态展开 103 项；最新 27 项和生产增量未重新编译 | 只能声称旧交叉编译 checkpoint；Windows 测试从未在 Linux 执行 |
| WPF shell | 较早版本完成 Windows 11 x64 Release 交叉编译，0 warning / 0 error | strict bootstrap、Presentation 和 Windows 依赖增量后未重编 | 没有 WPF runtime / UIA 运行证据 |
| 静态检查 | 最近 `git diff --check` 和相关 XAML / manifest / SVG `xmllint` 通过 | 本交接文档之后仍应再跑一次小型静态检查 | 只能证明文本/XML 基本完整 |

当前静态计数 512 / 70 / 103 是源码审查期望值，实际 test discovery 和 TRX 才是最终权威。必需测试的 skipped / not-executed 必须按失败处理。

## 4. 已落地的主要内容

### 产品与 UI 契约

- WPF 目标固定 Windows 11+ x64、自包含单文件、`asInvoker`、PerMonitorV2。
- 主窗口收敛为一个产品卡片：Available 只显示安装；Installed 显示修复和卸载；RecoveryRequired 只显示与 durable journal 精确一致的继续动作；busy 只显示取消。
- `InstallerProductStatePolicy` 使 durable transaction 优先于 package 观测，并拒绝非法 state / operation 组合。
- Presentation 源码测试已展开全部 15 个合法 operation / phase 组合、5 个非法组合，以及所有 `CanExecute=false` 状态；尚待资源门后的实际执行。
- `Logo.svg` 已依据原 PNG 的轮廓、阴影和白色标记重建，并以 WPF geometry 契约锁定。

### Core / durable protocol

- 已实现 operation-specific v2 journal、canonical codec / digest、CAS store、helper command/result framing、session guard、ack-loss replay、abort reconciliation、`CommitPackage` 和 certificate ownership primitive。
- command / result 携带 canonical journal bytes 与 hash；helper 后继必须与唯一合法 transition 精确一致。
- 新增只读 `IInstallerTransactionReader`，writer `IInstallerTransactionStore` 继承它；Windows protected stores 暴露 reader 与 writer 两种 capability view。
- protected root 固定 `%ProgramData%\ClashSharp\Installer\v2`，目标用户只有读取恢复状态的权限，不能为了 parent 接线而放宽 ACL。

### Windows helper / package primitive

- 已写严格八参数 parent-PID bootstrap、固定 helper 路径与 STA `runas` launcher、UAC 1223 分类。
- named-pipe DACL 拒绝 Network，允许 exact logon SID ReadWrite；为 over-the-shoulder elevation 额外允许 Builtin Administrators ReadWrite。此 ACL 只是连通门，连接后仍必须双向精确绑定 parent / helper PID 和签名镜像。
- 已写 first-pipe-instance / 单实例和双向 pipe PID 查询 primitive。
- PackageManager facade 已支持显式 target SID；`WindowsTargetUserPackageCommitInspector` 按 exact SID + package family + version + health fail closed。
- CurrentUser package adapter 仍显式传空 SID，保持原有 parent-current-user 行为；未来 helper 的 `CommitPackage` 必须传 journal 中的 exact `TargetSid`。

## 5. 当前最高优先级缺口

### P0：修正 protected-state authority 分工

这是接盘后第一个实现任务，先于 broker、SCM 和更多 UI。

`InstallerCoordinator` 现在仍：

1. 在首次 UAC 前直接 `SaveAsync(Prepared)`；
2. 在 helper 返回每个后继后再次 `SaveAsync`；
3. 在完成时直接 `ClearVerifiedAsync`。

这与安全模型冲突：WPF parent 是 `asInvoker`，而 protected root 对目标用户只读；直接接 Windows writer 会失败，helper 已提交后再由 parent 保存又会重复写或 CAS 冲突。

要求的最终分工：

- parent 只持有 `IInstallerTransactionReader`，构造 Prepared 只在内存中进行；
- 首个经过 PID / image / protocol 认证的 helper `Prepare` 在任何副作用前持久化 Prepared；
- journal 和 certificate ledger 的每次 transition 均由同一 helper session 唯一提交；
- helper 返回结果后，parent 只重新读取 protected store 并与 result exact-compare；
- Verified clear 是 helper-authoritative terminal operation，随后 parent 只读证明 journal 已不存在；
- UAC 在 helper 持久化 Prepared 前取消时，不得出现 durable recovery，也不得有任何副作用。

同时修复 `InstallerCoordinator.RefreshDurableAsync`：当前 `LoadAsync() ?? fallback` 会在权威读取成功且确实无 journal 时错误回退到内存 Prepared。只有读取本身失败时才允许使用 fallback；成功读取 `null` 必须保持 `null`。

优先涉及：

- `ClashSharp/ClashSharp.Installer.Core/Execution/InstallerCoordinator.cs`
- `ClashSharp/ClashSharp.Installer.Core/Transactions/IInstallerTransactionStore.cs`
- `ClashSharp/ClashSharp.Installer.Tests/InstallerCoordinatorTests.cs`
- `ClashSharp/ClashSharp.Installer.Tests/InstallerCoordinatorFaultMatrixTests.cs`
- `ClashSharp/ClashSharp.Installer.Tests/InstallerScenario.cs`
- `ClashSharp/ClashSharp.Installer.Windows/Transactions/WindowsInstallerProtectedStateStores.cs`

场景 fake 也必须模拟 helper-authoritative store，不能继续让 coordinator mirror 代替真实权限模型。至少新增：UAC-before-Prepared、helper committed / response lost、clear response lost、successful-null reload 和 read failure fallback 测试。

### P0：OTS 目标用户证书与 package

- 现有 `WindowsCurrentUserCertificateStoreAdapter` 会验证进程 token SID 等于 `TargetSid`，所以 alternate-admin helper 会 fail closed；它不会误写管理员 CurrentUser，但也无法完成目标用户证书操作。
- 首选候选是 native `CertOpenStore` + `CERT_SYSTEM_STORE_USERS`，且 store name 只能是 `<exact target SID>\TrustedPeople`；继续保留 thumbprint 与 DER SHA-256 双校验。
- 必须在 Windows 11 x64 标准用户 + 输入另一管理员凭据的 OTS VM 上取证。当前 pipe 方向是 parent server / helper client，`ImpersonateNamedPipeClient` 不能让 helper 反向 impersonate parent。
- 若目标 SID store 无法形成可靠证据，只允许 helper-authoritative ledger cut-point 包围 parent-side certificate mutation 的显式回退；绝不写入管理员 CurrentUser，也不放宽 protected root。
- exact-target-SID package inspector 已写但未编译，仍需接进 helper-only `CommitPackage` 并在 OTS VM 验证权限矩阵。

### P0：还没有的生产闭环

- 签名、hash-locked、独立 NativeAOT machine helper；
- 一个 UAC 生命周期内的 persistent authenticated broker；
- parent / helper 最终路径与 Authenticode 身份绑定、deadline、退出和 uncertain reconciliation；
- machine directory / ACL / SCM / association apply-remove 适配器；
- helper-authoritative certificate ledger；
- WPF production runtime composition；当前仍是 `MigrationPreviewInstallerRuntime`；
- 签名候选的 Windows E3 / E4、cut-point、重启和 OTS 证据。

## 6. 接盘后的精确启动顺序

### 步骤 0：只读确认

```bash
git status --short
git diff --check
./eng/check-linux-resource-budget.sh standard
```

门禁未 READY 就停在这里。不要启动 restore、compiler server、测试或渲染。

### 步骤 1：把当前静态增量恢复为真实 checkpoint

下面命令逐条、串行执行；每一条都重新过门并独占 host-wide lock：

```bash
./eng/check-linux-resource-budget.sh standard -- \
  /tmp/clashsharp-dotnet-10.0.201/dotnet test \
  ClashSharp/ClashSharp.Installer.Tests/ClashSharp.Installer.Tests.csproj \
  -c Release -p:Platform=x64 --no-restore \
  -m:1 -nr:false -p:UseSharedCompilation=false

./eng/check-linux-resource-budget.sh standard -- \
  /tmp/clashsharp-dotnet-10.0.201/dotnet test \
  ClashSharp/ClashSharp.Installer.Presentation.Tests/ClashSharp.Installer.Presentation.Tests.csproj \
  -c Release -p:Platform=x64 --no-restore \
  -m:1 -nr:false -p:UseSharedCompilation=false

./eng/check-linux-resource-budget.sh standard -- \
  /tmp/clashsharp-dotnet-10.0.201/dotnet build \
  ClashSharp/ClashSharp.Installer.Windows.Tests/ClashSharp.Installer.Windows.Tests.csproj \
  -c Release -p:Platform=x64 --no-restore \
  -m:1 -nr:false -p:UseSharedCompilation=false

./eng/check-linux-resource-budget.sh standard -- \
  /tmp/clashsharp-dotnet-10.0.201/dotnet build \
  ClashSharp/ClashSharp.Installer/ClashSharp.Installer.csproj \
  -c Release -p:Platform=x64 --no-restore \
  -m:1 -nr:false -p:UseSharedCompilation=false
```

如果 assets 不完整而确实需要 restore，只能先以同样的 standard wrapper 对 `ClashSharp/ClashSharp.slnx --locked-mode -p:Platform=x64` 做一次串行 restore，再回到上面顺序。不得无门禁 restore。

Linux 上不运行 Windows adapter tests；只交叉编译。完整 `dotnet format` 在 Linux 可能因 WindowsDesktop analyzer 缺少 `System.Composition` 崩溃，Linux 可先做受门禁的 whitespace 验证，完整 analyzer / format 以 Windows CI 为准。

首轮最可能暴露编译问题的位置：

- `IInstallerTransactionReader` 新 API 的引用和 XML public API 规则；
- `WindowsPackageRegistrationInspector.cs`；
- `WindowsTargetUserPackageCommitInspectorTests.cs`；
- Presentation theory 中 nullable enum / collection expression；
- `IWindowsPackageManagerFacade.FindPackagesForUser` 新显式 SID 签名的漏改实现或 fake。

### 步骤 2：实现 authority split，再补测试

先让步骤 1 绿色，再改 coordinator / helper ownership；每个小步只跑受影响的一个测试项目。不要在未编译堆积上继续增加 broker、SCM 或 UI。

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

- 当前 512 / 70 / 103 未经编译和执行；
- Windows tests 从未在 Linux 执行；
- WPF shell 没有 Linux runtime 证据，也没有 Windows UIA 证据；
- C# Installer 不是当前发布 authority；
- protected root / IPC / package inspector 是 primitive，不等于 authenticated broker；
- 当前 coordinator 权限模型仍错误，production composition 不得接线；
- CurrentUser certificate adapter 不支持 alternate-admin OTS 目标用户；
- `MigrationPreviewInstallerRuntime` 不执行安装；
- ICO 未完成；
- 没有签名候选、Windows E3/E4、reboot 或 cut-point 闭环。

## 10. 继续阅读顺序

1. [Installer / WPF 详细审计](2026-08-30-installer-wpf-rewrite-audit.md)
2. [生产就绪执行计划](2026-08-27-production-readiness-execution-plan.md)
3. [Machine-helper IPC 边界](../design/2026-08-30-installer-machine-helper-ipc.md)
4. [Protected-state root 边界](../design/2026-08-30-installer-protected-state-root.md)
5. [项目开发地图](2026-08-27-project-development-map.md)

接盘成功的第一个可审计里程碑不是“界面能打开”，而是：资源门 READY 后，当前 512 / 70 / 103 源码恢复为真实绿色 checkpoint；随后 coordinator 完成 helper-only write / transition / clear 与 parent read-only reload，并由 fault matrix 证明 UAC cancel、ack-loss、成功空读取和 clear reconciliation。
