# ClashSharp 第一轮修复及第二轮审查汇总审计

审计时间：2026-11-05。审计基线为 `102f8cd`，产品源码为 `31d693a`；第一轮修复对比范围为 `0a3ce8e..31d693a`，包含 `332f573` 与 `31d693a` 两个产品提交。当前工作区还包含未提交的第二轮报告、执行账本修改，以及原有交接文件。本文不把这些文档变动计入产品源码。

审计目标是核对第一轮 A01–A08 的实现、调用边界和新增测试，复核第二轮 B01–B05，并补记复审发现的遗漏。证据按下列层次区分：**调用链确认**表示可从当前生产源码推导触发与影响；**边界确认**表示源码条件成立但实际负载或运行频率未知；**待实测**表示关键的真实内核或跨组件条件尚未复现。

## 后续修复落实

本节记录审查之后的源码处理；下方发现和原审计验证记录保留审计时的结论。

| 发现 | 当前处理 | 回归证据 |
| --- | --- | --- |
| B01 | 配置列表、配置解析及主控从已安装配置身份读取；主控不再用失败目标覆盖观测名称、节点数、订阅和 DNS。 | 配置目录活动标签、主控目标与观测分离。 |
| B02 | 采样前后核对配置完整性，把配置身份随样本传到 SQLite；累计计数跨配置版本的增量归入 `unknown`，避免分配给新目标。旧库幂等增加身份列。 | 样本身份优先于目标回调、同内核配置变化的未知归属。 |
| B03/C03 | 明细省略以带总数的类型化结果通知连接页，保留关闭全部；触发器直接读取可信总数，无需完整行。 | 明细省略后的页面关闭与恢复、触发器探测非零总数。 |
| B04/C01 | 命名管道 HTTP 按流扫描计数和总行数，超过明细预算时丢弃行，避免先触发旧的 4 MiB/100,000 节点限制。 | 25,000 条、超过旧节点预算、分块边界及数组后置计数；拒绝重复根键、非法行和负计数。 |
| C02 | SQLite 持久记录明细缺口，统计页说明连接、节点和规则统计不完整；清除历史时清除缺口记录。 | 缺口保存、累计总量保留和清除。 |
| B05 | 应用生成 TUN 候选后调用与服务相同源码的只读策略验证，提升配置前返回稳定诊断；服务继续独立复核。 | 普通配置保留 `geox-url`、TUN 提前拒绝；仓库锁定 Mihomo v1.19.33 的 `-t` 已确认普通样例有效。 |

扫描仍受 64 MiB 文档、64 KiB 单 token、深度 32 和取消期限约束；这是显式资源边界。明细省略后无法重建从未观测到的连接分项，界面会保留不完整提示。修复不会重新分配旧版本已写错的历史配置统计。

固定 SDK 已在仓库缓存 `artifacts/tools/dotnet-10.0.403` 找到，后续构建及测试均使用此版本。Release 全解决方案构建零警告/错误，全解决方案格式检查通过。首批针对性回归 216 项通过；完整主程序测试发现 4 项旧 provider 路径期望与新预检冲突，其他 4951 项通过；修正测试职责并补充不安全路径预检后，最终针对性回归 482 项通过、零失败、零跳过。

产品提交 `5247188315addde0c84282d37dd852a8e196d537` 的[完整 CI 41989216542](https://github.com/Water-Run/ClashSharp/actions/runs/41989216542) 已通过，包括 Release 构建、格式检查、服务/watchdog 独立发布、离线安装包、四套测试和结果证据校验。下载的四份原始 TRX 均独立核对通过数和 SHA-256，合计 **7742 通过、0 失败、0 跳过**。

| 测试报告 | 通过数 | 原始 TRX SHA-256 |
| --- | --- | --- |
| 主程序 | 4958 | `89a1bad38553d0230ea1e9b8fe0d9e68670e7cc117aa67cc31c257f332d5809d` |
| Installer Core | 1141 | `85b175c20fdf832c1f79abc14ddb654848f1acd18b9558aaea404eac59913841` |
| Installer presentation | 240 | `69e63dc415306ef8528c956b67941eb93235c74eb63fecf5b64aa288ce594af6` |
| Installer Windows | 1403 | `fdc9e5c180a76e7c30d107b0735d24758a3cec6457af683a950213e4670e9655` |

这些证据覆盖源码和构建产物验证；本次没有对蓝机执行新的安装或交互验收。

## 审计基线结论与优先级

第一轮针对原始八项问题的代码和回归均已落实。A01/A02 的配置写入和权限检查改用原生观测，A03 将订阅网络准备移出共享 FIFO，A04 约束响应正文期限，A05 增加 DNS 监听诊断，A06 引入 IPC v4 明细完整性，A07 统一 URL 身份，A08 将网络状态收口到同一次锁内观测。复审没有发现足以断言这些原始修复整体失效的证据。

审计基线的第一轮修复并未覆盖所有消费端和资源上限，当时汇总为 1 项 P1、7 项 P2；后续处理见上方修复落实表：

| 编号 | 级别 | 状态 | 问题 |
| --- | --- | --- | --- |
| B02 | P1 | 调用链确认 | 失败或待处理的目标配置会接收实际运行配置的流量统计。 |
| B01 | P2 | 调用链确认 | 配置列表、主控和配置解析将目标配置显示为活动配置。 |
| B03 | P2 | 调用链确认 | 连接明细超限时连接页禁用关闭全部。 |
| C03 | P2 | 调用链确认 | 连接明细超限影响关闭连接触发器的恢复和验证。 |
| B04 | P2 | 边界确认 | 4 MiB 控制器响应上限在累计计数提取前触发。 |
| C01 | P2 | 边界确认 | 100,000 个 JSON 节点上限也在计数提取前触发。 |
| C02 | P2 | 调用链确认 | 明细不完整期间消失的连接不会进入节点、规则和连接数统计。 |
| B05 | P2 | 规则差异确认；具体样例待实测 | 除 `dns.listen` 外的服务禁止项缺少对应的 TUN 预检提示。 |

编号 B 保留[第二轮报告](2026-11-05-second-static-audit.md)的身份；C 为此次复审补充。B03 与 C03 共用同一明细读取契约，B04 与 C01 共用同一解析入口，修复时应合并设计和回归。

## 第一轮修改逐项审计

### A01 配置事务使用原生基线

**实现与证据。** [`GenerationProfileRuntime`](../../ClashSharp/ClashSharp/AppHost/Data/GenerationProfileRuntime.cs) 不再从 `Desired` 拼装模式、TUN 和端口；[`ExecuteObservedProfileMutationAsync`](../../ClashSharp/ClashSharp/Service/NetworkTakeoverService.ObservedProfiles.cs#L61) 在 `_transitionGate` 内核验原生配置和代理所有权，再把观测值传入配置事务。设置参与者对未包含在请求中的字段也使用独立观测。

**回归。** [`GenerationProfileRuntime_PreservesTheInstalledNetworkAndLivePermissionAfterAFailedSettingsBatch`](../../ClashSharp/ClashSharp.Tests/Unit/Services/NetworkSettingsRuntimeTests.cs#L21) 构造失败设置批次并刷新配置，检查原生模式、端口和原批次身份；另有显式配置激活用例。审计未发现原先“失败目标随订阅刷新生效”的相同写入路径重新开放。

**剩余边界。** 实际身份没有传递给所有读侧和统计写入，见 B01/B02。观测失败时相关配置变更会拒绝，恢复路径需保持清晰诊断。

### A02 活动配置源的权限与原生转换同步

**实现与证据。** [`ImportObservedProfileContentAsync`](../../ClashSharp/ClashSharp/Service/NetworkTakeoverService.ObservedProfiles.cs#L27) 在同一个原生转换锁内识别活动配置、检查额外权限并提交源文件；删除也在同一边界核对活动身份。MCP 的源哈希检查与目录变更仍位于其协调路径。

**回归。** 真实设置会话配合隔离原生端口覆盖目标和活动配置不一致、低权限编辑拒绝、所有权未知时保留源文件。目录测试也覆盖该权限判断。未发现原 A02 的低权限活动源覆盖路径。

**待补。** 现有测试以可控端口为主；应增加身份在排队期间变化、保存目录失败后补偿、代际替换/撤销并发的端到端测试，核对活动源和历史记录一致性。这里是建议验证，未确认新的权限绕过。

### A03 订阅下载与变更提交分离

**实现与证据。** [`ProfileCatalogMutationCoordinator`](../../ClashSharp/ClashSharp/Service/ProfileCatalogMutationCoordinator.cs#L57) 在普通准入内执行网络准备，再进入共享公平队列提交；[`ImportSubscriptionAsync`](../../ClashSharp/ClashSharp/Service/ProfileCatalogService.cs#L948) 提交前核对链接修订号、地址和自动更新资格。失败/取消状态回写仍持有准入，修改链接定义会结束旧的下载状态。

**回归。** 慢响应时编辑或删除链接、执行其他目录变更、释放响应后丢弃旧结果；另覆盖失败报告期间准入未释放。当前测试证据支持原先共享队列被慢下载占用的问题已处理。

**待补。** 增加下载完成后等待公平队列时的取消、代际关闭以及失败回调与链接修订交错的组合测试；继续核对每链接门锁的排队时间和取消边界。

### A04 更新检查正文期限

**实现与证据。** [`CheckCoreAsync`](../../ClashSharp/ClashSharp/Service/GitHubReleaseUpdateChecker.cs#L84) 使用关联的取消源，将响应头和有界正文读取纳入最多五秒期限；调用方取消继续向上传递，超时或网络错误保留已有缓存。

**回归。** 第一轮新增停滞正文、调用方取消和缓存/队列释放测试。静态检查未发现原先正文不受检查期限约束的路径。

### A05 TUN 的 DNS 监听诊断

**实现与证据。** [`EnsureTunDnsConfiguration`](../../ClashSharp/ClashSharp/Service/MihomoRuntimeConfigurationBuilder.cs#L270) 对 `dns.listen` 返回稳定诊断，设置参与者失败结果保留诊断代码，八种语言提供提示。普通系统代理配置仍保留该字段。

**回归与余项。** [`MihomoProfileImportCompatibilityTests`](../../ClashSharp/ClashSharp.Tests/Unit/Services/MihomoProfileImportCompatibilityTests.cs#L9) 同时覆盖普通模式保留和 TUN 拒绝。其他服务禁止字段仍缺少同等预检，见 B05。

### A06 IPC v4 区分累计计数与连接明细

**实现与证据。** [`MihomoServiceControllerBroker`](../../ClashSharp/ClashSharp.MihomoService/MihomoServiceControllerBroker.cs#L249) 对超过明细条数或 IPC 帧预算的响应标记 `ConnectionsComplete=false`，保留总数和累计字节；客户端、统计和实时速率消费该标记。统计在同一内核代际的不完整快照期间保留旧连接基线，新代际清除旧基线。协议固定为 v4，应用和服务需配套更新。

**回归。** 服务测试覆盖 2048/2049 条，协议测试覆盖序列化，SQLite 测试覆盖不完整快照前后的同一连接和新内核代际。它们证明有限样例中的计数保留及基线处理，并不覆盖所有大响应、连接生命周期、页面和触发器影响。

**剩余边界。** B03/B04/C01/C02/C03。尤其 2049 条测试的响应仍在原始字节和节点预算内，不能推广为任意大连接集合均可取得累计计数。

### A07 订阅 URL 身份

**实现与证据。** [`ProfileSubscriptionLink.HasSameUri`](../../ClashSharp/ClashSharp/Model/ProfileSubscriptionLink.cs#L39) 统一用于直接导入复用、目录新增和编辑比较，保留用户信息、路径与查询大小写，忽略片段；URI 规范化处理主机等部分。

**回归。** 测试覆盖主机大小写、查询令牌大小写及用户信息差异。未发现本轮所述三处比较规则再次分叉。实际服务器对 URL 的额外等价规则不由客户端推断。

### A08 原子网络快照与托盘通知

**实现与证据。** [`ObserveNetworkStateAsync`](../../ClashSharp/ClashSharp/Service/NetworkTakeoverService.NetworkSettings.cs#L65) 从锁内核验使用的代理观测生成快照；[`MainWindowComposition`](../../ClashSharp/ClashSharp/Presentation/Composition/MainWindowComposition.cs#L398) 监听设置生效证据变化并按网络身份过滤，卸载时解除事件订阅。

**回归。** 测试覆盖最后一次核验后外部代理状态变化及无关设置身份稳定性。该锁约束应用自身转换，不能阻止外部程序在返回快照之后更改 Windows 代理；调用方仍需依靠后续观测。托盘颜色的蓝机验收属于先前 `c531c43`，不是本次 `31d693a` 的实机验证。

## 汇总问题详解

### B02：流量写入未生效目标（P1）

[`AppDataGenerationRepositories`](../../ClashSharp/ClashSharp/AppHost/Data/AppDataGenerationRepositories.cs#L48) 将日志库的配置 ID 解析器绑定到 `GenerationRepositorySettings.ActiveProfileId`，后者读取 `Desired`。[`AppendTrafficSnapshot`](../../ClashSharp/ClashSharp/Service/LogStorageService.Traffic.cs#L21) 将其用于 `ProfileTrafficStats`。A 正在运行而 B 切换失败或待处理时，采样的 A 流量可以永久累计到 B；内核累计字节本身未因此改变。现有集成测试证明目标与观测值可以分离，但没有覆盖采样入库。

建议在采样、最终采样和配置切换之间建立可核验的配置身份与时间边界；身份不可确定时显式记录未知归属。测试 A → B 失败、同内核连续采样、切换前最终采样、恢复重试及重启后统计。

### B01：目标配置被标为活动配置（P2）

[`GetProfiles`](../../ClashSharp/ClashSharp/Service/ProfileCatalogService.cs#L263) 使用目标 ID 生成 `IsActive`；主控使用同一目标 ID 查名称、节点和规则数，并在它与应用计划不同时将内核所有权标为未知。代理节点和规则的活动配置解析也读取目标。A 实际运行而 B 是失败目标时，这些页面可能展示 B。设置页保留用户目标是不同的语义，活动视图需标明观测值或明确展示未知状态。

证据：[主控采集](../../ClashSharp/ClashSharp/Presentation/Adapters/MasterControlRuntimeSnapshotSource.cs#L71) · [主控所有权](../../ClashSharp/ClashSharp/Presentation/Adapters/MasterControlRuntimeSnapshotWork.cs#L107) · [配置解析源](../../ClashSharp/ClashSharp/Service/MihomoProfileParserServiceFactory.cs#L30)。

### B03：连接页不能在明细超限时关闭全部（P2）

服务返回不完整快照后，客户端 [`MapConnections`](../../ClashSharp/ClashSharp/Service/MihomoControllerClient.cs#L1025) 抛出 `InvalidDataException`；页面将其当作不可用并清空列表，[`CanCloseConnections`](../../ClashSharp/ClashSharp/ViewModel/ConnectionsViewModel.cs#L183) 因此为 false，按钮禁用。服务的关闭全部为独立命令。应区分控制器可用、连接总数和明细完整性，允许在可信总数非零时执行关闭全部，并对逐条操作保留严格限制。

### C03：触发器恢复依赖完整明细（P2）

[`TriggerActionRuntimeAdapter`](../../ClashSharp/ClashSharp/Service/TriggerActionRuntimeAdapter.cs#L67) 用活动连接列表判断关闭动作是否完成。超限会使探测返回未知；已经处于 `Running` 的动作在恢复时被 [`TriggerActionExecutor`](../../ClashSharp/ClashSharp.Application/Triggers/TriggerActionExecutor.cs#L175) 标为不确定，后续动作停止。正常待执行动作即使初次探测未知仍会尝试执行；如果执行后的验证仍超限，也可能变为不确定。回归需区分初次执行、执行后验证与中断恢复，不应假定所有场景都没有发送关闭命令。

### B04 与 C01：解析前预算阻断累计计数（P2）

`/connections` 先受 [`4 MiB 原始响应限制`](../../ClashSharp/ClashSharp.MihomoService/MihomoServiceControllerBroker.cs#L259)，再经 [`100,000 个 JSON 节点限制`](../../ClashSharp/ClashSharp.MihomoService/MihomoServiceControllerBroker.cs#L682)，两者均早于累计计数读取。合成 JSON 实验生成了 320,638 字节、106,552 个节点的响应结构，说明节点限制可先于字节限制命中；该实验未执行生产 broker，也不证明真实 Mihomo 经常产生此结构。

安全预算应保留。设计上需考虑受限流式提取或独立计数来源，并继续验证结构、重复键、身份与资源预算。分别测试超过字节预算、超过节点预算、恰好在阈值内、取消及非法响应，明确无法取得可信计数时的用户反馈。

### C02：不完整明细期间的统计缺口（P2）

[`AppendTrafficSnapshot`](../../ClashSharp/ClashSharp/Service/LogStorageService.Traffic.cs#L36) 在不完整快照中只推进累计总量，并保留旧连接基线。一条在两次完整快照之间、且整个生命周期均落在不完整期间的连接，不会进入连接计数、规则命中和节点流量表。即使普通周期采样也不保证捕获每条短连接，此处额外缺口是即便采样发生也没有明细可供记录。现有测试只覆盖降级前后仍存活的同一连接。

建议为统计结果携带采样完整性或缺口信息，避免将这些分项展示为完整统计，并评估可用的数据源。测试完整 → 不完整 → 完整期间连接创建、结束和跨越边界的组合。

### B05：其他 TUN 不兼容项缺少具体提示（P2）

应用覆盖器保留非托管字段；服务 [`MihomoServiceConfigurationTrustValidator`](../../ClashSharp/ClashSharp.MihomoService/MihomoServiceConfigurationTrustValidator.cs#L55) 禁止 `geox-url` 等字段。应用侧目前只对 `dns.listen` 给出专门提前诊断，服务在激活时独立验证并可返回通用不可信配置错误。规则差异已确认；该具体样例能否完整经过真实普通导入和内核运行，仍需实测。

建议对实际生成的 TUN 候选提供版本一致的只读兼容性诊断，服务保留独立信任校验；测试普通模式、TUN、来源资源缺失和错误消息本地化。

## 审计阶段的验证记录与限制

- 前一次精确产品提交的 [完整 CI 41982201532](https://github.com/Water-Run/ClashSharp/actions/runs/41982201532) 通过，四套 TRX 合计 7728／0／0；这是既有测试证据，不证明上述新增场景通过。
- 本次复审逐项阅读产品差异、关键生产调用链及新增测试，并对 JSON 字节数和节点数做隔离合成计算。该计算不执行产品 broker。
- 上一轮复审尝试运行相关测试时，仓库固定 SDK 为 `10.0.403`，本机只发现 `10.0.201`、`10.0.400`、`10.0.401`，测试命令未启动。本次没有重新构建、运行新测试或对蓝机部署。
- 汇总结论是有范围的代码审计，不等于全部并发交错、真实网络负载、安装恢复或 GUI 场景已实测。未发现新的问题也不构成无缺陷保证。

## 建议修复和验收顺序

1. 统一已观测配置身份及采样边界，先处理 B02/B01，并覆盖失败批次、切换前末次采样和身份未知。
2. 统一连接响应的累计计数、明细完整性及命令能力契约，合并处理 B03/B04/C01/C02/C03，保留有界解析和鉴权。
3. 用真实内核配置验证 B05，建立与服务策略一致的可操作诊断，保留服务侧独立拒绝。
4. 对修复后的同一产品提交运行相关故障注入与完整 CI，再按既有验收计划构建、签名，并在蓝机验证配置失败、TUN 高连接数、统计及托盘状态。
