# Settings generation cutover

版本保持 `1.0.0`。完整切换在 `feat/settings-generation` 分支和[草稿 PR #5](https://github.com/Water-Run/ClashSharp/pull/5) 推进，基础提交为 `e3f597c`。当前已实现迁移、异步设置会话、应用状态流转、代际内服务访问、公共异步入口，以及 StartupTask、Sampling 的实际服务适配器。控制端凭据已从偏好中拆分并接入生产启动、运行时和数据清理；生产偏好仍使用现有设置入口。页面写入、全部运行时参与者和 profile/log/trigger 仓库寿命需要一起接入后，才替换临时架构门禁并合入 main。

## 已实现的存储与迁移

`WindowsLegacySettingsSource` 在读取时才访问 LocalSettings，只请求 registry 的规范键和旧别名。它没有写入接口，未注册字段和控制端凭据不会进入迁移快照。`LegacySettingsSnapshot` 复制允许的不可变原始类型，严格匹配键大小写，限制字符串长度，并为排序后的偏好计算稳定摘要。

`SettingsMigrationPlanner` 将旧整数枚举转换为规范枚举值，验证范围和类型，保留地区模式旧布尔别名及组合模式拆分的实际行为。缺失值采用声明的默认值，非法值采用安全回退。初始 envelope 的 desired 与 key revision 为 1，记录 migration identity 和输入摘要；所有 applied 都是未观察状态，由唯一待办覆盖，迁移本身不声称 Windows 或运行时已应用这些值。

`SettingsAuthorityBootstrapper` 先打开指定代际的仓库，只在已确认空仓库时读取旧偏好。已有 JSON 权威优先；并发初始化采用经过重读验证的获胜提交。损坏、不可用和回执丢失均保留其持久分类，后续会话重新打开实际文件来确定结果。

修复了一个持久化缺口：主文件和备份被隔离为 `.corrupt.<guid>` 后，下一次打开曾将两个原文件名缺失误判为空仓库。仓库现在识别顶层、精确命名的隔离证据，继续返回 Corrupt；有效主文件或备份仍优先处理。两项回归先复现错误的 Succeeded，再验证修复。

## 应用状态与恢复

`SettingsApplicationBatchEditor` 是纯状态编辑器。开始应用时保留 desired 和批次身份，先将该批次记录为 Running，并撤销其旧生效证据。完成需要匹配 generation、batch、attempt，以及完整规范键、类型、值、revision 和摘要；部分结果、旧 attempt 和其他代际的结果不能清除待办。失败保留 Failed 和 unknown applied；重试显式分配新的 attempt identity。

`SettingsAuthoritySession` 在一个代际内串行执行异步修改和应用。持久 Running 确认前允许取消；确认后持有原许可，等待探测、效果及最终保存结束。参与者先探测，目标已生效时直接记录验证；需要应用时使用显式不可变目标，返回后再次探测。应用回执丢失但实际目标已验证时记录 `settings.application.reply_lost_resolved`。探测失败不授权盲目应用，致命异常图继续向上传递。

最终保存失败会使内存投影失效，调用者必须重读仓库。并发文件写入返回真实获胜 envelope，完整保留其他设置修改；Running 待办可在重开后先探测，再决定是否需要效果。会话退休拒绝新命令，并等待已开始的效果和保存完成。

`PrepareStartupAdmittedAsync` 需要独占许可，撤销上一进程留下的 verified applied，创建必要的观察待办。已有 Running 和 Failed 身份继续保留；明确阻止的外部探测保留其处理方式。Restart 批次在 Live 阶段保持待处理，必须由独占启动阶段应用。

## 公共异步入口

`ISettingsAuthority` 与 `GenerationSettingsAuthority` 为页面、磁贴和触发器提供同一条完整命令路径：进入许可、解析当前代际的 `SettingsGenerationContext`、提交完整 desired、依次验证受影响的批次，然后释放代际租约和许可。上下文中的 session 必须匹配租约的完整 descriptor，错误装配在打开其他代际仓库之前被拒绝。快照返回不可变 envelope 和 generation identity，调用者不持有活仓库引用。

复原偏好、失败重试和启动重新观察也经过该入口。多个参与者中后续失败时，前面已验证的证据保留；重试只处理具有新 attempt identity 的失败批次。排队命令在开始前复制输入，外部集合随后变化不会更改已提交的命令内容。

新增回归复现了完整命令的一个衔接缺口：desired 已经提交后，退出开始排空并撤销等待许可，原 session 的普通入口会取消随后的运行时应用。现在 facade 使用内部的已提交命令续行路径，继续验证原许可的有效性，持有原代际，完成全部参与者和保存；后续排队命令仍受撤销控制。直接调用 session 的普通批次入口继续遵守原有 Running 提交前取消规则。

## StartupTask 与 Sampling 的实际服务适配

`StartupTaskSettingsParticipant` 和 `SamplingSettingsParticipant` 在访问运行时之前检查完整 generation descriptor、应用类别、允许的键和原许可的有效性。两者都不写偏好、不重新申请普通许可。StartupTask 通过生产 `StartupLaunchService` 读取 Windows 注册状态；已满足目标时不重复注册，拒绝或未知状态保留待办。应用回执丢失由后续独立平台探测判断。

`ConnectionSamplingService` 现在串行拥有完整的配置与生命周期转换。运行中的循环使用已经安装的间隔；偏好变化不会在下一轮采样中提前生效。显式配置入口等待旧循环及未完成的采样结束，再同时安装启用状态和间隔，随后启动新循环。该入口开始排空后不会因页面取消而放弃任务。探测读取实际循环和已安装的间隔，不访问旧偏好；永久停止的服务不能被报告为已启用。只修改一个采样键时，适配器从同一不可变请求读取配套值，暂停、恢复及重新启用也使用该完整配置。

新增回归使用真实 JSON 会话、生产启动服务和采样 supervisor，Windows、mihomo 及统计存储边界采用隔离模拟。测试覆盖启用、禁用、拒绝后显式重试、回执丢失、错误代际和许可、单键修改、旧循环排空与退出许可关闭，以及尚未应用的 desired 不改变实际间隔。它们没有修改开发机启动任务或访问实际 mihomo。

另外修复了启动服务的异常分类：探测和设置入口先检查完整异常图，嵌套致命异常保持原异常传播，不能返回未知状态或包装成 `StartupLaunchUpdateException`。两项回归先复现旧行为，再验证修复；持久 Running 记录保留给后续进程重新观察。

## 独立控制端凭据

`AppSettingsService` 及其 editor 不再生成、读取或删除控制端凭据，核心配置偏好端口也不再携带 secret。`IControllerCredentialProvider` 只读取启动时已验证的进程凭据；HTTP、WebSocket 和配置生成使用同一提供者，独占设置操作期间的读取不访问存储、不获取新许可。生产主机拥有 `ControllerCredentialService`，现有静态运行时工厂通过显式启动绑定访问它。

`WindowsControllerCredentialStore` 只访问原来的 `MihomoControllerSecret` 槽位，不枚举偏好或私有未知值。存在且合法的旧凭据原样保留；缺失或类型、格式非法时生成 256 位随机凭据，写入后独立重读一致才发布。存储不可用或结果无法验证时阻止启动，不产生临时内存替代凭据；丢失写入回执但实际值已经匹配时完成初始化。异常只对外报告稳定代码，致命异常图继续传播。此 Windows API 使用主程序的包身份，符合[系统管理应用数据的适用范围](https://learn.microsoft.com/en-us/windows/apps/develop/data/store-and-retrieve-app-data)。

新增启动步骤顺序为 140，位于 Installer 事务门禁（125）之后、运行时恢复（150）之前。构造过程没有存储副作用，初始化失败时不绑定运行时消费者。主机释放会撤销进程中的可用凭据投影。

普通偏好重置保留凭据。清除全部数据先停止运行时，再通过独立维护能力删除并确认槽位不存在，随后清理其他数据。进入终态后使用 shutdown maintenance 许可；偏好删除已经开始时，页面取消不会中断后续凭据和文件清理。凭据删除无法确认会使清理失败并停止后续文件删除，避免声称已全部清除。凭据服务的释放发生在主机停止及数据维护之后。

当前生产装配已接入这项拆分；JSON 偏好权威和数据代际整体切换仍未激活。实际打包候选的启动验收将在对应 CI 包产出后执行。

## 代际服务寿命

`DataGenerationManager.ExecuteAsync` 在取得代际租约后，从该代际拥有的 `IServiceProvider` 解析服务，并等待完整操作结束才释放租约。`ReadSnapshot` 仅用于同步、无 I/O 的不可变内存快照，不阻塞异步任务。服务容器的异步释放仍由 `DataGenerationScope` 的原生命周期协议负责。

真实 JSON 仓库与 manifest 集成回归确认：进行中的设置操作阻止切换；提交后只解析新会话，旧会话拒绝写入；回滚恢复原会话并释放候选会话；解析或操作失败释放租约。生产 profile/log/trigger 的容器装配及导入、重置切换尚待完成。

## 本地验证与 CI

- 18 项目 Release x64 完整构建通过，零警告、零错误，用时 26.33 秒。
- 主程序 2705 项通过，零失败、零跳过，用时 49 秒；本分支新增 84 项回归。
- 完整 format 加载 1459 个文件，最终检查零处变更。
- 记录保存在 `artifacts/verification/1.0.0-settings-generation-main.trx`、`build-settings-generation-complete.log`、`format-settings-generation-verified.log` 和 `local-validation-settings-generation-foundation.json`。

基础提交 `2775afe` 的[两项 CI 均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34211372829)。实际下载并核验四份 TRX，共 4770 项通过、零失败、零跳过，其中新增 84 项均实际执行。PR 合并提交为 `8febfaf`，其 tree 与 `2775afe` 相同，摘要为 `d325bf6e849ec6edc13602388ae2b8860bc901f4`；收据为 `ci-validation-settings-generation-foundation.json` 和 `ci-settings-generation-foundation-regressions.json`。开发包构建成功，完整生产切换后的候选再执行原生验收。

公共入口追加 9 项回归后，主程序 2714 项全部通过，零失败、零跳过，用时 50 秒；18 项目完整构建零警告、零错误，用时 26.51 秒，format 检查 1464 个文件、零处变更。本分支累计新增 93 项回归。收据为 `local-validation-settings-generation-facade.json`、`1.0.0-settings-facade-main.trx` 及同前缀的构建、格式日志。首次红测有一项许可撤销问题和一项夹具对预建代际目录的错误假设，均保留原报告；错误装配验证以 Settings 目录未创建为实际边界。

公共入口提交 `a08fc4b` 的[两项 CI 均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34214328575)，实际下载的四份 TRX 共 4779 项通过，零失败、零跳过。合并提交 `e31dd2c` 与源提交的 tree 同为 `78fe7a3dd89e81d4ba3203ca5dbb16ab4d6b4e35`，验证收据为 `ci-validation-settings-generation-facade.json`。开发安装器包构建成功，尚未为这份包追加原生验收。

实际服务适配和启动异常修复新增 16 项回归，本分支累计新增 109 项。最终完整主程序 2730 项通过，零失败、零跳过，用时 51 秒；18 项目 Release x64 构建零警告、零错误，用时 22.29 秒，format 检查 1473 个文件、零处变更。收据为 `local-validation-settings-generation-runtime.json`、`1.0.0-settings-runtime-final.trx`、`build-settings-runtime-final.log` 和 `format-settings-runtime-verified.log`。两项异常图红测保存在 `1.0.0-settings-runtime-fatal-red.trx`；此前夹具对初始 revision 和匹配 applied/pending 的错误构造也保留独立失败报告，不计为产品缺陷复现。

运行时适配提交 `37f4c51` 的[两项 CI 均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34217135381)，实际四份 TRX 共 4795 项通过、零失败、零跳过。合并提交 `95ee8d8` 与源提交的 tree 同为 `a85ebf2b51881d3a38f3e9321100edec68c4b014`，收据为 `ci-validation-settings-generation-runtime.json`。安装器包构建成功，此份包仅核验构建结果及元数据。

凭据拆分验证包含 30 项新用例，并将原先设置类中的凭据删除回归替换为独立数据维护职责的检查，净增 29 项。本分支主程序累计净增 138 项，完整 2759 项通过，零失败、零跳过，用时 52 秒；18 项目构建零警告、零错误，用时 27.21 秒，format 检查 1488 个文件、零处变更。定向 99 项通过；收据为 `local-validation-controller-credentials.json`、`1.0.0-controller-credentials-main.trx`、`build-controller-credentials-complete.log` 和 `format-controller-credentials-verified.log`。首次定向验证的退出夹具重复提交已经终态的许可，调整为可提交退出的独占许可后通过，原报告保留。

持久中断测试使用真实临时仓库、切点注入及新对象重开，运行时参与者为受控模拟。Windows 旧设置适配器已编译，未在开发机读取实际 LocalSettings。实际打包应用的迁移、进程崩溃、完整页面和安装器兼容验收将在生产切换后执行。开发机代理摘要保持 `95e97918ff6de70655b412568cd18dc81c5d6584c607bb9a71ddc72e22460447`。

## 完整切换的剩余依赖

1. 将偏好写入统一为应用层异步 change set；页面、磁贴、触发器和网络提交者使用同一个接口。独立控制端凭据已接入生产调用，后续代际重置继续使用该能力。
2. 完成 Internal、Appearance、Network、Triggers 的实际 apply/probe 适配器，并将已实现的 StartupTask、Sampling 一起装配；明确读取 desired、有效状态和待办的消费者。
3. 在设置驱动的启动步骤之前完成旧事务恢复、代际打开和偏好迁移。profile/log/trigger 与 settings 必须由同一代际容器解析、排空和替换。
4. 将导入、重置和回滚接入候选代际及 manifest 提交，完成生产消费者替换后，原子替换 `SettingsAuthorityArchitectureTests` 中的临时门禁。
5. 运行新候选的 CI、打包应用及隔离 Windows 验收，再将完整节点推送 main。
