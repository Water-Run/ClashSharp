# ClashSharp 发布审查问题修复

本轮基于 `d4dd30d` 落实[发布级静态审查](2026-11-05-release-static-review.md)的 R01–R09。修复同时覆盖恢复义务、配置资源的代际、统计身份、规则语法、原生诊断与连接响应降级。源码与审查文档一并提交到 main，验证产物保存在被 Git 忽略的本地 artifacts。未进行部署，原有 `docs/handoff-2026-11-05.md` 保留在本地。

## 修复结果

| 问题 | 当前行为 | 主要入口与回归 |
| --- | --- | --- |
| R01 | 清除数据先恢复所有仍由应用拥有的原生环境变量和 Store 豁免，逐项回读，再删除凭据及数据。恢复失败保留日志并阻止删除；诊断写入持有准入租约，清除必须等其排空。 | [清除工厂](../../ClashSharp/ClashSharp/AppHost/Data/GenerationDataClearOperationFactory.cs)、[原生恢复](../../ClashSharp/ClashSharp/Service/WindowsNetworkDiagnosticService.cs)、[跨清除回归](../../ClashSharp/ClashSharp.Tests/Unit/Services/ReleaseAuditRegressionTests.cs) |
| R02 | 服务配置源和本地 provider 资源都按同一个 DataGenerationId 解析。资源根由固定安装路径和不透明代际 ID 计算，不接受客户端指定路径。 | [源路径解析](../../ClashSharp/ClashSharp.MihomoService/MihomoServiceSourcePaths.cs)、[materializer](../../ClashSharp/ClashSharp.MihomoService/MihomoEffectiveConfigurationMaterializer.cs)、[新旧目录诱饵测试](../../ClashSharp/ClashSharp.Tests/Unit/MihomoService/MihomoEffectiveConfigurationMaterializerTests.cs) |
| R03 | 迁移、代际快照和备份共用可携带文件规则，保留自定义 provider 目录。生成配置、运行态快照、内核 cache.db 及私有事务旁文件不进入新代际。 | [文件规则](../../ClashSharp/ClashSharp.Core/Data/MihomoUserFilePolicy.cs)、[仓库快照](../../ClashSharp/ClashSharp.Infrastructure/Data/UserDataRepositorySnapshot.cs)、[首次及已有代际回归](../../ClashSharp/ClashSharp.Tests/Integration/LegacyDataGenerationPreparerTests.cs) |
| R04 | 新增按已安装配置身份隔离的规则观测表，使用统一条件键关联运行时与页面。类型名、MATCH 和逻辑表达式统一处理；重复条件或无法可靠识别的规则显示计数不可确定。 | [条件身份](../../ClashSharp/ClashSharp.Core/Domain/Rules/RuleObservationKey.cs)、[采样入库](../../ClashSharp/ClashSharp/Service/LogStorageService.Traffic.cs)、[规则目录](../../ClashSharp/ClashSharp/Service/RuleCatalogService.cs)、[真实 SQLite 回归](../../ClashSharp/ClashSharp.Tests/Unit/Services/ReleaseAuditRegressionTests.cs) |
| R05 | 配置所有者在提升 live 文件之前完成末次采样，传入经核验的旧配置身份。采样排队、计数增量及 SQLite 提交仍串行，事务中的普通观测继续保持保守语义。 | [配置事务](../../ClashSharp/ClashSharp/Service/CoreConfigurationService.RuntimeTransaction.cs)、[网络采样边界](../../ClashSharp/ClashSharp/Service/NetworkTakeoverService.DataScope.cs)、[配置事务与采样入库回归](../../ClashSharp/ClashSharp.Tests/Unit/Services/RuntimeConfigurationTransaction.StatisticsTests.cs) |
| R06 | 规则按外层字段解析，保留完整逻辑条件、正则逗号和普通进程路径。无法明确解析时显示原始规则，不制造动作或命中计数。 | [规则语法](../../ClashSharp/ClashSharp.Core/Domain/Rules/MihomoRuleSyntax.cs)、[两条预览路径](../../ClashSharp/ClashSharp/Service/MihomoProfilePreviewParser.Semantic.cs)、[复杂规则与路径测试](../../ClashSharp/ClashSharp.Tests/Unit/Services/MihomoProfileParserServiceTests.cs) |
| R07 | 原生修正读取独立网络观测中的实际端口。内核停止或所有权未知时不写入代理变量；TUN 下恢复旧环境修正，避免写入已关闭的本地 HTTP 端口。 | [运行态适配](../../ClashSharp/ClashSharp/Service/WindowsNetworkDiagnosticServiceFactory.cs)、[诊断服务](../../ClashSharp/ClashSharp/Service/WindowsNetworkDiagnosticService.cs)、[端口及 TUN 回归](../../ClashSharp/ClashSharp.Tests/Unit/Services/ReleaseAuditRegressionTests.cs) |
| R08 | 识别已注册的默认 WSL1，或通过 wslinfo 查询实际模式。WSL1 和 mirrored 可使用桥接配置；NAT、未识别模式或查询失败不会被报告为已配置成功，也不写入代理变量。八种语言提供处理提示。 | [模式判断](../../ClashSharp/ClashSharp/Service/WindowsNetworkDiagnosticService.cs)、[注册信息读取](../../ClashSharp/ClashSharp/Service/WindowsNetworkDiagnosticServiceFactory.cs)、[NAT、未知、WSL1、mirrored 回归](../../ClashSharp/ClashSharp.Tests/Unit/Services/ReleaseAuditRegressionTests.cs) |
| R09 | 普通 REST、连接 WebSocket 和服务 broker 共用有界扫描器。明细超限时保留可信累计计数与总连接数，向连接页返回已有的类型化省略结果，继续允许关闭全部。其他 REST 文档增加明确字节预算和正文期限。 | [公共扫描器](../../ClashSharp/ClashSharp.Core/ServiceProtocol/MihomoConnectionResponseReader.cs)、[客户端](../../ClashSharp/ClashSharp/Service/MihomoControllerClient.cs)、[大响应回归](../../ClashSharp/ClashSharp.Tests/Unit/Services/MihomoControllerClient.LargeConnectionTests.cs) |

## 数据与兼容性

规则页的新计数使用 `DataGenerationId + AppliedGeneration + AppliedContentHash` 作为配置身份。SQLite 幂等创建 `ObservedRuleHits`，保留原有统计和历史。无法可靠确认归属的旧记录不会被自动分配到新配置；同一条件对应多条配置规则时，运行时观测不足以判断具体是哪一行，界面以破折号表示未知。

普通连接响应沿用服务侧的明确预算：最大扫描 64 MiB、单 token 64 KiB、JSON 深度 32；完整明细还受 512 KiB、2,048 行及节点预算限制。REST 响应头和正文共用最多五秒期限，连接 WebSocket 的单条消息也有限时。超出整份文档的扫描预算仍返回不可用；明细省略不会伪装成完整的空列表。

服务 wire 协议仍为 v4，没有新增客户端提供文件路径的能力。后续候选必须同时包含更新后的应用与服务，才能获得 R02 的完整修复。

WSL 模式分支已经对照[微软 wslinfo 实现](https://github.com/microsoft/WSL/blob/master/src/linux/init/wslinfo.cpp)及[发行版 Version 属性](https://github.com/microsoft/WSL/blob/master/src/windows/service/exe/DistributionRegistration.h)核对。逻辑规则计数对应的是仓库锁定内核的实际 Payload 表示，已对照[Mihomo v1.19.33 逻辑规则实现](https://github.com/MetaCubeX/mihomo/blob/v1.19.33/rules/logic/logic.go)核对。这里的核对不等于已经在真实 WSL 或安装服务中完成运行验收。

## 验证边界

本轮使用固定 SDK 10.0.403、Release x64 构建，并保留针对性和完整测试的 TRX。测试中的环境变量、Store 操作及大响应均使用隔离端口；真实 SQLite、配置事务、文件日志、代际目录和客户端扫描逻辑直接执行生产实现。

最终本地结果如下，验证期间源码指纹保持不变：

| 检查 | 结果 |
| --- | --- |
| 全解决方案 Release 构建 | 0 警告、0 错误 |
| 完整主程序测试 | 4,982 通过、0 失败、0 跳过 |
| Installer Core | 1,141 通过、0 失败、0 跳过 |
| Installer Presentation | 240 通过、0 失败、0 跳过 |
| Installer Windows 安全子集 | 1,390 通过、0 失败、0 跳过 |
| 合计实际执行 | 7,753 通过、0 失败、0 跳过 |
| 全解决方案格式检查 | 返回 0，需格式修改的文件为 0；设计时仍有原有 WinUI 生成文件重复加载警告 |
| PowerShell 7 与 5.1 语法 | 各 64 个脚本，0 语法错误 |

主程序执行用例从基线 4,958 项增至 4,982 项。第一批针对性 313 项通过后，补充了最终异常分类、进程路径与逻辑规则原生表示的回归，并对最终源码重新运行完整主程序测试及安装器三套测试。

Windows 安装器中要求 disposable CI / Windows Sandbox 或隔离提权 runner 的 13 项原生用例未在本机运行，仍需在相应环境验收。没有修改真实代理环境、证书或服务，没有进行签名打包及蓝机部署。

完整计数、测试报告摘要及源码指纹收录于[本地验证证据](../../artifacts/validation/release-static-fixes-20261105/evidence-summary.json)；本地 artifacts 被 Git 忽略，不会随文档自动进入 Git。
