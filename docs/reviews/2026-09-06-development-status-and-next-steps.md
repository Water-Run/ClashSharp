# ClashSharp 开发状态与推进计划

日期：2026-09-06（Asia/Shanghai）<br>
核对基线：本地 `main@60aa5dc` 加本轮工作树改动；开始时工作树干净。<br>
定位：当前开发状态和后续执行入口。历史审查保留原始快照，不能直接作为当前待办或发布证明。

## 1. 当前判断

主体功能已成形，当前工作重点是运行可靠性和安装发布闭环。解决方案包含 18 个 .NET 工程，使用固定 .NET SDK `10.0.201` / C# 14；用户侧入口为 WinUI 3 主程序和唯一 C# / WPF Installer。Rust/Slint 安装器已退役。

本轮完成订阅操作锁的资源回收、双版本 PowerShell AST 检查和 WinUI 格式检查的 x64 环境修复。本地 Release x64 构建及 3231 项测试通过，但尚不满足生产发布条件：安装器执行编译门仍默认关闭，签名候选、真实安装入口、VM 故障恢复和发布 promotion 证据尚未闭环。

| 领域 | 当前实现与证据 | 尚需推进 |
|---|---|---|
| 主体分层 / MVVM | Core、Application、Infrastructure、Presentation 与 ViewModel 边界已有架构测试；两个产品入口、公开 XML 文档与 System32 P/Invoke 约束已落地 | 持续按权限、取消和资源所有权审查新增代码 |
| 代理运行与恢复 | 主控、接管模式、TUN/Core owner、Service IPC、Watchdog 与恢复事务已有实现和测试 | 同一签名包的真实代理、TUN、崩溃和重启恢复验收 |
| 配置 / 订阅 | CRUD、下载、校验、调度、历史与回滚已实现；本轮补齐按链接操作锁的空闲回收 | 活跃配置切换、设置 generation 与回滚的联合验证；长期运行 soak |
| 触发器 | 持久化、匹配、调度、outbox、恢复和编辑页面均已有；架构账本 P1-02～P1-05 已关闭 | 签名产品运行 smoke，不重复开启已关闭架构项 |
| 连接 / 日志 / 统计 | WebSocket 实时链路、SQLite 存储与页面已实现 | Connections 刷新和关闭操作仍共用 `_loadSession`；补齐日志入口限长、控制字符和敏感内容边界审查 |
| 设置与页面生命周期 | typed settings、JSON generation、desired/applied 与页面取消基础已存在 | 架构账本 P1-06～P1-08 的完整事务矩阵；Connections 快速导航与操作取消语义 |
| Installer | Core、Presentation、Windows adapters、parent engine、认证 helper 与 durable journal 已接通源码；本轮开发机安全集 985 项通过 | 跨 owner reassociation 仍拒绝；标准用户/alternate-admin、目标 SID、单 UAC、崩溃与重放矩阵 |
| Sandbox | 严格报告 schema、scenario、runId 和步骤校验已实现，报告契约双版本通过 | `install-only` 仍直接安装证书/MSIX；launch、startup、cleanup 等场景仍显式失败，尚未验证真实 Installer |
| 构建输入 | locked restore、Mihomo 固定版本/长度/SHA-256、精确打包契约已存在 | 本工作树没有 `Binaries/GeoData/manifest.json`；需准备四项 GeoData 的受控来源、许可、版本和 digest |
| CI / 发布 | 单一 Windows CI 覆盖构建、格式、测试、覆盖率和 Sandbox 报告；本轮新增 PowerShell AST 门禁 | 仓库尚无独立 candidate/release workflow；受控签名、SBOM、provenance、VM 结果绑定与 promotion 待实现 |

现状依据为当前源码和本轮本机验证；未取得本轮提交的远端 CI、签名包或 VM 报告。历史文档的 CI run 和旧测试数量不累计到本轮证据中。

## 2. 本轮已实现

### DEV-01：回收空闲订阅操作锁

原 `ProfileCatalogService` 用 `Dictionary<string, SemaphoreSlim>` 永久保存每个访问过的链接 ID。删除链接后条目仍被服务引用，长期创建、操作和删除订阅会让锁表随历史 ID 数量增长。

现在将已有配置导入锁推广为 [`ProfileOperationGate`](../../ClashSharp/ClashSharp/Service/ProfileOperationGate.cs)，配置导入与订阅分别持有独立实例。订阅检查、导入、编辑、删除均通过 `using` lease 覆盖原有事务范围：

- 同一 key 串行；不同 key 在该 gate 内互不阻塞。上层进程级 mutation coordinator 的排序约束仍有效。
- 执行者和等待者共同持有引用；取消等待、异常返回或正常结束都会释放各自引用。
- 最后一个引用退出后，从表中移除 exact entry 并释放 semaphore。
- 已归零 entry 不允许复活，旧 lease 重复释放也不会放行新的执行者。

[`ProfileOperationGateTests`](../../ClashSharp/ClashSharp.Tests/Unit/Services/ProfileOperationGateTests.cs) 共 8 项通过，其中新增 6 项，覆盖 256 个历史 key 回收、取消、等待者交接、重复释放、异常退出，以及 16 个 worker / 4 个 key / 1024 次操作的并发互斥与最终清空。

### DEV-02：将 PowerShell AST 语法检查接入 CI

新增 [`eng/Test-PowerShellSyntax.ps1`](../../eng/Test-PowerShellSyntax.ps1)，通过当前 PowerShell 的 `Parser.ParseFile` 检查 Git 受控及未忽略的新 `.ps1` / `.psm1` 文件，不执行被检查脚本。发现失败、空文件集合、源文件缺失或任意语法错误都会失败，并给出文件、行、列和诊断标识。

CI 在 Windows PowerShell 5.1 和 PowerShell 7 中分别运行该脚本。本轮两个版本均成功解析 10 个源文件。隔离 Git fixture 验证了带空格路径、子目录调用、未跟踪新模块、ignored 文件排除，以及 tracked/untracked 错误脚本均被拒绝。含 `throw` 的有效源文件通过解析，确认没有执行源文件。

该门禁证明语法可以解析；带 `#Requires -Version 7.0` 的工具仍须按其声明在 PowerShell 7 执行，不能把 AST 成功当成 5.1 运行兼容证明。

### DEV-03：修复格式检查的 WinUI 加载环境

原 `dotnet format` 返回 0，但诊断日志显示主体项目加载失败：`WindowsAppSDKSelfContained requires a supported Windows architecture.`。这类退出码不足以证明整个解决方案都被检查。

[CI](../../.github/workflows/ci.yml) 现在只在格式检查步骤设置 `Platform=x64`；本地执行方式同步写入 [`CodingStyle.md`](../../CodingStyle.md)。Installer Core / Presentation 的测试仍使用 AnyCPU 产物路径。

## 3. 后续执行队列

以下按依赖推进；“完成”必须附对应验收证据。工作量按可独立审查的批次划分，不在环境和签名输入未知时承诺日期。

| 顺序 / ID | 优先级 | 具体工作 | 验收条件 |
|---|---|---|---|
| 1 / NEXT-01 | P0 | 固定候选的 GeoData、runtime、SDK 和签名输入；建立受控候选构建与证据目录 | 干净 checkout 可复现取得四项 GeoData 与全部 runtime 输入；来源、许可、版本、长度、digest 可追溯 |
| 2 / NEXT-02 | P0 | 完成显式 Repair 换绑 UI/parent policy 和双 owner root/SCM 协议 | 普通 Repair 不换绑；明确确认路径才允许 reassociation；失败、旧 owner 残留与重放有可运行测试 |
| 3 / NEXT-03 | P0 | 将 Sandbox 场景改为调用真实 Installer，补齐 launch/startup/cleanup | 报告绑定候选 digest、scenario、runId；包、进程、服务、证书、payload 和代理后置条件均有真实观测；必需场景无 skipped |
| 4 / NEXT-04 | P0 | 在隔离 Windows 11 x64 VM 执行标准用户和 alternate-admin 故障矩阵 | 同一签名候选验证 install/repair/upgrade/reject-downgrade/uninstall、单 UAC、精确 SID、每个 durable cut-point、断连/崩溃/重启/篡改恢复 |
| 5 / NEXT-05 | P0 | 接通受保护 candidate → VM → promotion 流程 | 只提升已通过矩阵的同一 artifact digest；具有签名、时间戳、SBOM、provenance、发布说明和恢复手册 |
| 配套 / NEXT-06 | P1 | 分离 Connections latest-wins 刷新与串行关闭操作 | 刷新不取消已提交的 close；关闭操作按序完成；页面卸载能取消并观察全部任务；实时流不会用旧结果覆盖新状态 |
| 配套 / NEXT-07 | P1 | 完成 settings generation 与运行时事务矩阵 | desired/applied、持久化失败、取消、回滚和重放状态一致；按账本要求关闭 P1-06～P1-08 |
| 配套 / NEXT-08 | P1 | 补齐日志边界、无障碍 smoke 与发布运维文档 | 日志长度/控制字符/敏感内容边界测试通过；键盘、Narrator、高对比度、200% 缩放验证有记录；补齐 SECURITY、支持策略和变更记录 |

NEXT-06～NEXT-08 中会改变产品产物的工作应在最终候选冻结前完成。NEXT-02～NEXT-04 的实现和取证可以交替推进；任何修改候选的修复都要重新生成并验证候选。只有全部发布条件闭环后，才允许调整正式打包对 mutation-runtime 的启用策略。

## 4. 本轮验证记录

环境：Windows x64，固定 SDK `10.0.201`；Windows PowerShell `5.1.26100.9223`、PowerShell `7.6.5`。

| 检查 | 结果 |
|---|---|
| solution locked restore | 18 工程成功，未修改依赖锁文件 |
| Release x64 solution build | 成功，0 warning / 0 error |
| 主体 `ClashSharp.Tests` | 2246 / 2246 通过，0 failed / 0 skipped |
| Installer Core | 548 / 548 通过，0 failed / 0 skipped |
| Installer Presentation | 89 / 89 通过，0 failed / 0 skipped |
| Installer Windows 开发机安全集 | 348 / 348 通过，0 failed / 0 skipped |
| PowerShell AST | 5.1 / 7 均解析 10 文件，0 错误；错误输入拒绝验证通过 |
| Sandbox report contract | 5.1 / 7 均通过；未启动 Sandbox |
| 完整 x64 workspace format | 通过，退出码 0；18 项目正常加载，无 workspace 警告；0 个文件需要格式化 |
| `git diff --check` | 通过；新增文件另外复核 whitespace 和末尾换行 |

共执行 3231 项 .NET 测试。`WindowsCurrentUserCertificateStoreAdapterTests` 的 3 项测试明确未选择，留在隔离测试环境执行；它们不计入上述通过数。没有执行安装器 mutation、签名打包或修改开发机代理/服务/证书存储。

测试结果保存在各测试项目的 `TestResults/development-2026-09-06.trx`；Windows 安全集使用 `development-2026-09-06-safe.trx`。格式诊断保存在本地 `artifacts/verification/format-x64-2026-09-06.log`。这些生成文件被 Git 忽略，不是发布候选的不可变证据。

可复现命令（仓库根目录）：

```powershell
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
dotnet build ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore

# 只为格式检查进程设置 Platform，结束后恢复调用环境。
$previousFormatPlatform = $env:Platform
try {
    $env:Platform = 'x64'
    dotnet format ClashSharp/ClashSharp.slnx --verify-no-changes --no-restore --verbosity diagnostic
} finally {
    $env:Platform = $previousFormatPlatform
}

dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build
dotnet test ClashSharp/ClashSharp.Installer.Tests/ClashSharp.Installer.Tests.csproj -c Release -p:Platform=AnyCPU --no-build
dotnet test ClashSharp/ClashSharp.Installer.Presentation.Tests/ClashSharp.Installer.Presentation.Tests.csproj -c Release -p:Platform=AnyCPU --no-build
dotnet test ClashSharp/ClashSharp.Installer.Windows.Tests/ClashSharp.Installer.Windows.Tests.csproj -c Release -p:Platform=x64 --no-build --filter 'FullyQualifiedName!~WindowsCurrentUserCertificateStoreAdapterTests'

powershell.exe -NoProfile -File eng/Test-PowerShellSyntax.ps1
pwsh -NoProfile -File eng/Test-PowerShellSyntax.ps1
powershell.exe -NoProfile -File ClashSharp/SandboxTest/Test-SandboxReportContract.ps1
pwsh -NoProfile -File ClashSharp/SandboxTest/Test-SandboxReportContract.ps1
git diff --check
```

## 5. 文档索引与证据边界

- 本文：当前状态、已交付批次、后续顺序与本轮验证。
- [2026-08-31 总审查](2026-08-31-project-structure-mvvm-code-quality-installer-audit.md)：主体分层、Installer 权限边界和发布阻断细节。
- [2026-08-27 开发地图](2026-08-27-project-development-map.md)与[生产就绪执行计划](2026-08-27-production-readiness-execution-plan.md)：历史测绘与迁移期任务；Rust、旧进度和旧数量不能视为当前状态。
- [架构稳定账本](../architecture/stabilization-ledger.md)：长期问题的正式关闭证据。本文不自动修改其 Closed 状态、reviewer 或 closure commit。

源码实现、本地测试、目标提交的远端 CI、最终签名候选 VM 证据分别记录。本轮结果属于本地验证；不能据此标记 Installer 可发布或项目生产就绪。
