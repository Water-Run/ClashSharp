# ClashSharp 第二轮静态功能审查

审查后的源码处理及 B01–B05/C01–C03 验证进展统一记录在[汇总审计修复](2026-11-05-consolidated-fix-audit.md)。下文保留审查基线的发现。

审查基线：`102f8cd`，其中产品源码为 `31d693a`。本轮复核第一轮 A01–A08 的修复边界，并沿设置权威、原生配置、配置目录、连接与流量、服务 IPC、订阅、MCP、触发器、启动恢复和安装发布入口检查读写语义。结论依据生产调用链和现有测试代码；本轮没有修改产品源码，也没有声称完成蓝机运行验收。此前对应产品提交的完整 CI 为 [41982201532](https://github.com/Water-Run/ClashSharp/actions/runs/41982201532)，四套 TRX 共 7728 项通过、零失败、零跳过。

## 结论

第一轮的原生写入、订阅提交排序、版本下载期限、DNS 监听诊断、IPC v4 计数降级、URL 身份和托盘观测修复仍可从当前调用链追溯。第二轮发现五个剩余问题：失败或待处理的配置目标仍被部分读侧当成活动配置；连接明细降级没有传到连接页的关闭操作；原始控制器响应过大时仍会丢失计数；TUN 兼容性诊断只覆盖 DNS 监听一种服务拒绝原因。

| 编号 | 级别 | 发现 | 证据状态 |
| --- | --- | --- | --- |
| B01 | P2 | 配置列表、主控概览和配置解析仍把 `Desired` 配置标为活动配置 | 生产调用链确认，已有测试证实目标与观测可分离 |
| B02 | P1 | 连接与流量采样可能把实际配置 A 的流量永久记到未生效的目标 B | 生产调用链确认，缺跨组件回归 |
| B03 | P2 | TUN 明细超限时，连接页禁用“关闭全部”，尽管服务仍支持该命令 | 生产调用链确认，缺超限页面回归 |
| B04 | P2 | `/connections` 原始响应超过 4 MiB 时，IPC v4 的累计计数降级无法执行 | 传输边界和解析顺序确认，缺大响应回归 |
| B05 | P2 | TUN 提前诊断仅识别 `dns.listen`，其他服务禁止字段仍在激活时才失败 | 两层验证器差异确认，需针对具体配置补端到端回归 |

### B01：读侧仍将目标配置显示为活动配置

设置权威在网络批次失败或待处理时保留 `Desired`，而原生配置仍可以是 A。现有集成测试直接构造了目标 `pending-profile`、观测配置 `builtin-direct` 同时存在的状态。第一轮修复让目录写入调用 [`ReadObservedActiveProfileIdAsync`](../../ClashSharp/ClashSharp/Service/ProfileCatalogService.cs#L2253)，但 [`GetProfiles`](../../ClashSharp/ClashSharp/Service/ProfileCatalogService.cs#L263) 的 `IsActive` 仍取 `GenerationRepositorySettings.ActiveProfileId`，即 `Desired`。配置页通过这些行显示活动标签，主控通过 [`settings.ActiveProfileId`](../../ClashSharp/ClashSharp/Presentation/Composition/MasterControlPageComposition.cs#L64) 查配置名称、节点与规则数；[`MasterControlRuntimeSnapshotWork`](../../ClashSharp/ClashSharp/Presentation/Adapters/MasterControlRuntimeSnapshotWork.cs#L107) 还会因该 ID 与实际应用计划不同而把内核所有权显示为未知。配置解析文本源也读取目标 ID。

复现条件是 A 正在运行，切换 B 失败或保留待处理，随后打开配置页、主控或代理解析视图。页面可能标 B 活动、显示 B 的节点与规则数，同时内核仍使用 A。设置页展示用户期望值本身合理；这些带有“活动”“运行”含义的读侧应使用同一次原生观测或明确区分目标与生效值。回归需要覆盖失败批次、待处理批次和恢复后状态。证据：[目标与观测分离测试](../../ClashSharp/ClashSharp.Tests/Integration/GenerationSettingsAuthority.ActionTests.cs#L145) · [配置列表](../../ClashSharp/ClashSharp/Service/ProfileCatalogService.cs#L269) · [配置页绑定](../../ClashSharp/ClashSharp/Presentation/Composition/ProfilesPageComposition.cs#L15) · [解析文本源](../../ClashSharp/ClashSharp/Service/MihomoProfileParserServiceFactory.cs#L29)。

### B02：流量归属采用未核验的配置目标

代际仓库将日志存储的配置解析器绑定到 [`settings.ActiveProfileId`](../../ClashSharp/ClashSharp/AppHost/Data/AppDataGenerationRepositories.cs#L48)，该属性读 `session.Snapshot.Desired`。采样器获取经过内核代际绑定的流量快照后，[`AppendTrafficSnapshot`](../../ClashSharp/ClashSharp/Service/LogStorageService.Traffic.cs#L12) 在入库前另取该 ID，并将增量写入 `ProfileTrafficStats`。旧的 `AppendConnectionSnapshot` 也走相同解析器。实际配置 A 运行、目标 B 失败或待处理时，A 的字节数与新连接数会累计到 B；后续恢复不会重新分配历史统计。这是持久化数据错误，故高于 B01。

修复需要让采样快照携带与其内核代际一致的已观测配置 ID，或者在同一原生事务边界解析 ID；不能在采样和入库间单独读取当前目标。回归要在 A 运行时留下 B 的失败批次、采样非零流量，再核对 A/B 统计行，并覆盖切换前末次采样。证据：[目标读法](../../ClashSharp/ClashSharp/AppHost/Data/GenerationRepositorySettings.cs#L17) · [采样入口](../../ClashSharp/ClashSharp/Service/ConnectionSamplingService.cs#L168) · [统计写入](../../ClashSharp/ClashSharp/Service/LogStorageService.Traffic.cs#L80) · [持久表查询](../../ClashSharp/ClashSharp/Service/LogStorageService.cs#L155)。

### B03：明细降级同时关闭了连接页的恢复操作

服务代理在超过 2048 条连接时返回 `ConnectionsComplete=false`、空明细和真实 `TotalConnectionCount`，累计计数仍有效。应用读取活动连接明细时，[`MapConnections`](../../ClashSharp/ClashSharp/Service/MihomoControllerClient.cs#L1025) 对不完整快照抛 `InvalidDataException`。连接页将该异常视为读取失败，清空行并设 `_isAvailable=false`；[`CanCloseConnections`](../../ClashSharp/ClashSharp/ViewModel/ConnectionsViewModel.cs#L183) 因此为 false，XAML 禁用“关闭全部”。服务端的 [`CloseAllConnections`](../../ClashSharp/ClashSharp.MihomoService/MihomoServiceControllerBroker.cs#L333) 是独立的 `DELETE /connections`，没有明细条数前置条件。

超过阈值后，用户不能从连接页执行最直接的降载操作，且看到的是“不可用”而非“明细过多”。应向页面传递“控制器可用、总数已知、明细不完整”的状态，并只禁用逐条操作；关闭全部应由真实连接数和命令能力决定。回归可复用服务端现有的 2048/2049 边界用例，增加页面层测试。证据：[服务边界测试](../../ClashSharp/ClashSharp.Tests/Unit/MihomoService/MihomoServiceControllerBrokerTests.cs#L10) · [页面异常处理](../../ClashSharp/ClashSharp/ViewModel/ConnectionsViewModel.cs#L310) · [按钮绑定](../../ClashSharp/ClashSharp/View/Connections.xaml#L37)。

### B04：原始响应上限先于计数提取

IPC v4 对“连接数超过 2048”或编码帧过大做明细降级，但 [`GetConnectionsAsync`](../../ClashSharp/ClashSharp.MihomoService/MihomoServiceControllerBroker.cs#L249) 首先以 4 MiB 上限读取整个 `/connections` 响应。命名管道 HTTP 传输在 `Content-Length` 或实际读入字节超过该上限时抛异常，代理尚未解析 `uploadTotal`、`downloadTotal`，所以实时计数和统计采样一并失去本次样本。这一问题不要求连接数超过 2048；少量但字段很长的行也可能触发。

上限本身是合理的资源保护。需要在有界读取内单独取得可信累计计数，或为超限提供独立计数查询路径，继续拒绝无界明细。增加超过 4 MiB 的合成控制器响应测试，核对内存边界、失败代码及计数采样结果。证据：[响应上限](../../ClashSharp/ClashSharp.MihomoService/MihomoServiceControllerBroker.cs#L259) · [传输提前拒绝](../../ClashSharp/ClashSharp.MihomoService/MihomoControllerRuntime.cs#L339) · [计数解析顺序](../../ClashSharp/ClashSharp.MihomoService/MihomoServiceControllerBroker.cs#L289)。目前是静态边界结论，未测量真实订阅环境达到该大小的频率。

### B05：TUN 兼容性提示仍只覆盖一种禁止项

第一轮在应用侧生成 TUN 配置时提前拒绝 `dns.listen` 并给出稳定代码。与此同时，配置覆盖器会保留非应用托管的源字段，例如 `geox-url`；服务端信任验证器明确禁止该字段以及若干 LocalSystem 风险入口。普通导入和应用侧 `mihomo -t` 不调用服务信任验证器，服务子进程在激活阶段才检查，失败统一归到 `service.child.configuration_untrusted`。服务策略应保留；缺口是用户在导入和启用 TUN 前没有与服务实际规则一致的兼容性诊断。

建议暴露与服务信任策略同版本的只读预检，验证实际覆盖后的候选配置，给出字段级提示，并在服务侧继续独立复核。至少增加含 `geox-url` 的配置在普通导入、应用内核和 TUN 激活路径的回归。证据：[覆盖器保留规则](../../ClashSharp/ClashSharp/Service/MihomoRuntimeConfigurationBuilder.cs#L93) · [仅 DNS 提前检查](../../ClashSharp/ClashSharp/Service/MihomoRuntimeConfigurationBuilder.cs#L270) · [服务禁止字段](../../ClashSharp/ClashSharp.MihomoService/MihomoServiceConfigurationTrustValidator.cs#L55) · [服务激活时检查](../../ClashSharp/ClashSharp.MihomoService/MihomoChildSupervisor.cs#L581)。本轮未对该合成配置重新运行内核 `-t`，因此“普通内核接受该具体配置”仍需测试确定。

## 功能边界复核

| 领域 | 本轮追踪路径 | 结果 |
| --- | --- | --- |
| 启动与外观 | 启动步骤顺序、设置批次参与者、窗口创建 | 第一轮主题初始化顺序和设置批次责任未见新的静态异常；未做 GUI 复现。 |
| 原生网络与配置 | 设置权威 → 网络参与者 → 转换锁 → 运行时配置及回读 | A01/A02 的写侧修复保留；读侧 B01/B02 仍越过观测身份。 |
| 配置、订阅与 MCP | 目录准入、慢下载准备、修订号重查、MCP 活动配置授权 | A03/A07 的提交与 URI 身份修复保留；没有证据表明旧的低权限写入路径重开。 |
| 连接与统计 | 服务代理 → IPC v4 → 控制器客户端 → 页面与 SQLite | 计数/明细拆分有效；B02/B03/B04 分别影响归属、页面操作和原始响应边界。 |
| TUN 信任边界 | 应用配置覆盖、内核校验、服务验证、激活诊断 | DNS 监听有早期提示；B05 是其余服务规则的可用性缺口。 |
| 托盘与主控 | 单锁网络快照、设置状态事件、主控汇总 | A08 的快照与事件路径保留；主控配置汇总受 B01 影响。 |
| 数据代际与恢复 | 独占准入、代际切换、退出流量末次采样 | 未发现可由现有静态证据确认的新提交或恢复缺口；B02 需在切换前末次采样验证。 |
| 触发器、安装器与发布 | outbox 探测/应用、安装事务、发布签名约束 | 对核心入口及协议版本做交叉检查，未发现可独立确认的新问题；未运行签名或部署。 |

## 验证界限与建议顺序

本轮是静态审查，没有新增产品代码、测试或构建。现有 CI 证明当前提交的既有测试通过，但不覆盖上述跨组件失败场景。建议先处理 B02 与 B01 的共同配置身份来源，再处理 B03/B04 的连接降级契约，最后统一 B05 的应用与服务预检。完成后应以同一新提交运行完整 CI，再做真实 TUN、失败批次和蓝机验收。
