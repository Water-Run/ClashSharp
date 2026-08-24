# ClashSharp 全量开发审计与优化路线图

- 审查日期：2026-08-10
- 审查基线：`main@8a69c27`
- 审查范围：解决方案结构、MVVM、WinUI 3、编码规范、现代 C#、LINQ、文档注释、主体与 Installer 权限边界、构建发布、安全、测试
- 审查方式：源码与配置静态审计、Release 构建、格式验证、.NET/Rust 测试、分析器基线、依赖与漏洞检查
- 当前状态：这是后续开发的权威待办汇总；P0-01 已于 2026-08-24 完成源码与程序集边界收口，其余整改、正式签名 Installer 和 Windows 真机矩阵尚未验收

## 1. 总体结论

ClashSharp 已具备可工作的分层、较强的运行时所有权保护、大规模自动化测试和严格的 Release 构建基础。当前代码能够干净编译，现有 .NET/Rust 测试全部通过。

但项目尚未满足以下发布目标：

1. **产品只有一个用户可见 Installer 和一个主体应用。** 当前用户可见入口符合方向，且 P0-01 已移除主体内的服务注册部署、配置和删除权限；Installer 的版本、身份、payload 与完整事务 authority 仍需继续收口。
2. **所有人写声明都有高质量文档。** C# 仍有 7,454 个声明点完全缺失文档，Installer Rust 约有 392 个声明缺失；现有文档也存在参数、返回值、异常和线程语义缺口。
3. **MVVM 与组合根完全收敛。** 分层方向正确，但 Presentation 仍存在静态 Service Locator，导航、页面生命周期和部分系统服务获取仍散落。
4. **Installer 可以作为安全发布入口。** 防降级、身份单一来源、payload provenance、TOCTOU、子进程超时、证书/卸载事务及真实 E2E 门禁仍需完成。
5. **分析器和现代化改造有明确语义。** 项目已经大量使用 C# 14 和 LINQ；后续应修复真正问题，不能机械追求 LINQ、`ConfigureAwait(false)`、`internal` 或空洞注释。

在本文列出的发布阻断项全部关闭、文档门禁清零并完成真实 Installer 验收前，不应把当前提交认定为稳定发布候选。

## 2. 产品与项目结构

### 2.1 目标产品拓扑

```text
ClashSharp-Installer.exe                 唯一用户可见安装/修复/升级/卸载入口
  └─ ClashSharp MSIX
      └─ ClashSharp.exe                 唯一用户可见主体应用
          ├─ ClashSharp.MihomoService.exe   主体内部 Windows Service
          ├─ ClashSharp.RecoveryWatchdog.exe 主体内部一次性恢复助手
          └─ mihomo.exe + GeoData           主体内部运行资产

测试/验证产物（不得进入正式 payload）
  ├─ ClashSharp.ProcessProbe.exe
  ├─ ClashSharp.StartupProbe.exe
  ├─ ClashSharp.TriggerProbe.exe
  ├─ ClashSharp.SettingsProbe.exe
  └─ SandboxTest
```

这里的“一个 Installer 和一个主体”按**用户可见产品入口**解释。MihomoService、RecoveryWatchdog、mihomo 和 GeoData 是主体的内部实现资产，不是独立产品。若未来要求发布包物理上只能包含两个 EXE，则必须另行重构 Windows Service 和异常恢复方案。

### 2.2 当前解决方案

`ClashSharp/ClashSharp.slnx` 当前包含 11 个 .NET 项目：

| 层/项目 | 责任 | 当前判断 |
|---|---|---|
| `ClashSharp.Core` | 领域模型、稳定基础契约 | 依赖方向正确 |
| `ClashSharp.Application` | 用例、协调、端口接口 | 方向正确，仍可继续承接主体业务编排 |
| `ClashSharp.Infrastructure` | SQLite、文件、进程、网络等适配器 | 方向正确，需继续吸收主体中的 IO 实现 |
| `ClashSharp` | WinUI 3、ViewModel、组合根及部分遗留服务 | 主体过重，服务注册权限已移除，静态单例等 MVVM 债务仍存在 |
| `ClashSharp.MihomoService` | LocalSystem 内部服务宿主 | 内部组件，必须仅由 Installer 部署 |
| `ClashSharp.RecoveryWatchdog` | 同用户一次性恢复助手 | 内部组件，必须纳入包内容契约 |
| 四个 `*Probe` | 跨进程测试探针 | 测试专用，必须不可发布/不可打包 |
| `ClashSharp.Tests` | 单元、集成、架构测试 | 覆盖基础强，但尚无覆盖率阈值 |

另有：

- `ClashSharp/Installer`：Rust 2024 + Slint 的唯一正式安装器。
- `ClashSharp/SandboxTest`：安装场景测试工具，不属于产品。

### 2.3 已确认的良好基础

- Core ← Application ← Infrastructure/Presentation 的总体依赖方向已经形成，并有架构测试保护。
- ViewModel 不直接依赖 WinUI 类型、Service、不自行创建 `HttpClient`，构造器也避免隐式加载。
- `AsyncRelayCommand` 已提供防重入、取消和统一错误汇聚，不应为了采用新库而机械替换。
- App、MainWindow、托盘和页面卸载路径已有较完整的资源清理意识。
- Runtime、Service IPC、mutation admission、配置 generation、恢复 journal 等高风险链路已有大量定向测试。

## 3. 验证基线

| 检查 | 结果 |
|---|---|
| `dotnet format ... --verify-no-changes --no-restore` | 通过 |
| Release x64 solution build | 通过，0 warning / 0 error |
| .NET tests | 2,229 通过，0 失败，0 跳过 |
| Installer `cargo fmt --check` | 通过 |
| Installer `cargo clippy --locked --all-targets -- -D warnings` | 通过 |
| Installer Rust tests | 62 通过 |
| SandboxTest fmt/clippy | 通过 |
| SandboxTest Rust tests | 11 通过 |
| NuGet vulnerable scan | 未发现已知漏洞 |
| 正式签名 Installer 构建 | 未执行；缺少完整受控 release 输入与真机验收 |

强制 `AnalysisLevel=latest-all` 且禁止增量分析后约有 1,977 个唯一诊断：

| 规则 | 数量 | 处理原则 |
|---|---:|---|
| CA2007 | 1,109 | 库/服务按上下文处理；WinUI continuation 不得机械添加 |
| CA2000 | 202 | 逐项确认所有权转移、Dispose 和异步生命周期 |
| CA1849 | 169 | 优先处理 UI/线程池中的同步阻塞；同步系统 API允许窄边界说明 |
| CA1515 | 145 | XAML Page、入口和公开契约不可机械 internalize |
| CA1032 | 109 | 只为真正公开异常补标准构造器，不恢复 BinaryFormatter 构造器 |
| CA5392 | 50 | 系统 DLL 搜索路径属于高优先级真问题，应修复并升 error |
| CA1308 | 48 | hash、ID 的 canonical lowercase 通常是有意行为，使用窄抑制 |
| CA1031 | 34 | 顶层 worker/event/process 边界可在过滤 fatal exception 后保留 |
| CA2100 | 33 | 区分真实动态 SQL 标识符与已验证整数 PRAGMA |

这些诊断是审计基线，不等于 1,977 个独立缺陷，也不能以批量 suppress 方式“清零”。

## 4. P0：发布前必须完成

### P0-01 Installer 成为唯一安装状态 authority

**现状证据**

- `ClashSharp/ClashSharp/Service/MihomoServiceManager.cs:223` 的 `DeployAsync` 可执行 `sc create/config`。
- 同文件 `:574` 的 `UninstallAsync` 可执行 `sc delete`。
- `MihomoServiceManagerFactory.cs:20` 仍在正式组合中注入部署上下文。
- `InstallerOwnershipArchitectureTests.cs:33` 只禁止公开入口，没有禁止内部部署权限。

**开发内容**

- [x] 删除主体中的 `DeployAsync`、`UninstallAsync`、`IMihomoServiceDeploymentContext`、`MihomoServiceDeploymentContext` 和相应提权 `sc.exe` 路径。
- [x] 主体只保留服务状态观察、认证 IPC 及运行期 start/stop/restart；不得创建、配置或删除 SCM 注册。
- [x] 将部署/卸载测试迁移到 Installer 契约或安装器 E2E。
- [x] 架构测试直接禁止主体出现 `sc create/config/delete`、部署上下文和 Installer-owned mutation。
- [x] 将 `StartupRestoreFallbackService.Uninstall` 重命名为准确的 startup registration removal 语义，避免被误认为产品卸载。

**关闭证据（2026-08-24）**

- `MihomoServiceManager` 的 SCM 边界已收窄为固定 `query` 与运行期故障回收所需的固定 `stop`，不再接受通用命令参数；服务注册、payload 与 owner 变更仅存在于 Installer `service_plan.rs`。
- 删除主体部署上下文、部署专用二进制信任校验器及相应 C# 部署/卸载测试；Installer 的 service plan/transaction 测试继续覆盖固定路径、owner 检查、create/config/delete、事务顺序和卸载。
- `InstallerOwnershipArchitectureTests` 同时扫描全部主体源码和生成的 `ClashSharp.dll`，禁止部署类型、内部 `DeployAsync`/`UninstallAsync`、`sc create/config/delete` 与 `binPath` 回归，并证明 Installer 保留唯一实现。
- 登录恢复 helper 及其 ViewModel/UI 调用统一改为 `RemoveRegistration` 语义。
- 无系统 mutation 验证：格式检查通过；Release x64 构建 0 warning / 0 error；.NET 2,204 项通过、0 失败、0 跳过；Installer fmt/clippy 与 62 项 Rust 测试通过。

**验收条件**

- [x] 主体程序集源码和 IL 中不存在服务安装、配置、删除能力。
- [x] 只有 Installer 能修改 MSIX、SCM 注册、Program Files payload、关联 owner 和信任证书；主体仅保留 SCM 状态查询和运行期 stop 故障回收。

### P0-02 禁止 Repair 隐式降级

**现状证据**

- `Installer/src/main.rs:663` 仅根据布尔 `is_installed` 选择 Repair。
- `Installer/src/main.rs:868` 使用 `-ForceUpdateFromAnyVersion`。

**开发内容**

- [ ] 查询并解析当前注册包的完整数字版本。
- [ ] 将 installed version 与 Installer 内嵌可信 payload version 比较。
- [ ] 默认拒绝 `installed > payload`，返回稳定错误码和本地化说明。
- [ ] 如未来需要回滚，单独设计显式 Rollback 操作、强警告、审计记录及数据兼容协议；不得复用普通 Repair。

**验收条件**

- 新装、同版本 Repair、升级均通过；旧 Installer 降级被确定性拒绝。

### P0-03 MSIX Identity 只有一个真相源

**现状证据**

- `ClashSharp/Package.appxmanifest:12` 定义 Name、Publisher、Version。
- `Installer/src/service_plan.rs:13` 另行硬编码 Name、Publisher、PublisherId、FamilyName。
- `Installer/build.rs:280` 当前只从 MSIX 提取 Version。

**开发内容**

- [ ] 从最终 MSIX 的 AppxManifest 解析 Name、Publisher、PublisherId/FamilyName、Version、ProcessorArchitecture 和 Application/Executable。
- [ ] 由构建脚本生成 Rust 常量，或与唯一声明源进行完整 fail-closed 比对。
- [ ] 禁止手工维护第二份 FamilyName/Publisher 常量。
- [ ] 增加合法更换 Publisher、错误架构、错误 family 和错误 executable 的构建契约测试。

**验收条件**

- Identity 任一字段不一致时构建失败；运行期查询目标完全由已验证的最终包派生。

### P0-04 建立精确 payload、来源与签名契约

**现状证据**

- 主项目仅通过 `AfterTargets=Build` 复制 Service/Watchdog，未形成明确的 MSIX Content 契约。
- `Installer/build.rs:210` 要求 service、mihomo、GeoData，但不要求 RecoveryWatchdog。
- `Installer/build.ps1:393` 从“最新” AppPackages 目录挑选 MSIX，并复制 `Dependencies/x64` 下全部 MSIX。
- `Installer/build.rs:533` 会把当时存在的任意 dependencies 固化为 hash anchor。

**开发内容**

- [ ] 将 MihomoService、RecoveryWatchdog、mihomo 和四项 GeoData 声明为明确的最终包内容。
- [ ] 在全新随机 staging 目录发布；拒绝 payload、dependencies 及祖先目录中的 reparse point。
- [ ] 解析最终 AppxManifest，要求精确一个 `Application` 且 executable 为 `ClashSharp.exe`。
- [ ] 建立完整 executable/file allowlist；显式禁止 Probe、SandboxTest、Installer 和第二 updater 进入 MSIX。
- [ ] 根据包 manifest 建立依赖 exact allowlist/count，并逐个验证 identity、publisher、architecture 和 signer。
- [ ] 生成 trust anchor 前复核主 MSIX 与依赖的 Authenticode signer/thumbprint。
- [ ] promotion 到 `release-artifacts` 后重新比较文件集合、长度和 SHA-256。

**验收条件**

- 最终 archive 必含 main/service/watchdog/mihomo/GeoData，且不含任何测试探针或第二产品入口。
- 任一旧文件、额外依赖、错误 signer、错误架构或 reparse 路径都会使构建失败。

### P0-05 关闭 payload 校验与使用之间的 TOCTOU

**现状证据**

- `Installer/src/trust_anchor.rs:45` 验证后只返回 `PathBuf`，验证句柄随后关闭。
- `Installer/src/main.rs:839` 和 `:850` 通过路径让另一个 PowerShell 进程重新打开 CER/MSIX。
- 注册后只复核 service/mihomo/GeoData 子集，没有复核整个主体内容。

**开发内容**

- [ ] 验证期间持有禁止写入/删除共享的文件及父目录句柄，直到导入/注册完成；或复制到受保护 immutable staging 后再消费。
- [ ] 对注册后的 package 做与 archive manifest 对应的完整文件复核，而不是只复核 machine subset。
- [ ] 添加“验证后替换 CER/MSIX、目录 rename/junction、并发写入”的攻击性测试。

**验收条件**

- PowerShell/AppXSVC 消费的字节与 Installer 验证的字节可被同一不可变性证据证明。

### P0-06 子进程必须有 deadline 和有界输出

**现状证据**

- `Installer/src/main.rs:3097` 使用无限 `wait_with_output()`。
- `Installer/src/main.rs:3169` 使用无限 `.output()`。
- 所谓 bounded details 实际拼接完整 stdout/stderr。

**开发内容**

- [ ] 为查询、部署、SCM、Appx 和 UAC 操作定义分级 deadline。
- [ ] 流式、有界读取 stdout/stderr；记录截断状态，禁止无界内存增长。
- [ ] 超时后终止完整 Job/process tree，并等待确定终态。
- [ ] 区分 UAC 用户取消、超时、进程失败和输出解析失败。
- [ ] 保证超时或取消后事务 journal 仍可由 Repair 恢复。

**验收条件**

- 模拟挂起和无限输出的 child process 不会永久卡住 UI，也不会导致内存无界增长。

### P0-07 真实 Installer E2E 成为发布门禁

**现状证据**

- `SandboxTest/scripts/Run-InSandbox.ps1:121` 直接导入证书并调用 `Add-AppxPackage`，绕过 Installer/UAC/服务事务。
- 同文件 `:204` 的多个场景仍为 skipped。
- `SandboxTest/Run-SandboxTest.ps1:218` 把 skipped 当作成功。

**开发内容**

- [ ] Sandbox/VM 必须运行最终签名开发 Installer，而不是直接安装 MSIX。
- [ ] 正式门禁中任何必需场景 skipped 都必须失败。
- [ ] 覆盖首次安装、同版本 Repair、升级、拒绝降级、卸载、UAC 取消、应用运行中、不同 SID、服务 pending-delete、重启和 payload 篡改。
- [ ] 在 Prepared、PackageCommitted、machine payload swap、association commit 等 cut point 强制结束进程，再验证 Repair 收敛。
- [ ] CI 至少校验报告 schema；正式 release pipeline 必须要求完整 Windows 场景通过。

**验收条件**

- 绿色报告证明实际 Installer、AppXSVC、UAC、SCM、journal 和卸载路径，而不是旁路脚本。

## 5. P1：高优先级正确性、安全与生命周期

### Installer

- [ ] **证书生命周期**：把“是否由本 Installer 添加”和精确 thumbprint 纳入持久事务；失败时补偿；卸载仅在无其他相关包依赖时移除。
- [ ] **durable uninstall**：增加 `UninstallPrepared/MachineRemoved/PackageRemoved/Verified` 等阶段，或建立等价的可重入卸载恢复协议。
- [ ] **worker 终态**：用 RAII guard 保证 `ACTION_RUNNING` 复位；处理 thread spawn 失败；在 worker 边界将 panic 转为稳定错误，移除生产 `expect`。
- [ ] **刷新状态竞争**：action 和 refresh 共用显式状态机及 generation id；动作期间切换语言只更新文本，不重新探测或覆盖 busy。
- [ ] **可信系统程序路径**：不再使用裸 `powershell.exe` 或 `reg`；固定外链优先用 `ShellExecuteW`，系统工具使用绝对 System32 路径和最小环境。
- [ ] **查询错误语义**：`Get-AppxPackage` 失败不得折叠为“未安装”；使用 `Result` 阻断破坏性操作。
- [ ] **卸载可用性**：payload 缺损时仍允许安全进入已安装产品的卸载路径；安装/Repair 完整性与卸载可用性分离。
- [ ] **roll-forward 契约**：删除永久不可达的 rollback 分支，明确当前策略是 durable roll-forward + 同一 Installer Repair；或真正实现并故障注入测试 rollback。
- [ ] **Installer UI 可访问性**：将 Rectangle/TouchArea 菜单换为标准控件或补 role/name/focus/键盘；允许 DPI/文本缩放；卸载增加本地化确认。

### C# 安全与资源所有权

- [ ] **CA5392**：为所有 system DLL P/Invoke 限定 `System32`，或迁移到 `LibraryImport`；完成后将 CA5392 升为 error。
- [ ] **动态 SQL**：将 `LogStorageMaintenance`、`LogStorageSchema` 的任意标识符 API 改为 closed enum、allowlist 或专用迁移方法；已验证整数 PRAGMA 只做窄 suppress。
- [ ] **Profile gate**：修复 `ProfileCatalogService` 永久保留且不 Dispose `SemaphoreSlim` 的字典，复用引用计数 gate 模式。
- [ ] **后台任务 owner**：`StartupConflictDetectionService` 取消等待后仍运行的任务必须由明确 owner 观察并完成。
- [ ] **页面 CTS**：Profiles、Links 等页面使用 `Interlocked.Exchange` 后 cancel + dispose，并加入反复导航测试。
- [ ] **日志边界**：在存储入口统一长度限制、控制字符清理、URL/token/path 脱敏和稳定诊断码；ViewModel 不直接持久化任意 `Exception.Message`。
- [ ] **覆盖率门禁**：对 Installer transaction、身份校验、卸载、日志边界和关键状态机建立 line/branch 阈值，不能只有 package 引用。

## 6. 文档注释全量整改

### 6.1 当前统计

扫描排除了 `bin/`、`obj/`、`artifacts/`、`.worktrees/`、`*.g.cs`、`*.g.i.cs` 和 `*.generated.cs`，保留测试代码。C# 按源码声明点统计，不合并 partial 声明，不统计 lambda/accessor/compiler-generated 成员。

| 范围 | 声明 | 已有文档 | 完全缺失 |
|---|---:|---:|---:|
| C# 生产代码 | 8,489 | 3,792 | 4,697（55.3%） |
| C# 测试 | 3,885 | 1,128 | 2,757（71.0%） |
| C# 合计 | 12,374 | 4,920 | 7,454（60.2%） |
| Installer Rust（含生成 public const） | 约 632 | 240 | 约 392 |

C# 生产代码按访问性：

- public/隐式 public：4,222，缺 1,716。
- internal：996，缺 396。
- private：3,247，缺 2,565。

现有生产文档仍有：

- 672 个参数说明缺失。
- 519 个非 void/异步返回语义缺失。
- 96 个仅 `<inheritdoc/>`，其继承目标不一定完整。
- 约 290 个 public callable 直接 guard/throw，但异常契约不完整。
- 多个并发类型未说明线程安全、串行化、重入、所有权和关闭语义。

Rust 当前有 119 个返回 `Result` 的函数，但整个 Installer 没有任何 `# Errors`；34 个外部 public `Result` 函数全部缺失错误契约。

### 6.2 典型证据

- `ClashSharp/ViewModel/SettingsViewModel.cs`：284 个声明缺失。
- `ClashSharp.Infrastructure/Processes/WindowsJobProcessLauncher.cs`：145 个声明缺失，包含 Win32 结构、字段和所有权语义。
- `ClashSharp/Service/ClashDataPackageService.Transaction.cs`：123 个声明缺失。
- `ClashSharp.MihomoService/MihomoChildSupervisor.cs`：79 个声明缺失，锁、门和后台任务尤其需要线程说明。
- `ClashSharp/Service/AppSettingsService.cs:846`：四个构造器 `<param>` 错放到 class，且大小写不匹配。
- `ClashSharp/ViewModel/TriggerEditorOption.cs:4`：缺少泛型参数和 positional record 参数说明。
- `Installer/src/lib.rs:9`：公开模块无 module documentation。
- `Installer/src/main.rs:190`：`TextPack` 大量字段没有文档。
- `Installer/src/installer_transaction.rs:52`、`service_plan.rs:412`：安全/持久化结构字段没有文档。
- `Installer/build.rs`、集成测试 crate 缺少 crate/module 文档。

### 6.3 文档质量契约

`CodingStyle.md` 必须从“private 成员按复杂度选择性注释”升级为以下规则：

- [ ] 所有人写类型、构造器、函数、局部函数、属性、索引器、字段、事件、delegate、枚举成员都有文档。
- [ ] `<summary>` 或 Rust 首段必须解释职责、约束或语义，禁止只复述符号名。
- [ ] `<param>`、`<typeparam>` 与签名逐项精确匹配；positional record 参数同样适用。
- [ ] 非 `void`、`Task`、`ValueTask` 明确返回含义；属性说明值域、单位、默认值或状态语义。
- [ ] guard/direct throw 和稳定失败路径说明 `<exception>`/`# Errors`。
- [ ] `CancellationToken` 说明取消时机、效果以及抛异常还是返回结果。
- [ ] 同步原语相关类型说明线程安全、串行化、重入、锁顺序、所有权和释放。
- [ ] 文件、句柄、流、CTS、Semaphore、Job/process 等说明资源所有权和 dispose/kill 责任。
- [ ] P/Invoke、unsafe、提权、持久化和事务代码说明安全边界与不变量。
- [ ] `<inheritdoc/>` 只允许真实 override/interface implementation，且继承目标本身必须通过完整性门禁。
- [ ] 测试文档说明被保护的行为/回归契约，而不是重复测试方法名。
- [ ] PowerShell 提供 comment-based help；Slint 的公共属性、callback、状态值和副作用也要记录。

### 6.4 自动门禁

编译器 XML 文档警告统一应用于全部项目：

```xml
<GenerateDocumentationFile>true</GenerateDocumentationFile>
<WarningsAsErrors>
  $(WarningsAsErrors);CS1570;CS1572;CS1573;CS1587;CS1591;CS1711;CS1712;CS1734;CS1735
</WarningsAsErrors>
```

CS1591 不能覆盖 private/internal/local，也不能验证高质量语义。因此需要仓库内自定义 Roslyn analyzer 或等价的源码架构测试，检查全部声明、参数、返回、异常、取消、继承和并发契约。生成代码通过 `// <auto-generated/>`、`GeneratedCodeAttribute` 和明确输出目录排除。

Installer `Cargo.toml` 增加：

```toml
[lints.rust]
missing_docs = "deny"

[lints.clippy]
missing_docs_in_private_items = "deny"
missing_errors_doc = "deny"
missing_panics_doc = "deny"
```

CI 增加：

```powershell
cargo clippy --manifest-path ClashSharp/Installer/Cargo.toml --all-targets --all-features --locked -- -D warnings
cargo doc --manifest-path ClashSharp/Installer/Cargo.toml --no-deps --document-private-items
```

全量缺口不得通过按符号名生成空洞摘要修复。实施顺序必须是：错位文档 → 公共接口 → 实现继承 → 类型/状态 → private 生命周期/并发 → 测试 → 门禁升 error/deny。

## 7. MVVM 与 WinUI 3 整改

### 7.1 组合根与依赖注入

- [ ] 删除 `Presentation/Composition/LegacyPageServiceBridge.cs` 的静态 `.Instance` 解析。
- [ ] AppHost 成为唯一 composition root，负责创建 window scope、page factory 和 navigation service。
- [ ] View/Page 只接收窄依赖，不暴露 `IServiceProvider`。
- [ ] 用 `IPageFactory` 或受控 route descriptor 解决 `Frame.Navigate(Type)` 对参数less page 的依赖。
- [ ] 把主体 `Service/` 中纯业务编排迁入 Application，把 SQLite、文件、网络、进程和系统 API 适配迁入 Infrastructure。
- [ ] 注入 initiating Window/XamlRoot/dispatcher，逐步移除全局 `App.MainWindow` 获取。

### 7.2 导航与页面生命周期

- [ ] 集中维护 route、back stack、NavigationView selection 和 NavigationFailed。
- [ ] 页面不再直接跳转其他页面；由 navigation service 接收语义化目标。
- [ ] `Frame.Navigate` 返回 false 或 NavigationFailed 时提供稳定诊断。
- [ ] About 页面使用 PageLoadSession，离页取消网络更新检查。
- [ ] Connections 将 latest-wins refresh session 与 serialized mutation gate 分离。
- [ ] 页面事件和命令使用明确 owner；Loaded/Unloaded 必须对称且可重复。

### 7.3 绑定、命令和可访问性

当前 20 个 XAML 文件有 489 个 `{Binding}`、0 个 `{x:Bind}`。迁移原则：

- [ ] 对 ViewModel、稳定属性、事件和 DataTemplate 逐页使用强类型 `x:Bind`。
- [ ] 动态 DataContext、运行时路径和确需 late binding 的场景保留 `{Binding}`。
- [ ] 迁移后验证编译期类型、更新模式、converter、null/default 和页面生命周期。
- [ ] Connections 等页面应绑定 ViewModel 命令，不由 click handler 直接调用业务方法。

可访问性：

- [ ] `MasterInfoTile` 从可点击 Grid/Pointer 处理器改为 Button、ToggleButton 或正确的自定义 Control。
- [ ] 所有 glyph-only 按钮提供 AutomationProperties.Name、Tooltip、键盘操作、焦点态和 CanExecute/IsEnabled。
- [ ] 自定义 modal overlay 改用 ContentDialog，或完整实现 Escape、焦点 trap/恢复、UIA dialog 语义和背景隔离。
- [ ] 检查高对比度、200% 文本缩放、触控目标、Narrator 和纯键盘导航。

### 7.4 拆分类和资源

- [ ] 将 3,481 行 `SettingsViewModel` 按通用、网络、服务、更新、数据、外观等领域拆分子 ViewModel。
- [ ] 将 2,386 行 `ProfileCatalogService` 按 catalog、subscription schedule、download/import、history 和 gate 拆分。
- [ ] 将 Rust `main.rs` 的 UI controller、包部署、PowerShell runner、状态探测分离。
- [ ] 将 `service_plan.rs` 的计划模型、机器脚本模板、事务转换和验证分离。
- [ ] 合并 `App.xaml` 与 `Themes/Generic.xaml` 的重复资源并定义唯一资源所有者。

## 8. 现代 C#、LINQ 与分析器策略

### 8.1 已有现代语言基础

仓库已统一使用 C# 14、Nullable、file-scoped namespace，并大量使用 target-typed `new`、collection expression、record、`required`、`init` 和 LINQ。目标不是增加“新语法数量”，而是让代码更清晰、更可证明。

### 8.2 推荐的 LINQ 优化

- [ ] `ProfileCatalogService.cs:189` 的纯投影可改为 `Select(...).ToArray()`。
- [ ] `GetDueSubscriptionLinks` 当前逐项 `Find`，应先建立 schedule dictionary，再 `Where/Select`，把 O(links × schedules) 降为 O(n)。
- [ ] `LogsViewModel.cs:191` 不应在绑定 getter 每次重新投影；在数据变更点建立只读快照。
- [ ] 对纯过滤、映射、分组、去重和 immutable snapshot 优先采用意图明确的 LINQ。

以下场景保留显式循环：事务步骤、错误分段、取消点、文件/流/句柄生命周期、Installer/Service 状态机、逐项安全校验和性能热路径。禁止带副作用的复杂 LINQ 链。

### 8.3 分析器升级顺序

1. 修复 CA5392、真实 CA2100、明确 CA2000、关键 CA1849。
2. 按程序集配置 CA2007，而不是全仓统一。
3. 收紧纯实现类型的可见性；保留 XAML、入口和公开契约。
4. 为 hash/ID lowercase、顶层 exception boundary 等建立带理由的局部 suppress。
5. 每批清零后再把对应规则从 suggestion 提升为 warning/error。

`.editorconfig` 当前把 CA1001、CA1822、CA1859 整体降级。应逐项评估后改为更窄的项目、目录或符号级策略，不能长期全局关闭。

### 8.4 依赖维护

审计时未发现 NuGet 已知漏洞，但多个包有同 major/minor 的补丁版本可用，包括 `Microsoft.Data.Sqlite`、`Microsoft.Extensions.DependencyInjection`、`Microsoft.Extensions.Hosting.WindowsServices`、`System.Drawing.Common`、SQLitePCLRaw、Windows SDK BuildTools、Windows App SDK 1.8 和 xUnit runner。

- [ ] 先在独立提交中更新同分支安全/修复补丁并重新生成 lockfiles。
- [ ] Windows App SDK 保持 1.8 patch 更新；不要把 2.x major 迁移混入常规维护。
- [ ] .NET SDK pin 更新必须与构建机已安装 SDK、CI 和正式签名环境同时验证。
- [ ] Installer 的 RustSec 警告按 Windows target dependency tree 分类；升级 Slint/transitive dependencies 后重跑 `cargo audit`，不以全局 ignore 掩盖。

## 9. Probe、测试与发布隔离

- [ ] 四个 Probe 设置 `IsTestProject=true`、`IsPublishable=false`、`IsPackable=false`。
- [ ] 评估移动到 `Tests/Probes` 或测试专用 solution，减少正式解决方案产品歧义。
- [ ] SandboxTest 明确标记测试工具，release build 不得拷贝到 Installer payload。
- [ ] 最终 archive 使用 allowlist 与 denylist 双重验证。
- [ ] 架构测试断言 Manifest 恰好一个 Application、没有第二 updater、Probe 不可发布。

## 10. 实施里程碑

### 里程碑 A：产品边界与 Installer 发布安全

完成 P0-01 至 P0-07，并同步完成证书、卸载 journal、worker/state machine、可信系统程序路径和 Probe 发布隔离。

**退出条件**：主体无安装权限；干净 staging 生成的签名 Installer 通过真实 install/repair/upgrade/reject-downgrade/uninstall/cut-point 矩阵。

### 里程碑 B：文档规范与报告门禁

更新 `CodingStyle.md`，实现 C#/Rust/PowerShell/Slint 文档规则；先以报告模式输出完整缺口，修复错位文档。

**退出条件**：门禁能准确覆盖全部人写声明，不误报生成文件，不允许空洞占位文档。

### 里程碑 C：全量文档清零

按 Core → Application → Infrastructure → Service/Installer → Presentation/ViewModel → Tests 的顺序补齐，并逐子系统审阅异常、取消、线程和资源语义。

**退出条件**：C# 和 Rust 缺失数为 0，XML/rustdoc/自定义质量规则全部通过。

### 里程碑 D：MVVM 与 WinUI 收敛

完成 composition root/page factory/navigation、页面生命周期、命令绑定、关键 `x:Bind`、可访问性和大类拆分。

**退出条件**：Presentation 不再使用 static `.Instance`；导航和 window context 有单一 owner；键盘/Narrator/缩放矩阵通过。

### 里程碑 E：定向现代化和质量门禁

修复高价值分析器问题、LINQ 性能热点、依赖补丁及覆盖率门槛。

**退出条件**：新增规则均已清零并提升到 error；没有未经说明的全局 suppress；所有 lockfile 可重现。

### 里程碑 F：最终验收与发布

执行全部源码、构建、测试、文档、Installer、VM、签名和 payload 验证。

**退出条件**：本文第 11 节 Definition of Done 全部有直接证据，工作树干净后才允许最终发布提交/推送。

## 11. Definition of Done

只有以下各项全部成立，才能称为“整体梳理、检查和优化完成”：

- [ ] 用户可见产品只有一个 Installer 和一个主体 Application。
- [ ] 主体不包含安装、升级、Repair、迁移或卸载 authority。
- [ ] Internal service/watchdog/mihomo/GeoData 精确存在，Probe/Sandbox/第二 updater 精确不存在。
- [ ] Identity、版本、架构、publisher、signer 和 payload hash 都来自或绑定到唯一可信源。
- [ ] 默认禁止降级；完整安装和卸载均可中断恢复。
- [ ] 所有子进程有 deadline、有界输出和完整 process-tree 终止。
- [ ] 真实 Installer Windows 矩阵全部通过，必需场景没有 skipped。
- [ ] 12,374 个 C# 声明点及约 632 个 Installer Rust 声明点全部通过严格文档门禁。
- [ ] PowerShell help 和 Slint 公共状态/callback 文档完整。
- [ ] MVVM 不再使用静态 Service Locator；导航、Window、页面生命周期和异步任务所有权明确。
- [ ] 关键 WinUI 页面通过编译绑定、键盘、Narrator、高对比度和文本缩放检查。
- [ ] 选定分析器规则全部清零并升为 error，没有无理由的全局 suppress。
- [ ] Release build、格式、全部 .NET/Rust tests、Rustdoc、覆盖率、漏洞检查均通过。
- [ ] 正式 Installer、MSIX 和依赖签名验证通过，最终 archive 精确契约通过。
- [ ] 仓库中没有意外生成物、密钥、证书、旧 payload 或未归属改动。

建议的最终无系统 mutation 验证命令：

```powershell
dotnet format ClashSharp/ClashSharp.slnx --verify-no-changes --no-restore
dotnet build ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build --collect:"XPlat Code Coverage"

cargo fmt --manifest-path ClashSharp/Installer/Cargo.toml -- --check
cargo clippy --manifest-path ClashSharp/Installer/Cargo.toml --locked --all-targets --all-features -- -D warnings
cargo test --manifest-path ClashSharp/Installer/Cargo.toml --locked --all-targets
cargo doc --manifest-path ClashSharp/Installer/Cargo.toml --no-deps --document-private-items

cargo fmt --manifest-path ClashSharp/SandboxTest/Cargo.toml -- --check
cargo clippy --manifest-path ClashSharp/SandboxTest/Cargo.toml --locked --all-targets -- -D warnings
cargo test --manifest-path ClashSharp/SandboxTest/Cargo.toml --locked --all-targets
```

这些命令不能替代正式发布机上的 `Installer/build.ps1`、SignTool 验证、最终 archive 清单以及 Windows Sandbox/VM/UAC/SCM/AppXSVC 真机矩阵。

## 12. 工作树与提交边界说明

审查开始前工作树已经存在 `ClashSharp/ClashSharp.Tests/packages.lock.json` 的本地修改，新增 132 行 RID lock 信息。该修改不属于本次文档整理，没有被覆盖或纳入本文档提交。

后续进行依赖升级时，应先确认该 lockfile 修改的所有者和意图，再决定保留、合并或重新生成；不得静默覆盖用户已有修改。
