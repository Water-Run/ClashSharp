# ClashSharp 发布级静态审查与功能逻辑覆盖

R01–R09 的后续源码处理与最终本地验证见[问题修复记录](2026-11-05-release-static-fixes.md)。下文保留修复前基线的发现，不代表修复后工作区仍有这九项开放问题。

审查日期为 2026-11-05，基线为 `d4dd30df74a65804160154216669ba77206aafaa`，其中最近的产品源码提交为 `5247188315addde0c84282d37dd852a8e196d537`。本轮发现 **2 项 P1、7 项 P2**。建议先处理外部修改恢复依据丢失和 TUN 跨数据代际读取旧资源，再进行发布签收。Release 构建和已有测试不能排除这些跨组件缺陷。

本轮没有修改产品源码，没有提交或推送，没有启动实际产品、修改真实代理环境、安装证书或部署服务。原有未提交文件 `docs/handoff-2026-11-05.md` 保留。新增内容为本报告及被 Git 忽略的本地验证产物。

## 审查范围与证据口径

解决方案包含 19 个项目：11 个生产项目、4 个测试项目、4 个隔离探针项目。11 个生产项目的 C# 与 XAML 共 1,140 个文件、169,470 行。另对受版本管理的脚本、项目文件、工作流、清单和锁文件做清点，共 1,786 个代码与构建配置文件、336,118 行，包含测试和锁文件。

覆盖方式是全仓源文件清点、构建分析器、格式与脚本检查，以及从用户功能入口追踪到数据所有者、权限检查、外部副作用、持久化和恢复路径。逐功能调用链审查不等于对每一行完成形式化证明；没有把文件清单或测试通过数当成人工逐行审阅的替代证据。

证据分为三类：

- **隔离复现**：执行当前源码编译出的生产逻辑或测试程序集内直接链接的同一生产源码，数据和外部能力替身均位于临时验证目录。
- **调用链确认**：生产代码的条件与调用顺序可以确定结果，尚未执行完整的 Windows 场景。
- **外部语义核对**：操作系统或内核行为以官方资料核对，仍区分资料结论和本机实测。

七项发现具有隔离执行证据。其中 R05 分别验证了配置事务内的身份观测与 SQLite 归属结果，没有把它写成一次完整的真实网络切换实验。R02 使用关闭 ACL 修改的生产 materializer，验证路径选择；没有运行提权服务。

## 发现清单

| 编号 | 级别 | 发现 | 主要代码位置 | 证据 |
| --- | --- | --- | --- | --- |
| R01 | P1 | 清除全部数据删除原生修正日志，未先恢复环境变量等外部修改 | [清除工厂](../../ClashSharp/ClashSharp/AppHost/Data/GenerationDataClearOperationFactory.cs#L29) | 隔离复现 |
| R02 | P1 | TUN 本地 provider 的资源根没有跟随数据代际，可能使用旧规则或旧节点 | [服务资源根绑定](../../ClashSharp/ClashSharp.MihomoService/MihomoEffectiveConfigurationMaterializer.cs#L82) | 隔离复现与生产组成链 |
| R03 | P2 | 代际快照保留配置，却遗漏配置引用的部分本地 provider 文件 | [快照目录白名单](../../ClashSharp/ClashSharp.Infrastructure/Data/UserDataRepositorySnapshot.cs#L17) | 隔离复现 |
| R04 | P2 | 规则命中统计写入键与规则页面查询键不同 | [规则查询键](../../ClashSharp/ClashSharp/Service/RuleCatalogService.cs#L108) | 真实 SQLite 隔离复现 |
| R05 | P2 | 正常切换前的末次流量样本也会被归为未知配置 | [样本身份获取](../../ClashSharp/ClashSharp/Service/ConnectionSamplingServiceFactory.cs#L49) | 事务观测与 SQLite 组合证据 |
| R06 | P2 | AND、OR、NOT、SUB-RULE 等规则的预览字段被普通逗号拆分破坏 | [语义预览解析](../../ClashSharp/ClashSharp/Service/MihomoProfilePreviewParser.Semantic.cs#L59) | 隔离复现及官方语法核对 |
| R07 | P2 | 原生修正只使用目标端口，不核对实际 HTTP 代理监听器 | [诊断端口适配](../../ClashSharp/ClashSharp/Service/WindowsNetworkDiagnosticServiceFactory.cs#L37) | 调用链确认 |
| R08 | P2 | WSL 修正未识别 NAT 网络，可能把不可用的 localhost 代理报告为健康 | [WSL 健康判定](../../ClashSharp/ClashSharp/Service/WindowsNetworkDiagnosticService.cs#L171) | 调用链与微软文档 |
| R09 | P2 | 普通模式大连接响应没有与 TUN 一致的降级契约，页面会失去关闭全部能力 | [WebSocket 大小限制](../../ClashSharp/ClashSharp/Service/MihomoControllerClient.cs#L627) | 大响应隔离复现 |

P1 表示应优先修复的发布阻断候选；P2 表示有明确触发条件和用户影响的正确性或可用性问题。没有依据将这些问题描述为已经证实的远程代码执行或提权漏洞。

## R01 清除全部数据丢失原生修改的恢复依据

用户先在 Windows 原生设置中应用终端或 WSL 修正，再执行清除全部数据并重启。修正已经改变用户级 `HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY`、`NO_PROXY`，WSL 还会改变 `WSLENV`。原值仅保存在应用根下的 `WindowsNetworkMutationJournal.json`。

生产清除工厂只在关停后清理控制器凭据和旧设置，没有调用原生诊断服务的恢复入口。随后 `ApplicationDataClearOperation` 删除数据根内的所有普通文件，包括这份恢复日志。日志不存在时，诊断存储返回空状态；之后点击撤销也无法找回原值。[清除工厂](../../ClashSharp/ClashSharp/AppHost/Data/GenerationDataClearOperationFactory.cs#L29) · [文件删除](../../ClashSharp/ClashSharp/Service/ApplicationDataClearOperation.cs#L74) · [日志缺失视为空状态](../../ClashSharp/ClashSharp/Service/WindowsDiagnosticMutationJournal.cs#L135)

隔离探针使用生产文件日志、生产诊断服务、内存环境变量端口和生产清除类，结果为：

```text
清除前 HTTP_PROXY 原值 = http://corporate.example:8080
应用修正后             = http://127.0.0.1:19090
清除前恢复日志存在     = true
清除后恢复日志存在     = false
清除后再次撤销的值     = http://127.0.0.1:19090
```

影响包括新终端继续指向已停止的本地代理，以及原有企业代理值无法由产品恢复。Microsoft Store 的撤销依据也在同一日志内，因此存在相同的依据丢失；本轮没有修改真实 Store 豁免来复现这一分支。

修复应在删除恢复日志之前，按所有权规则恢复所有仍归 ClashSharp 所有的外部修改并独立回读。恢复失败时保留日志与数据清除义务；外部程序已经改写的值继续保留。诊断写入也应参与清除时的准入与排空，防止日志在检查和删除之间重新生成。

验收至少覆盖终端和 WSL 共同拥有变量、原企业代理、原值不存在、外部改写、恢复 API 失败，以及清除期间仍有诊断命令运行。最终检查外部状态和日志生命周期，不能只断言目录为空。

## R02 TUN 服务跨代际读取本地 provider 资源

安装器写入的固定服务参数仍指向包数据根下的 `LocalState/mihomo/config.yaml`。应用实际配置已经位于 `Data/v1/generations/<id>/mihomo`，服务的 `ResolveSourceConfiguration` 也会根据请求的 `DataGenerationId` 查找这一新位置。

但服务 materializer 在组成时将 `_sourceAssetRoot` 固定为 `Path.GetDirectoryName(options.ConfigPath)`。复制 `type: file` 的 provider 时优先使用这条固定旧路径，没有按 staged generation 的 `DataGenerationId` 解析资源根。因此配置文件来自新代际，provider 内容却可能来自旧目录。[安装器固定路径](../../ClashSharp/ClashSharp.Installer.Windows/Machines/WindowsMachineDeploymentPlan.cs#L169) · [配置源按代际解析](../../ClashSharp/ClashSharp.MihomoService/MihomoGenerationStore.cs#L149) · [资源根固定](../../ClashSharp/ClashSharp.MihomoService/MihomoEffectiveConfigurationMaterializer.cs#L82) · [资源读取](../../ClashSharp/ClashSharp.MihomoService/MihomoRuntimeAssetStager.cs#L69)

探针同时放置两个 `rules/local.yaml`：旧根为 `payload: [old.example]`，新代际为 `payload: [new.example]`。staged 配置携带新代际 ID，生产 materializer 实际复制的是 `old.example`。探针使用与生产组成相同的固定资源根选择，关闭 ACL 修改，仅验证复制和配置生成。

旧文件不存在时，TUN 激活会因资源缺失被拒绝；旧文件存在且仍是有效 provider 时，新的配置身份和哈希并不能证明运行的是新代际的规则或节点。该错误会影响恢复、导入和代际切换后的实际路由。

修复应让配置源与本地资源源共享同一个经验证的数据代际描述符。保留服务侧独立路径校验及私有目标目录，不能让客户端直接提供任意资源根。完整回归需要同时存在不同内容的旧根与新根，并检查最终复制字节、数据代际 ID 和真实内核可见内容。

## R03 代际快照遗漏本地 provider 依赖

`UserDataRepositorySnapshot` 只复制固定文档、数据库，以及 `mihomo/profiles` 和 `mihomo/history` 两个目录。合法配置可以引用 `mihomo/rules/local.yaml` 或其他安全相对路径；这些文件没有进入快照白名单。[复制白名单](../../ClashSharp/ClashSharp.Infrastructure/Data/UserDataRepositorySnapshot.cs#L11) · [目录复制循环](../../ClashSharp/ClashSharp.Infrastructure/Data/UserDataRepositorySnapshot.cs#L68)

首次迁移和全量设置重置都复用此快照。全量重置使用空文件导入计划，没有其他阶段补回这些文件。[首次迁移](../../ClashSharp/ClashSharp.Infrastructure/Data/LegacyDataGenerationPreparer.cs) · [候选准备](../../ClashSharp/ClashSharp/AppHost/Data/GenerationDataCandidatePreparer.cs#L106)

隔离结果为配置文件已复制、被引用资源未复制、原始资源仍存在。因而这是候选内容不完整，不应描述为本轮已经删除原文件。活动配置需要该资源时，候选验证可能失败并导致重置回滚；保留但未激活的配置则可能在以后激活时失败。若资源恰好位于已复制的 profile 子目录内，不受此目录遗漏影响。

修复应明确可迁移的本地资源集合，携带全部被保留配置所需的文件，同时排除运行态、凭据和临时事务文件。回归必须包含自定义目录、文件型代理 provider、文件型规则 provider、缺失资源和重解析点，并检查新代际能独立使用资源。R02 的服务根修复与本项应分别验收，修复其中一项不能关闭另一项。

## R04 规则命中统计无法回到对应规则行

采样入库以 `ActiveConnection.RawRuleDisplay` 为键，即规则类型和载荷；规则页以 `RuleType,Payload,Action` 查计数。两侧键的字段数量已经不同，实际 API 的类型名称还可能采用不同的规范形式。[采样写入](../../ClashSharp/ClashSharp/Service/LogStorageService.Traffic.cs#L69) · [运行时规则文本](../../ClashSharp/ClashSharp.Core/Domain/Connections/ActiveConnection.cs#L32) · [页面查询](../../ClashSharp/ClashSharp/Service/RuleCatalogService.cs#L95)

真实 SQLite 探针故意让两侧规则类型完全相同，以排除大小写干扰：

```text
运行时写入键 = DOMAIN-SUFFIX,example.com        计数 1
页面查询键   = DOMAIN-SUFFIX,example.com,PROXY  计数 0
```

因此即使已经采到命中，规则页仍会读到初始化的零值。现有规则目录测试直接构造带 Action 的字典，日志测试则只检查 RawRuleDisplay，各自通过也无法验证二者能连接起来。

修复应建立共享的规范化规则身份，处理 API 类型名、MATCH、复合规则及配置版本。不能简单把整条代理链追加为 Action。新增测试应从运行时连接样本写入真实 SQLite，再经真实规则目录读取最终规则行，并覆盖配置切换后的统计隔离。

## R05 切换前末次流量丢失已知配置归属

流量源现在在控制器读取前后调用 `ObserveRuntimeConfigurationIntegrity`，只有两次观测一致且已知时才标记配置身份。这个规则可以避免把不确定区间的流量分给错误目标。

正常切换的末次采样却发生在 `CoreConfigurationService.ApplyRuntimeConfigurationAsync` 已经持有 `_runtimeConfigurationGate`、并调用 `runtime.ApplyAsync` 之后。`NetworkTakeoverService` 在停止旧内核之前调用 flush。此时公共完整性观测的 `Wait(0)` 必然失败，所以末次样本也被标成 `unknown`。[事务持锁与 Apply](../../ClashSharp/ClashSharp/Service/CoreConfigurationService.RuntimeTransaction.cs#L248) · [持锁时返回未知](../../ClashSharp/ClashSharp/Service/CoreConfigurationService.RuntimeTransaction.cs#L136) · [停止前 flush](../../ClashSharp/ClashSharp/Service/NetworkTakeoverService.cs#L275) · [样本身份](../../ClashSharp/ClashSharp/Service/ConnectionSamplingServiceFactory.cs#L49)

隔离执行生产配置事务得到 `Before=true, InsideApply=false, After=true`。再执行生产 SQLite 归属逻辑，配置 A 的累计上传从 10 增到 30、末次样本身份为 unknown 时，结果为 A=10、unknown=20。两段证据与上述生产调用顺序共同确认问题；没有运行真实代理切换。

影响是总流量仍保留，正常切换前的最后一段流量却不能归入原配置。若同一内核代际继续使用，下一次身份从 unknown 回到已知时还会触发一次保守的区间归属。

修复应在已持有转换所有权且旧运行态仍可核验时完成末次采样，或由转换所有者向采样传递经过核验的旧配置身份。公共观测在事务进行中返回未知的保护应保留。验收需要同时使用真实配置事务、flush 回调和 SQLite，并断言成功切换、失败补偿和关闭时的归属及总量。

## R06 复杂规则预览误解析动作与载荷

语义 YAML 解析之后仍对每条规则直接 `Split(',')`，把第三段当成动作。逻辑规则的括号内本身包含逗号，SUB-RULE 的条件也一样。保留的旧扫描回退分支采用相同的拆分方式。[语义分支](../../ClashSharp/ClashSharp/Service/MihomoProfilePreviewParser.Semantic.cs#L59) · [扫描回退](../../ClashSharp/ClashSharp/Service/MihomoProfilePreviewParser.cs#L111)

隔离结果如下：

| 输入 | 正确动作 | 当前显示动作 |
| --- | --- | --- |
| `AND,((NETWORK,UDP),(DST-PORT,443)),REJECT` | `REJECT` | `UDP)` |
| `SUB-RULE,(NETWORK,TCP),sub1` | `sub1` | `TCP)` |

括号与内部逗号是内核支持的语法，不应当作损坏配置。官方文档分别说明了逻辑规则和子规则。[Mihomo 路由规则](https://wiki.metacubex.one/en/config/rules/)

本项影响预览、筛选和用户理解，不表示原 YAML 已被改写，也不表示实际内核执行了界面显示的错误动作。修复需要按顶层分隔符解析，识别括号层次和不同规则类型的参数结构；复杂正则载荷和附加参数也应纳入用例。对无法可靠解析的扩展语法展示原始规则，比制造一个错误动作更准确。

## R07 原生修正可能写入没有监听器的代理地址

`WindowsDiagnosticSettingsAdapter.MixedPort` 直接读取 `AppSettingsService.MixedPort`，后者绑定的是设置的 `Desired`。诊断和应用修正只据此生成 localhost HTTP 代理地址，没有独立核对当前运行模式、实际端口或监听器是否存在。[端口来源](../../ClashSharp/ClashSharp/Service/WindowsNetworkDiagnosticServiceFactory.cs#L37) · [Desired 投影](../../ClashSharp/ClashSharp/Service/AppSettingsService.Authority.cs#L94) · [环境变量生成](../../ClashSharp/ClashSharp/Service/WindowsNetworkDiagnosticService.cs#L379)

至少有两条可达路径：

1. 端口 A 正在工作，修改为 B 失败但目标值保留；应用终端或 WSL 修正后，环境变量被写成未生效的 B。
2. 已启用 TUN，应用内核停止，服务 materializer 将 `mixed-port` 强制设为 `0`；修正仍写入设置中的混合端口，该 HTTP 代理监听器实际上不存在。[TUN 关闭应用内核](../../ClashSharp/ClashSharp/Service/NetworkTakeoverService.cs#L472) · [服务禁止混合监听](../../ClashSharp/ClashSharp.MihomoService/MihomoEffectiveConfigurationMaterializer.cs#L129)

随后健康检查只比较环境变量是否等于这个目标地址，可以把同一个错误写入再报告为健康。Windows 设置步骤已完成，不等于终端具备可用代理。

修复应根据已核验的运行模式和实际监听器决定是否需要环境代理。对 TUN、内核停止、端口应用失败和所有权未知返回明确的可操作结果，不能无条件覆盖环境变量。新增测试应在真实设置失败批次或 TUN 状态下调用修正入口，断言没有写入不可用端点。

## R08 WSL NAT 模式下的健康判断失真

WSL 修正写入 `http://127.0.0.1:<port>` 并增加 WSLENV 桥接标记。诊断只检查 `wsl.exe --status` 成功、用户环境变量和桥接标记，不识别发行版的网络模式，也不从 Linux 侧检查到代理的连通性。

微软文档说明，在 WSL2 NAT 下从 Linux 访问 Windows 服务应使用宿主 IP；mirrored 模式才提供此方向的 localhost 连通能力。应用生成的监听器又固定为 `127.0.0.1`，所以简单替换成宿主 IP 也不能让当前监听器在 NAT 网络中可达。[微软 WSL 网络说明](https://learn.microsoft.com/en-us/windows/wsl/networking) · [WSL 判定](../../ClashSharp/ClashSharp/Service/WindowsNetworkDiagnosticService.cs#L171) · [应用监听器](../../ClashSharp/ClashSharp/Service/MihomoRuntimeConfigurationBuilder.cs#L32)

本项针对 WSL2 NAT 和普通系统代理路径；不推广到 WSL1、mirrored 或另有透明接管的环境。本轮没有启动真实 WSL 发行版。源码已经足以确认，现有健康判定没有包含决定连通性的网络模式条件。

修复应区分 WSL1、mirrored、NAT 和不可确定状态。对未支持的模式给出明确提示，或使用有独立设计和验证的宿主桥接能力。配置检查成功应与实际连通性成功分开展示。验收需要覆盖 NAT 和 mirrored 两种真实发行版环境。

## R09 普通模式的大连接响应会禁用关闭全部

上一轮已为 TUN 服务实现有界扫描和明细省略，但应用内核的连接 WebSocket 仍在 4 MiB 直接抛出普通 `InvalidDataException`。连接页把该异常视为不可用，清空列表，并令 `CanCloseConnections=false`。本应独立可用的关闭全部按钮因此禁用。[帧上限](../../ClashSharp/ClashSharp/Service/MihomoControllerClient.cs#L627) · [页面错误处理](../../ClashSharp/ClashSharp/ViewModel/ConnectionsViewModel.cs#L289) · [不可用状态](../../ClashSharp/ClashSharp/ViewModel/ConnectionsViewModel.cs#L410) · [命令可用性](../../ClashSharp/ClashSharp/ViewModel/ConnectionsViewModel.cs#L185)

与此同时，普通 REST 路径通过默认整包缓冲再创建 JsonDocument，没有与服务侧一致的业务字节、行数和 token 预算。相同负载在两个接口中的行为不同。[REST 读取](../../ClashSharp/ClashSharp/Service/MihomoControllerClient.cs#L1139)

隔离探针构造 15,000 条连接、4,983,945 字节的同一合法 JSON：REST 成功解析 15,000 行，WebSocket 读取则抛出 `InvalidDataException`。探针使用内存 HTTP 与 WebSocket 端口，没有制造真实连接负载。

修复应把普通内核与服务内核的计数、明细完整性和命令能力收口到一致契约。保留资源上限，在明细无法显示时保留可信总数及关闭全部能力。REST 同样需要显式预算和降级，而非依赖 HttpClient 的通用缓冲上限。回归应从大帧经过客户端到连接页，检查按钮状态、累计计数与后续恢复。

## 功能与逻辑覆盖

以下记录本轮检查的功能入口、主要约束和结果。“未新增确认问题”仅表示这些已检查的路径未形成新的可复核发现，不构成对所有并发次序和 Windows 状态的无缺陷保证。

| 功能 | 检查的逻辑 | 结果与入口 |
| --- | --- | --- |
| 主实例与启动 | 主实例仲裁、窗口激活、安装事务阻断、启动步骤排序、恢复先于后台生产者 | 核对 App、StartupCoordinator、WindowsPrimaryInstanceBootstrap 和启动注册；未新增确认问题。[启动组成](../../ClashSharp/ClashSharp/AppHost/ClashSharpAppHostFactory.cs) |
| 启动失败 | 启动取消后等待已接受工作、诊断落盘、无主窗口关停、恢复锁保留 | 核对 ApplicationBootstrapper、ProcessLifetimeRunner、watchdog 协调；未新增确认问题。[进程生命周期](../../ClashSharp/ClashSharp.Application/Hosting/ProcessLifetimeRunner.cs) |
| 安全退出与重启 | 关闭准入、撤销排队工作、停止生产者、网络清理、释放仓库、重启进程 | 普通关停路径有明确所有者；清除全部数据的原生修正遗漏见 R01。[生命周期协调](../../ClashSharp/ClashSharp.Application/Lifecycle/RuntimeLifecycleCoordinator.cs) |
| 模式与接管 | Disabled、Standby、Rule、Global；应用和服务的唯一内核所有权；TUN 缺失与未知状态 | 核对转换门、原生回读、系统代理提交时机；末次统计见 R05，诊断端点见 R07。[接管](../../ClashSharp/ClashSharp/Service/NetworkTakeoverService.cs) |
| 配置事务 | 候选 YAML、内核预检、源和运行态提升、readiness、失败回滚、快照清理 | 当前事务保持原生基线；相关统计回调的身份问题见 R05。[配置事务](../../ClashSharp/ClashSharp/Service/CoreConfigurationService.RuntimeTransaction.cs) |
| Windows 系统代理 | 原值日志先于写入、字段级所有权、外部改写保留、崩溃恢复 | 核对 WindowsProxyService、OwnershipRestorer 和 watchdog；遗留同端口启发式仍需实机兼容性验收。[代理所有权](../../ClashSharp/ClashSharp/Service/WindowsProxyOwnershipRestorer.cs) |
| 配置目录 | 本地导入、激活、重命名、删除、删除活动配置的替换、目录落盘补偿 | 已安装身份用于读侧及写侧判定；资源迁移问题见 R02/R03。[目录](../../ClashSharp/ClashSharp/Service/ProfileCatalogService.cs) |
| 配置历史 | 源哈希、历史版本、预览、回滚、版本保留和清理失败 | 核对 stage、finalize、catalog 保存及补偿；未新增确认问题。[历史预览](../../ClashSharp/ClashSharp/Service/ProfileCatalogService.HistoryPreview.cs) |
| 订阅 | URL 身份、状态、用量、自动命名、超时、字节上限、自动重试、过期下载丢弃 | 下载与 FIFO 提交已分离，提交重查修订号；未重复列入已修复的旧问题。[调度](../../ClashSharp/ClashSharp/Service/ProfileSubscriptionScheduler.cs) |
| 节点与策略组 | 静态节点预览、运行态组与候选、选择权限、回读、持久化和补偿、跨配置恢复 | 核对 ProxySelectionService 与 Store；延迟是 TCP 可达性，不代表代理协议完整可用。[节点选择](../../ClashSharp/ClashSharp/Service/ProxySelectionService.cs) |
| provider 更新 | 按名称和类型路由、URL 与相对路径限制、HTTP 缓存、文件复制、资源摘要 | 本地文件源和迁移闭包见 R02/R03。[资源准备](../../ClashSharp/ClashSharp.MihomoService/MihomoRuntimeAssetStager.cs) |
| 规则 | YAML 预览、复杂语法、动作、命中计数、内置直接规则 | R04 和 R06；原配置交给内核执行的路径与预览错误区分。[规则目录](../../ClashSharp/ClashSharp/Service/RuleCatalogService.cs) |
| 连接 | REST、WebSocket、服务轮询、搜索、单条关闭、全部关闭、不可用与明细省略 | TUN 降级消费端已补齐；普通内核遗漏见 R09。[连接页模型](../../ClashSharp/ClashSharp/ViewModel/ConnectionsViewModel.cs) |
| 流量与统计 | 内核 epoch、累计增量、连接基线、配置身份、缺口持久化、日与节点汇总 | 配置错误目标归属已修复；R04/R05 仍在，短连接分项依赖采样完整性。[流量写入](../../ClashSharp/ClashSharp/Service/LogStorageService.Traffic.cs) |
| 日志与清理 | 日志来源、会话隔离、筛选、分页、导出、按时间/数量/空间清理、基线保留 | 核对参数化查询、事务及 ClearAll 保留累计计数基线；未新增确认问题。[日志存储](../../ClashSharp/ClashSharp/Service/LogStorageService.cs) |
| 触发器条件 | AND、阈值边沿、每日时间、总量修订号、上下文缺失与禁用状态 | 核对 TriggerMatcher 和 ContextProvider；未新增确认问题。[匹配器](../../ClashSharp/ClashSharp.Core/Domain/Triggers/TriggerMatcher.cs) |
| 触发器执行 | 定义修订、乐观冲突、latch 与 outbox 原子提交、Running 恢复、退出交接 | 明细省略后用可信总数探测关闭动作；未重复报告旧 C03。[动作执行](../../ClashSharp/ClashSharp.Application/Triggers/TriggerActionExecutor.cs) |
| 设置 | 注册表规范化、版本升级、Desired/Applied 分离、失败批次重试、组重置 | 核对会话、参与者和独立回读；原生诊断绕回 Desired 的遗留读取见 R07。[设置应用](../../ClashSharp/ClashSharp.Application/Settings/SettingsAuthoritySession.Application.cs) |
| 数据导入导出 | 包大小、文件数、解码上限、DTD 禁止、路径白名单、预览后不重开文件、代际提交 | 常规保护仍在；资源快照见 R03，清除外部状态见 R01。[包服务](../../ClashSharp/ClashSharp/Service/ClashDataPackageService.cs) |
| 数据替换恢复 | 排空、旧代际暂停、外部状态快照、manifest 决策、提交回包丢失、恢复专用准入 | 核对 GenerationReplacementCoordinator 与启动恢复；文件依赖闭包见 R03。[替换协调](../../ClashSharp/ClashSharp/AppHost/Data/GenerationReplacementCoordinator.cs) |
| Windows 原生修正 | 终端、WSL、Store；环境/豁免的原值保存、共同所有者、撤销 | R01/R07/R08；现有局部测试未验证完整数据清除或 NAT 连通性。[诊断服务](../../ClashSharp/ClashSharp/Service/WindowsNetworkDiagnosticService.cs) |
| 主控与网络诊断 | 运行态摘要、磁贴、模板顺序、DNS、速率、公网 IP、显式网站测试 | 核对已安装身份、后台采集和页面生命周期；未新增确认问题。[主控采集](../../ClashSharp/ClashSharp/Presentation/Adapters/MasterControlRuntimeSnapshotWork.cs) |
| 托盘与窗口 | 隐藏前可达性、Explorer 重启、状态唤醒、模式命令、关闭行为、位置保存 | 核对原子网络快照、事件注销和退出；真实通知区与多屏仍需指定候选验收。[托盘状态](../../ClashSharp/ClashSharp/Service/TrayStatusService.cs) |
| 通知 | 总开关、级别、前台降噪、连接变化、触发器收据、平台回退 | 自定义触发器通知没有再次发布普通通知事件，未确认直接自激循环；实际系统投递仍需验收。[通知](../../ClashSharp/ClashSharp/Service/NotificationService.cs) |
| 外观与本地化 | 主题色资源、语言设置、生效状态、八语言键、地区文本的展示边界 | 构建及现有测试覆盖通过；静态检查不能代替控件实际渲染和辅助技术验收。[外观](../../ClashSharp/ClashSharp/Service/AppThemeService.cs) |
| MCP 接入 | loopback、Host/Origin、Bearer、DPAPI、scope、参数封闭、重放收据、活动配置权限 | 核对本地认证、设置键白名单和目录锁内权限；未确认新的权限绕过。[HTTP 服务器](../../ClashSharp/ClashSharp.Infrastructure.Mcp/McpHttpServer.cs) |
| MCP 数据读取 | 敏感配置分权、历史摘要/详情分权、会话诊断、分页、日志脱敏和审计 | 未将普通状态权限扩展为原始配置读取；剪贴板配置路径排除历史与同步。[Agent 后端](../../ClashSharp/ClashSharp/AppHost/Agent/AppMcpAgentBackend.cs) |
| 服务 IPC | 用户 SID DACL、拒绝网络主体、固定时间认证、协议版本、请求相关性和帧预算 | 核对命名管道与控制器绑定；资源来源的代际错误见 R02。[IPC](../../ClashSharp/ClashSharp.MihomoService/MihomoServiceIpcServer.cs) |
| 特权内核 | LocalSystem 配置政策、独立校验、私有目录、Job、进程代际、readiness 与重启耗尽 | 当前限制控制器、监听器及多类本地文件面；源文件路径竞态的提权对抗测试未在本机执行。[配置政策](../../ClashSharp/ClashSharp.MihomoService/MihomoServiceConfigurationTrustValidator.cs) |
| 安装与修复 | 嵌入清单、载荷锁、签名与文件集、目标用户、同版本内容、证书、服务、最终验证 | 核对 durable phase 与 helper 状态回读；本机 13 项受保护原生用例未执行目标动作。[安装协调](../../ClashSharp/ClashSharp.Installer.Core/Execution/InstallerCoordinator.cs) |
| 卸载与所有权迁移 | 先服务后包、重试、原安装恢复、跨用户交接、退役卸载、目录账本清理 | 核对 request/release/owner 绑定与保留恢复状态；需要隔离提权环境完成最终验证。[所有权迁移](../../ClashSharp/ClashSharp.Installer.Core/Ownership/InstallerOwnerTransferCoordinator.cs) |
| 构建与发布 | SDK 固定、锁定依赖、警告即错误、工作流身份、签名候选、离线组件和源材料清单 | 本次只执行下表列出的本地验证；未把先前 CI 或旧候选作为本提交的实际部署证据。[CI](../../.github/workflows/ci.yml) |

## 前两轮发现的复核

此前报告用于定位已经变化的边界，本轮没有把旧基线的结论直接搬到当前提交。

| 旧项 | 当前复核结果 |
| --- | --- |
| A01/A02 | 配置内容写入使用锁内原生观测，活动配置写权限在相同边界判断；未重现原写入权限缺口。 |
| A03 | 订阅下载不再占有共享提交 FIFO；修订号和取消状态检查仍在。 |
| A04 | 更新检查的响应头与正文共用期限。 |
| A05/B05 | TUN 候选调用与服务同源的策略预检，服务保留独立检查。 |
| A06/B03/C03 | TUN 明细省略保留计数、关闭全部与触发器探测能力；R09 针对另一条普通内核路径。 |
| A07 | 订阅复用与编辑比较统一 URI 身份。 |
| A08 | 网络状态从同一次锁内观测返回，托盘监听网络生效证据。 |
| B01/B02 | 读侧改用已安装身份，样本身份随数据传入 SQLite；R05 是末次采样的剩余归属缺口，不是把流量写给失败目标的原问题。 |
| B04/C01/C02 | 服务按流扫描大响应并保存明细缺口；普通路径尚未收口，见 R09。 |

## 本地验证结果

使用固定 SDK `10.0.403`，路径为 `C:/Users/linzh/AppData/Local/Microsoft/dotnet-10.0.403`。产品构建和测试均使用 Release，格式检查设置 `Platform=x64`。本轮没有清理用户已有输出目录。

| 验证 | 实际结果 | 限制 |
| --- | --- | --- |
| Release 全解决方案构建 | 成功，0 警告、0 错误 | `--no-restore`，使用已有依赖还原结果。 |
| 主程序测试 | 4,958 通过、0 失败、0 跳过 | 约 4 分 53 秒。 |
| Installer Core | 1,141 通过、0 失败、0 跳过 | 本机执行。 |
| Installer Presentation | 240 通过、0 失败、0 跳过 | 本机执行。 |
| Installer Windows | 1,390 通过、13 失败、0 跳过 | 13 项在环境保护前置断言处失败，未执行所测证书或提权目录动作。 |
| 格式与分析器 | 返回 0，未发现需要格式化的文件 | 设计时工作区报告 WinUI 生成文件重复，详见下文。 |
| PowerShell 7 | 64 个脚本解析通过 | 不执行脚本内容。 |
| Windows PowerShell 5.1 | 64 个脚本解析通过 | 不执行脚本内容。 |
| 依赖漏洞查询 | `dotnet list ... --vulnerable --include-transitive --format json --no-restore` 返回 0，未列出漏洞 | 隔离探针构建另出现 NU1900 TLS/漏洞数据警告；不能据此签署依赖在最新公告下无漏洞。 |
| 隔离探针 | 上述 SQLite、规则解析、事务身份、清除、provider 和大响应结果均复现 | 无真实产品网络、提权服务或 WSL 连通性测试。 |

四份本机 TRX 合计 **7,729 通过、13 失败、0 跳过**。13 项分别属于 `WindowsCurrentUserCertificateStoreAdapterTests` 的 6 项和 `WindowsInstallerDirectoryCleanupNativeTests` 的 7 项。错误明确要求 disposable hosted CI / Windows Sandbox 或 isolated elevated Windows runner，本轮未设置绕过标记。

首次格式检查仅给出“加载工作区时遇到警告”。诊断级重跑将其定位到 WinUI `App.g.cs`、`App.g.i.cs` 及页面生成文件的重复来源；19 个项目均进入分析器步骤，结果显示 2,091 个设计时文件中需要格式化的文件为 0。该结果可支持“未发现格式修改”，但不应写成“工作区零警告”。

产品头部提交、源清点和每份 TRX 的实际计数与 SHA-256 已写入[证据摘要](../../artifacts/validation/release-static-audit-20261105/audit-evidence.json)。本轮没有用历史 CI 的 7,742 全绿数字覆盖本机 13 项前置条件失败。

## 本机证据与复现入口

以下文件位于被 Git 忽略的 `artifacts`，不会随本报告自动进入 Git；换机器应重新运行并归档证据。

- [源文件清单及摘要](../../artifacts/validation/release-static-audit-20261105/source-inventory.csv)
- [测试计数和 SHA-256](../../artifacts/validation/release-static-audit-20261105/audit-evidence.json)
- [Release 构建日志](../../artifacts/validation/release-static-audit-20261105/build.log)
- [主程序 TRX](../../artifacts/validation/release-static-audit-20261105/main/main.trx)、[Core TRX](../../artifacts/validation/release-static-audit-20261105/core/core.trx)、[展示层 TRX](../../artifacts/validation/release-static-audit-20261105/presentation/presentation.trx)、[Windows TRX](../../artifacts/validation/release-static-audit-20261105/windows/windows.trx)
- [格式诊断日志](../../artifacts/validation/release-static-audit-20261105/format-diagnostic.log)
- [隔离探针代码](../../artifacts/validation/release-static-audit-20261105/probe/Program.cs)、[探针项目](../../artifacts/validation/release-static-audit-20261105/probe/AuditProbe.csproj)、[探针输出](../../artifacts/validation/release-static-audit-20261105/probe.log)

探针使用真实 SQLite、直接链接的清除类，以及测试程序集内直接链接的生产源。配置事务的 RecordingRuntime、内存环境变量和 HTTP/WebSocket 仅作为外部能力替身。临时目录保留供复核；探针不会操作真实代理环境。

## 修复与发布验收顺序

1. **先处理恢复义务。** 修复 R01，在外部修改恢复或明确保留恢复义务后才允许删除数据。测试不允许以“清除完成但代理残留”作为成功。
2. **统一配置与资源的代际。** 分别修复 R02 和 R03。加入从旧数据、全量重置、备份导入到服务 materializer 的完整资源字节验证，确保旧目录中存在不同内容时也不会被误用。
3. **修正原生网络诊断。** 修复 R07/R08，覆盖失败端口目标、TUN、停止状态、WSL NAT 和 mirrored。把“配置写入完成”和“网络连通”分开。
4. **修正统计和规则解释。** 修复 R04/R05/R06。使用生产目录、采样事务及真实数据库连接测试，防止以伪造字典键或手工身份值掩盖跨组件断层。
5. **收口连接能力契约。** 修复 R09，在普通与 TUN 两个运行所有者下验证大响应、累计计数、明细省略、关闭全部和恢复。
6. **在同一产品提交上完成验证。** 重新执行 Release 构建与四套测试；13 项原生用例在受支持的隔离提权环境运行。另需独立的当前依赖公告及 Mihomo 二进制安全扫描、签名与载荷检查，以及指定候选的安装、卸载、恢复和 GUI 验收。

本轮静态审查和隔离验证没有发现足以确认新的 P0 或远程提权的证据。发布前仍应保留针对特权文件源替换、进程退出、管道端点冒充和安装中断的对抗测试；静态路径校验与模拟端口通过不能替代这些 Windows 原生边界的验证。
